using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MsTeamsLocal;

/// <summary>
/// Long-lived UI Automation engine that observes the Teams meeting toolbar and
/// performs control actions in-process. Running in-process (instead of spawning
/// a PowerShell + loading UIA on every poll/press) is what makes presses instant
/// and keeps the meeting state stable instead of flickering "offline".
/// </summary>
public sealed class TeamsAutomation : IDisposable
{
    private const int PollIntervalMs = 600;
    private const int MissThreshold = 3;        // consecutive misses before declaring "no meeting"
    private const int OptimisticHoldMs = 1500;  // honor an optimistic toggle this long

    private static readonly string[] TeamsProcessNames = { "ms-teams", "msteams", "Teams" };

    private readonly object _uia = new();        // serialize UIA work across poll + actions
    private readonly object _stateGate = new();

    private AutomationElement? _meetingWindow;
    private int _missCount;

    // Cache of toolbar control elements for the current meeting window. The element
    // identity is stable while the window lives, so we avoid a fresh descendant tree
    // walk for every control on every poll and every press.
    private readonly Dictionary<string, AutomationElement> _controls = new(StringComparer.Ordinal);

    // Last-known raw control state (preserved across transient read failures).
    private bool _teamsRunning;
    private bool _meetingActive;
    private bool _muted;
    private bool _cameraOff;
    private bool _handRaised;
    private bool _sharing;
    private bool _initialized;

    // Optimistic overrides keyed by control, with an expiry tick.
    private long _muteHoldUntil;
    private long _cameraHoldUntil;
    private long _handHoldUntil;

    private Thread? _pollThread;
    private volatile bool _running;

    public event Action<TeamsSnapshot>? SnapshotChanged;

    public TeamsSnapshot Current { get; private set; } = TeamsSnapshot.Empty;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "teams-uia-poll" };
        _pollThread.Start();
    }

    public void Dispose()
    {
        _running = false;
        try { _pollThread?.Join(1000); } catch { }
    }

    // ---- Polling ------------------------------------------------------------

    private void PollLoop()
    {
        while (_running)
        {
            try { PollOnce(); }
            catch (Exception ex) { Log.Error("poll failed", ex); }
            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>Run a single poll immediately (used right after a press for fast feedback).</summary>
    public void PollNow()
    {
        try { PollOnce(); } catch (Exception ex) { Log.Error("PollNow failed", ex); }
    }

    private void PollOnce()
    {
        bool teamsRunning = IsTeamsRunning();

        bool meetingActive;
        bool muted, cameraOff, handRaised, sharing;

        lock (_uia)
        {
            var window = ResolveMeetingWindow(teamsRunning);
            if (window is null)
            {
                // No meeting found this cycle. Debounce before flipping to inactive
                // so a single transient UIA hiccup never knocks the icons offline.
                _missCount++;
                meetingActive = _meetingActive && _missCount < MissThreshold;
                muted = _muted; cameraOff = _cameraOff; handRaised = _handRaised; sharing = _sharing;
            }
            else
            {
                _missCount = 0;
                meetingActive = true;
                // Read each control; keep the previous value if a read fails/returns unknown.
                muted = ReadBool(window, "microphone-button", "unmute") ?? _muted;
                cameraOff = ReadBool(window, "video-button", "camera on") ?? _cameraOff;
                handRaised = ReadBool(window, "raisehands-button", "lower") ?? _handRaised;
                sharing = ReadBool(window, "share-button", "stop") ?? _sharing;
            }
        }

        long now = Environment.TickCount64;
        lock (_stateGate)
        {
            _teamsRunning = teamsRunning;
            _meetingActive = meetingActive;

            // Apply real reads unless an optimistic hold is still active for that control.
            if (now >= _muteHoldUntil) _muted = muted;
            if (now >= _cameraHoldUntil) _cameraOff = cameraOff;
            if (now >= _handHoldUntil) _handRaised = handRaised;
            _sharing = sharing;
            _initialized = true;
        }

        Publish();
    }

    private void Publish()
    {
        TeamsSnapshot snap;
        lock (_stateGate)
        {
            snap = new TeamsSnapshot
            {
                Initialized = _initialized,
                TeamsRunning = _teamsRunning,
                MeetingActive = _meetingActive,
                Muted = _muted,
                CameraOff = _cameraOff,
                HandRaised = _handRaised,
                Sharing = _sharing,
            };
        }

        if (snap == Current) return;
        Current = snap;
        try { SnapshotChanged?.Invoke(snap); }
        catch (Exception ex) { Log.Error("SnapshotChanged handler threw", ex); }
    }

    // ---- Meeting window resolution -----------------------------------------

    private AutomationElement? ResolveMeetingWindow(bool teamsRunning)
    {
        if (!teamsRunning) { _meetingWindow = null; return null; }

        // Revalidate the cached window cheaply (scoped search inside it).
        if (_meetingWindow is not null)
        {
            try
            {
                if (GetControl(_meetingWindow, "hangup-button") is not null) return _meetingWindow;
            }
            catch { /* window gone */ }
            _meetingWindow = null;
            _controls.Clear();
        }

        var found = FindMeetingWindow();
        if (found is not null)
        {
            _meetingWindow = found;
            _controls.Clear(); // new window -> control elements are new
        }
        return _meetingWindow;
    }

    /// <summary>Get a toolbar control by automationId, reusing a cached element when still alive.</summary>
    private AutomationElement? GetControl(AutomationElement window, string id)
    {
        if (_controls.TryGetValue(id, out var cached))
        {
            try { _ = cached.Current.ControlType; return cached; } // cheap liveness probe
            catch { _controls.Remove(id); }
        }
        var found = FindIn(window, id);
        if (found is not null) _controls[id] = found;
        return found;
    }

    private static AutomationElement? FindMeetingWindow()
    {
        var teamsPids = GetTeamsPids();
        if (teamsPids.Count == 0) return null;

        AutomationElement.RootElement.GetType(); // touch to ensure UIA init
        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        foreach (AutomationElement window in windows)
        {
            try
            {
                if (!teamsPids.Contains(window.Current.ProcessId)) continue;
                var hangup = FindIn(window, "hangup-button");
                if (hangup is not null) return window;
            }
            catch { /* skip windows that vanish mid-enumeration */ }
        }
        return null;
    }

    private static AutomationElement? FindIn(AutomationElement root, string automationId)
        => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    /// <summary>Returns true/false if the control's name contains <paramref name="keyword"/>, or null if unreadable.</summary>
    private bool? ReadBool(AutomationElement window, string automationId, string keyword)
    {
        try
        {
            var el = GetControl(window, automationId);
            if (el is null) return null;
            var name = el.Current.Name;
            if (string.IsNullOrEmpty(name)) return null;
            return name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return null; }
    }

    // ---- Process detection --------------------------------------------------

    private static bool IsTeamsRunning() => GetTeamsPids().Count > 0;

    private static HashSet<int> GetTeamsPids()
    {
        var pids = new HashSet<int>();
        foreach (var name in TeamsProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }
        }
        return pids;
    }

    // ---- Actions ------------------------------------------------------------

    /// <summary>
    /// Immediately reflect the expected new state for toggles so the key updates
    /// without waiting for the next real read (fixes the "unmute icon lag").
    /// </summary>
    public void ApplyOptimistic(ActionDescriptor d)
    {
        long until = Environment.TickCount64 + OptimisticHoldMs;
        lock (_stateGate)
        {
            switch (d.Kind)
            {
                case ActionKind.ToggleMute: _muted = !_muted; _muteHoldUntil = until; break;
                case ActionKind.ToggleCamera: _cameraOff = !_cameraOff; _cameraHoldUntil = until; break;
                case ActionKind.RaiseHand: _handRaised = !_handRaised; _handHoldUntil = until; break;
                default: return;
            }
        }
        Publish();
    }

    /// <summary>Perform the control action in-process. Returns true on success.</summary>
    /// <param name="restoreFocus">When true, restore the window stacking if Teams steals focus.</param>
    public bool Trigger(ActionDescriptor d, bool restoreFocus = true)
    {
        // Snapshot the active window AND the window stacking so we can put both back if
        // Teams jumps forward. The foreground window is tracked separately because the
        // topmost z-order window may be an always-on-top widget, not what the user was using.
        IntPtr previousForeground = restoreFocus ? GetForegroundWindow() : IntPtr.Zero;
        IntPtr[] windowOrder = restoreFocus ? CaptureWindowOrder() : Array.Empty<IntPtr>();
        bool result = false;
        try
        {
            lock (_uia)
            {
                var window = ResolveMeetingWindow(IsTeamsRunning());
                if (window is null) return false;

                result = d.Kind switch
                {
                    ActionKind.ToggleMute => InvokeControl(window, "microphone-button"),
                    ActionKind.ToggleCamera => InvokeControl(window, "video-button"),
                    ActionKind.RaiseHand => InvokeControl(window, "raisehands-button"),
                    ActionKind.ShareScreen => InvokeControl(window, "share-button"),
                    ActionKind.Leave => InvokeControl(window, "hangup-button"),
                    ActionKind.Reaction => SendReaction(window, d.ReactionAutomationId!),
                    _ => false,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Trigger {d.Id} failed", ex);
            result = false;
        }
        finally
        {
            if (restoreFocus)
                RestoreWindowOrder(windowOrder, previousForeground);
        }
        return result;
    }

    private bool InvokeControl(AutomationElement window, string id)
    {
        var el = GetControl(window, id);
        if (el is null) { Log.Warn($"control not found: {id}"); return false; }
        try { return Invoke(el); }
        catch
        {
            // Cached element went stale between poll and press; re-find once.
            _controls.Remove(id);
            var fresh = FindIn(window, id);
            if (fresh is null) return false;
            _controls[id] = fresh;
            return Invoke(fresh);
        }
    }

    private static bool Invoke(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var pat))
        {
            ((InvokePattern)pat).Invoke();
            return true;
        }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog))
        {
            ((TogglePattern)tog).Toggle();
            return true;
        }
        return false;
    }

    private bool SendReaction(AutomationElement window, string reactionId)
    {
        var react = GetControl(window, "reaction-menu-button");
        if (react is null) { Log.Warn("reaction-menu-button not found"); return false; }

        // Open the flyout (React supports ExpandCollapse, not Invoke).
        ExpandCollapsePattern? expand = null;
        if (react.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecp))
        {
            expand = (ExpandCollapsePattern)ecp;
            try { expand.Expand(); } catch { }
        }
        else
        {
            Invoke(react); // fallback
        }

        // Poll briefly for the reaction item (it lives in a popup, not the meeting window
        // subtree). Only search AFTER expanding, and break the moment it appears.
        var idCond = new PropertyCondition(AutomationElement.AutomationIdProperty, reactionId);
        AutomationElement? target = null;
        for (int i = 0; i < 25 && target is null; i++)
        {
            try { target = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, idCond); }
            catch { }
            if (target is null) Thread.Sleep(15);
        }

        bool ok = false;
        if (target is not null) ok = Invoke(target);
        else Log.Warn($"reaction item not found: {reactionId}");

        CloseReactionFlyout(expand);
        return ok;
    }

    private static void CloseReactionFlyout(ExpandCollapsePattern? expand)
    {
        try
        {
            if (expand is not null) { expand.Collapse(); return; }
        }
        catch { /* fall through to ESC */ }

        // Fallback: only Escape if Teams holds focus, so we never send a stray Escape
        // into whatever app the user is working in.
        try { if (IsForegroundTeams()) SendEscape(); } catch { }
    }

    // ---- Win32 interop ------------------------------------------------------

    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static void SendEscape()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool IsForegroundTeams()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return GetTeamsPids().Contains((int)pid);
    }

    /// <summary>
    /// Return focus to the window that was active before we acted, if Teams stole it.
    /// Uses AttachThreadInput to bypass the foreground lock without synthetic keystrokes.
    /// </summary>
    private static void RestoreForeground(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        var current = GetForegroundWindow();
        if (current == target || current == IntPtr.Zero) return; // focus didn't change

        uint currentThread = GetWindowThreadProcessId(current, out _);
        uint targetThread = GetWindowThreadProcessId(target, out _);
        uint thisThread = GetCurrentThreadId();
        bool attachedCurrent = false, attachedTarget = false;
        try
        {
            if (thisThread != currentThread) attachedCurrent = AttachThreadInput(thisThread, currentThread, true);
            if (thisThread != targetThread) attachedTarget = AttachThreadInput(thisThread, targetThread, true);
            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        catch (Exception ex) { Log.Error("RestoreForeground failed", ex); }
        finally
        {
            if (attachedCurrent) AttachThreadInput(thisThread, currentThread, false);
            if (attachedTarget) AttachThreadInput(thisThread, targetThread, false);
        }
    }

    // ---- Window z-order capture / restore -----------------------------------

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int nNumWindows);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(
        IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    /// <summary>Snapshot the visible, non-minimized top-level windows in z-order (topmost first).</summary>
    private static IntPtr[] CaptureWindowOrder()
    {
        var list = new List<IntPtr>(64);
        try
        {
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && !IsIconic(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("CaptureWindowOrder failed", ex); }
        return list.ToArray();
    }

    /// <summary>
    /// Reapply a previously captured z-order so the exact window stacking from before the
    /// action is restored, then re-activate the window that was actually focused. No-op when
    /// the foreground never changed.
    /// </summary>
    private static void RestoreWindowOrder(IntPtr[] order, IntPtr foreground)
    {
        if (order.Length == 0 && foreground == IntPtr.Zero) return;

        // Reapply the captured stacking if the z-order actually changed. We compare the full
        // order (not just the foreground window) because Teams can raise the meeting window
        // above other windows even after focus has already returned to the user's window.
        var current = CaptureWindowOrder();
        if (order.Length > 0 && !SameOrder(order, current))
        {
            try
            {
                var hdwp = BeginDeferWindowPos(order.Length);
                if (hdwp != IntPtr.Zero)
                {
                    // Synchronous (no SWP_ASYNCWINDOWPOS) so the stack ends up in the exact order.
                    const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
                    IntPtr insertAfter = HWND_TOP;
                    foreach (var hwnd in order) // top -> bottom
                    {
                        hdwp = DeferWindowPos(hdwp, hwnd, insertAfter, 0, 0, 0, 0, flags);
                        if (hdwp == IntPtr.Zero) break;
                        insertAfter = hwnd;
                    }
                    if (hdwp != IntPtr.Zero) EndDeferWindowPos(hdwp);
                }
            }
            catch (Exception ex) { Log.Error("RestoreWindowOrder failed", ex); }
        }

        // Re-activate the window that actually had focus (also restores keyboard input).
        if (foreground != IntPtr.Zero && GetForegroundWindow() != foreground)
            RestoreForeground(foreground);
    }

    private static bool SameOrder(IntPtr[] a, IntPtr[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
