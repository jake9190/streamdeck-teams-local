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
    // Poll cadence. While a meeting (or the pre-join screen) is live we poll quickly so state
    // changes (mute/camera/hand/share) reflect promptly. When idle we back off substantially:
    // the idle case is the norm (Teams running, no meeting), and each idle poll must probe the
    // Teams window for a meeting toolbar, which is the expensive part.
    private const int ActivePollIntervalMs = 600;
    private const int IdlePollIntervalMs = 2500;
    private const int MissThreshold = 3;        // consecutive misses before declaring "no meeting"
    private const int OptimisticHoldMs = 1500;  // honor an optimistic toggle this long
    private const int SelfTileRefindCooldownMs = 3000; // min gap between full self-video-tile searches after a miss
    private const int PressLockTimeoutMs = 3500;     // abandon a press if the UIA lock isn't free within this
    private const int ReactionSearchBudgetMs = 1500; // max wall-clock spent hunting for a reaction flyout item
    private const int AudioDeviceSearchBudgetMs = 700; // max wall-clock per device selection (bounds the UIA lock hold)
    private const int RaiseSettlePollMs = 60;        // poll interval while waiting for Teams' async raise to settle
    private const int RaiseSettleStable = 3;         // consecutive stable polls that mean Teams stopped raising
    private const int RaiseSettleMaxMs = 1200;       // give up waiting for the raise/settle after this

    private static readonly string[] TeamsProcessNames = { "ms-teams", "msteams", "Teams" };

    private readonly object _uia = new();        // serialize UIA work across poll + actions
    private readonly object _stateGate = new();

    private AutomationElement? _meetingWindow;
    private AutomationElement? _selfVideo; // local participant's video tile (for hand-raised reads)
    private long _selfVideoRetryAfter;     // tick before which we skip re-walking for a missing self tile
    private bool _windowIsPreJoin;         // whether the resolved window is the pre-join screen
    private int _missCount;

    // Cache of toolbar control elements for the current meeting window. The element
    // identity is stable while the window lives, so we avoid a fresh descendant tree
    // walk for every control on every poll and every press.
    private readonly Dictionary<string, AutomationElement> _controls = new(StringComparer.Ordinal);

    // Last-known raw control state (preserved across transient read failures).
    private bool _teamsRunning;
    private bool _meetingActive;
    private bool _preJoin;
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
        if (_comAutomation is not null)
        {
            try { Marshal.FinalReleaseComObject(_comAutomation); } catch { }
            _comAutomation = null;
        }
    }

    // ---- Polling ------------------------------------------------------------

    private void PollLoop()
    {
        while (_running)
        {
            try { PollOnce(); }
            catch (Exception ex) { Log.Error("poll failed", ex); }

            // Fast cadence only while something meeting-like is live; otherwise back off so the
            // idle process barely uses CPU and the UIA probes happen far less often.
            var s = Current;
            int delay = (s.MeetingActive || s.PreJoin) ? ActivePollIntervalMs : IdlePollIntervalMs;
            Thread.Sleep(delay);
        }
    }

    /// <summary>Run a single poll immediately (used right after a press for fast feedback).</summary>
    public void PollNow()
    {
        try { PollOnce(); } catch (Exception ex) { Log.Error("PollNow failed", ex); }
    }

    private void PollOnce()
    {
        var teamsPids = GetTeamsPids();
        bool teamsRunning = teamsPids.Count > 0;

        bool meetingActive, preJoin;
        bool muted, cameraOff, handRaised, sharing;

        lock (_uia)
        {
            var window = ResolveMeetingWindow(teamsPids);
            if (window is null)
            {
                // No meeting found this cycle. Debounce before flipping to inactive
                // so a single transient UIA hiccup never knocks the icons offline.
                _missCount++;
                bool live = _missCount < MissThreshold;
                meetingActive = _meetingActive && live;
                preJoin = _preJoin && live;
                muted = _muted; cameraOff = _cameraOff; handRaised = _handRaised; sharing = _sharing;
            }
            else
            {
                _missCount = 0;
                preJoin = _windowIsPreJoin;
                meetingActive = !preJoin;
                if (preJoin)
                {
                    // Pre-join mic/camera buttons have dynamic ids, so locate them by Name. The
                    // state keywords ("unmute" / "camera on") match the in-meeting wording.
                    muted = ReadNamedBool(window, "mute mic", "unmute") ?? _muted;
                    cameraOff = ReadNamedBool(window, "turn camera", "camera on") ?? _cameraOff;
                    handRaised = false;
                    sharing = false;
                }
                else
                {
                    muted = ReadBool(window, "microphone-button", "unmute") ?? _muted;
                    cameraOff = ReadBool(window, "video-button", "camera on") ?? _cameraOff;
                    sharing = ReadBool(window, "share-button", "stop") ?? _sharing;
                    // The raise-hand button Name never changes, so hand state is read from the
                    // self-video tile's Name (it gains "Hand raised" when up).
                    handRaised = ReadHandRaised(window) ?? _handRaised;
                }
            }
        }

        long now = Environment.TickCount64;
        lock (_stateGate)
        {
            _teamsRunning = teamsRunning;
            _meetingActive = meetingActive;
            _preJoin = preJoin;

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
                PreJoin = _preJoin,
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

    private AutomationElement? ResolveMeetingWindow(HashSet<int> teamsPids)
    {
        if (teamsPids.Count == 0) { _meetingWindow = null; return null; }

        // Revalidate the cached window cheaply (scoped search inside it).
        if (_meetingWindow is not null)
        {
            try
            {
                if (_windowIsPreJoin)
                {
                    // Revalidate by the structural signal (the "Join now" button), since the
                    // pre-join window title isn't always "Meeting join…".
                    if (IsPreJoinWindow(_meetingWindow))
                        return _meetingWindow;
                }
                else if (IsActiveMeetingWindow(_meetingWindow))
                {
                    return _meetingWindow;
                }
            }
            catch { /* window gone */ }
            _meetingWindow = null;
            _controls.Clear();
        }

        var found = FindMeetingWindow(teamsPids);
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

    private bool IsActiveMeetingWindow(AutomationElement window) =>
        GetControl(window, "hangup-button") is not null
        || GetControl(window, "microphone-button") is not null
        || GetControl(window, "video-button") is not null;

    private AutomationElement? FindMeetingWindow(HashSet<int> teamsPids)
    {
        if (teamsPids.Count == 0) return null;

        var (hwnd, preJoin) = FindMeetingHwndViaCom(teamsPids);
        if (hwnd == IntPtr.Zero) return null;

        _windowIsPreJoin = preJoin;
        try { return AutomationElement.FromHandle(hwnd); }
        catch (Exception ex) { Log.Error("FromHandle failed for meeting window", ex); return null; }
    }

    /// <summary>
    /// Locates the meeting (or pre-join) window handle using the COM UI Automation client, releasing
    /// every transient element it touches. This is the hot path: it runs on every idle poll to look
    /// for a meeting that usually isn't there. The managed <c>System.Windows.Automation</c> client
    /// leaks native memory under continuous descendant searches, so this detection deliberately uses
    /// the COM client (whose elements we free with <see cref="Marshal.ReleaseComObject"/>) and only
    /// materializes a managed <see cref="AutomationElement"/> once an actual meeting window is found.
    /// Window enumeration is done with cheap Win32 calls (no UIA) and each candidate is probed with a
    /// single descendant search for either the in-meeting hangup button or the pre-join "Join now"
    /// button.
    /// </summary>
    private (IntPtr hwnd, bool preJoin) FindMeetingHwndViaCom(HashSet<int> teamsPids)
    {
        var hwnds = GetTopLevelWindows(teamsPids);
        if (hwnds.Count == 0) return (IntPtr.Zero, false);

        var automation = ComAutomation;
        object? hangupCond = null, microphoneCond = null, videoCond = null;
        object? meetingCond = null, meetingOrVideoCond = null, prejoinCond = null, anyCond = null;
        IntPtr preJoinHwnd = IntPtr.Zero;
        try
        {
            hangupCond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "hangup-button");
            microphoneCond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "microphone-button");
            videoCond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "video-button");
            meetingCond = automation.CreateOrCondition(hangupCond, microphoneCond);
            meetingOrVideoCond = automation.CreateOrCondition(meetingCond, videoCond);
            prejoinCond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "prejoin-join-button");
            anyCond = automation.CreateOrCondition(meetingOrVideoCond, prejoinCond);

            foreach (var hwnd in hwnds)
            {
                IUIAutomationElement? container = null;
                IUIAutomationElement? match = null;
                try
                {
                    container = automation.ElementFromHandle(hwnd);
                    if (container is null) continue;

                    match = container.FindFirst(UiaTreeScope.Descendants, anyCond);
                    if (match is null)
                    {
                        // Some pre-join variants render "Join now" without the AutomationId; fall back
                        // to the conventional window title so we still detect the pre-join screen.
                        if (preJoinHwnd == IntPtr.Zero && WindowTitleStartsWith(hwnd, "Meeting join"))
                            preJoinHwnd = hwnd;
                        continue;
                    }

                    var aid = match.GetCurrentPropertyValue(UIA_AutomationIdPropertyId) as string;
                    if (string.Equals(aid, "prejoin-join-button", StringComparison.Ordinal))
                        preJoinHwnd = hwnd;   // remember pre-join, keep scanning for a real meeting
                    else
                        return (hwnd, false); // any active-meeting toolbar signal always wins
                }
                catch { /* window vanished mid-probe */ }
                finally
                {
                    if (match is not null) Marshal.ReleaseComObject(match);
                    if (container is not null) Marshal.ReleaseComObject(container);
                }
            }
        }
        catch (Exception ex) { Log.Error("COM meeting window search failed", ex); }
        finally
        {
            if (anyCond is not null) Marshal.ReleaseComObject(anyCond);
            if (prejoinCond is not null) Marshal.ReleaseComObject(prejoinCond);
            if (meetingOrVideoCond is not null) Marshal.ReleaseComObject(meetingOrVideoCond);
            if (meetingCond is not null) Marshal.ReleaseComObject(meetingCond);
            if (videoCond is not null) Marshal.ReleaseComObject(videoCond);
            if (microphoneCond is not null) Marshal.ReleaseComObject(microphoneCond);
            if (hangupCond is not null) Marshal.ReleaseComObject(hangupCond);
        }

        return preJoinHwnd == IntPtr.Zero ? (IntPtr.Zero, false) : (preJoinHwnd, true);
    }

    /// <summary>Visible top-level windows owned by one of the given Teams processes (Win32 only).</summary>
    private static List<IntPtr> GetTopLevelWindows(HashSet<int> pids)
    {
        var list = new List<IntPtr>(16);
        try
        {
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h))
                {
                    GetWindowThreadProcessId(h, out uint wpid);
                    if (pids.Contains((int)wpid)) list.Add(h);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("EnumWindows failed", ex); }
        return list;
    }

    private static bool WindowTitleStartsWith(IntPtr hwnd, string prefix)
    {
        try
        {
            Span<char> buf = stackalloc char[256];
            int len = GetWindowTextW(hwnd, ref MemoryMarshal.GetReference(buf), buf.Length);
            if (len <= 0) return false;
            ReadOnlySpan<char> title = buf[..len];
            return title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// True if <paramref name="window"/> is the Teams pre-join ("Meeting join") screen. Detected by
    /// the "Join now" button (prejoin-join-button), which is present regardless of the window title.
    /// </summary>
    private static bool IsPreJoinWindow(AutomationElement window)
    {
        try
        {
            if (FindIn(window, "prejoin-join-button") is not null) return true;
        }
        catch { }
        // Fallback to the conventional title for any UI variant lacking that AutomationId.
        try
        {
            return (window.Current.Name ?? "").StartsWith("Meeting join", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    /// <summary>
    /// Reads whether the local hand is raised. Teams does not expose this on the raise-hand button
    /// (its Name never changes), but the self-video tile's Name gains "Hand raised" when up. Returns
    /// null when the self tile can't be found, so the caller keeps the last/optimistic value.
    /// </summary>
    private bool? ReadHandRaised(AutomationElement window)
    {
        try
        {
            var tile = SelfVideoTile(window);
            if (tile is null) return null;
            var name = tile.Current.Name;
            if (string.IsNullOrEmpty(name)) return null;
            return name.IndexOf("hand raised", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { _selfVideo = null; return null; }
    }

    /// <summary>
    /// The local participant's video tile (an Image whose Name starts with "Myself video"). Cached
    /// and re-validated cheaply by name; only re-found (a scoped Image search) when the cache goes
    /// stale, so the per-poll cost is one property read in the common case.
    /// </summary>
    private AutomationElement? SelfVideoTile(AutomationElement window)
    {
        if (_selfVideo is not null)
        {
            try
            {
                if (_selfVideo.Current.Name.StartsWith("Myself video", StringComparison.OrdinalIgnoreCase))
                    return _selfVideo;
            }
            catch { }
            _selfVideo = null;
        }

        // The self tile is often absent (camera off, no video), so a full descendant Image walk
        // would otherwise run on every poll for the whole meeting. Back off between misses so a
        // missing tile costs nothing on most polls.
        if (Environment.TickCount64 < _selfVideoRetryAfter) return null;

        try
        {
            var images = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image));
            foreach (AutomationElement img in images)
            {
                try
                {
                    if (img.Current.Name.StartsWith("Myself video", StringComparison.OrdinalIgnoreCase))
                    {
                        _selfVideo = img;
                        return img;
                    }
                }
                catch { }
            }
        }
        catch { }

        _selfVideoRetryAfter = Environment.TickCount64 + SelfTileRefindCooldownMs;
        return null;
    }

    /// <summary>
    /// Finds a descendant control whose Name contains <paramref name="nameSubstring"/> (used for the
    /// pre-join screen, whose mic/camera/device controls have dynamic AutomationIds but stable Names).
    /// The pre-join mic/camera controls are toggle switches (CheckBox), not Buttons, so several
    /// interactive control types are searched.
    /// </summary>
    private static AutomationElement? FindButtonByName(AutomationElement window, string nameSubstring)
    {
        try
        {
            var scan = window.FindAll(TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
            foreach (AutomationElement b in scan)
            {
                try
                {
                    var n = b.Current.Name;
                    if (!string.IsNullOrEmpty(n) && n.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return b;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Reads a named button's state keyword (pre-join mute/camera), or null if not found.</summary>
    private static bool? ReadNamedBool(AutomationElement window, string locatorSubstring, string keyword)
    {
        try
        {
            var el = FindButtonByName(window, locatorSubstring);
            var name = el?.Current.Name;
            if (string.IsNullOrEmpty(name)) return null;
            return name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return null; }
    }

    /// <summary>
    /// Actuates a control located by Name (pre-join screen). These controls are web "switch"
    /// elements whose MSAA default action is a no-op, so we set focus and use the Toggle/Invoke
    /// pattern (this takes focus, which is acceptable on the pre-join screen).
    /// </summary>
    private bool ActuateNamed(AutomationElement window, IntPtr hwnd, string nameSubstring)
    {
        var el = FindButtonByName(window, nameSubstring);
        if (el is null) { Log.Warn($"control not found by name: {nameSubstring}"); return false; }
        try
        {
            Log.Info($"actuate named '{nameSubstring}' -> name='{el.Current.Name}' aid='{el.Current.AutomationId}' type={el.Current.ControlType.ProgrammaticName}");
            try { el.SetFocus(); } catch { }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog))
            {
                ((TogglePattern)tog).Toggle();
                Log.Info($"  actuated '{nameSubstring}' via Toggle");
                return true;
            }
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            {
                ((InvokePattern)inv).Invoke();
                Log.Info($"  actuated '{nameSubstring}' via Invoke");
                return true;
            }
            // Last resort: focus-free MSAA default action.
            var aid = el.Current.AutomationId;
            if (!string.IsNullOrEmpty(aid) && hwnd != IntPtr.Zero)
            {
                bool com = ComActuate(hwnd, aid);
                Log.Info($"  actuated '{nameSubstring}' via Com={com}");
                return com;
            }
            // No actionable pattern and no id (e.g. the pre-join "open speaker/microphone options"
            // buttons): fall back to a real mouse click at the control (focus is acceptable here).
            bool clicked = ClickElement(el);
            Log.Info($"  actuated '{nameSubstring}' via Click={clicked}");
            return clicked;
        }
        catch (Exception ex) { Log.Error($"actuate named {nameSubstring} failed", ex); return false; }
    }


    // ---- Process detection --------------------------------------------------

    private static readonly HashSet<string> TeamsProcessNameSet =
        new(TeamsProcessNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PIDs of the running Teams processes, via a single Toolhelp process snapshot. This is far
    /// cheaper than <c>Process.GetProcessesByName</c> (which enumerates every process on the system
    /// and allocates a managed <c>Process</c> — a handle open plus perf read — for each, once per
    /// name) and it runs on every poll, so the cost matters.
    /// </summary>
    private static HashSet<int> GetTeamsPids()
    {
        var pids = new HashSet<int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return pids;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    var exe = entry.szExeFile;
                    int dot = exe.LastIndexOf('.');
                    var bare = dot > 0 ? exe[..dot] : exe; // strip ".exe" to match the bare names
                    if (TeamsProcessNameSet.Contains(bare))
                        pids.Add((int)entry.th32ProcessID);
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        catch (Exception ex) { Log.Error("process snapshot failed", ex); }
        finally { CloseHandle(snapshot); }
        return pids;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ---- Audio device selection ---------------------------------------------

    /// <summary>
    /// Switches the meeting's microphone and/or speaker to the given Windows device ids (each
    /// optional -- null leaves that device unchanged). Opens Teams' audio options panel, selects
    /// the matching device list items by AutomationId (which equals the Core Audio endpoint id),
    /// then closes the panel. Returns true if at least one device was selected.
    /// </summary>
    public bool SetAudioDevices(string? micDeviceId, string? speakerDeviceId)
    {
        if (string.IsNullOrEmpty(micDeviceId) && string.IsNullOrEmpty(speakerDeviceId))
        {
            Log.Warn("audio device action has no mic or speaker configured");
            return false;
        }

        // Resolve the Windows friendly names up front (outside the UIA lock, since Core Audio
        // enumeration is unrelated to UIA) so selection can fall back to matching a device list item
        // by Name when Teams no longer stamps the endpoint id on the item's AutomationId.
        var (micName, speakerName) = ResolveDeviceNames(micDeviceId, speakerDeviceId);

        if (!Monitor.TryEnter(_uia, PressLockTimeoutMs))
        {
            Log.Warn("SetAudioDevices timed out waiting for the UIA lock; abandoning");
            return false;
        }
        try
        {
            try
            {
                var window = ResolveMeetingWindow(GetTeamsPids());
                if (window is null) return false;

                // The pre-join screen has no combined audio panel: it has separate "open microphone
                // options" / "open speaker options" buttons that each open a device list.
                if (_windowIsPreJoin)
                    return SetAudioDevicesPreJoin(window, (IntPtr)window.Current.NativeWindowHandle, micDeviceId, speakerDeviceId, micName, speakerName);

                var configure = FindIn(window, "audio-button-configure");
                if (configure is null) { Log.Warn("audio-button-configure not found"); return false; }

                // Open the audio options panel (it is a toggle/expand control).
                ExpandCollapsePattern? ec = null;
                if (configure.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecp))
                {
                    ec = (ExpandCollapsePattern)ecp;
                    try { ec.Expand(); } catch { }
                }
                else
                {
                    Invoke(configure);
                }

                bool any = false;
                if (!string.IsNullOrEmpty(speakerDeviceId)) any |= SelectAudioDevice(window, speakerDeviceId!, speakerName);
                if (!string.IsNullOrEmpty(micDeviceId)) any |= SelectAudioDevice(window, micDeviceId!, micName);

                // Close the panel.
                try
                {
                    if (ec is not null) ec.Collapse();
                    else Invoke(configure);
                }
                catch { }

                return any;
            }
            catch (Exception ex) { Log.Error("SetAudioDevices failed", ex); return false; }
        }
        finally { Monitor.Exit(_uia); }
    }

    /// <summary>
    /// On the pre-join screen, opens the separate microphone/speaker option menus and selects the
    /// configured device in each (the device list items carry the same AutomationId as in-meeting).
    /// Each picker is a toggle panel that stays open after selection, so it is closed by re-clicking
    /// its button.
    /// </summary>
    private bool SetAudioDevicesPreJoin(AutomationElement window, IntPtr hwnd, string? micId, string? speakerId, string? micName, string? speakerName)
    {
        bool any = false;
        if (!string.IsNullOrEmpty(speakerId))
            any |= SelectPreJoinDevice(window, hwnd, "open speaker options", speakerId!, speakerName);
        if (!string.IsNullOrEmpty(micId))
            any |= SelectPreJoinDevice(window, hwnd, "open microphone options", micId!, micName);
        return any;
    }

    /// <summary>Opens a pre-join device picker, selects the configured device, then closes the picker.</summary>
    private bool SelectPreJoinDevice(AutomationElement window, IntPtr hwnd, string buttonName, string deviceId, string? deviceName)
    {
        if (!ActuateNamed(window, hwnd, buttonName)) return false;
        Log.Info($"prejoin select '{buttonName}' id={deviceId}");
        bool selected = SelectAudioDevice(window, deviceId, deviceName);
        // The picker is a toggle panel that remains open after a selection; re-click to close it.
        Thread.Sleep(80);
        ActuateNamed(window, hwnd, buttonName);
        return selected;
    }

    /// <summary>Candidate control types for a device list entry (used by both the name-fallback
    /// search and the diagnostic dump).</summary>
    private static readonly Condition DeviceItemCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));

    /// <summary>
    /// Resolves the Windows friendly names for the requested endpoint ids in a single Core Audio
    /// enumeration, so device selection can match a list item by Name when the endpoint-id
    /// AutomationId is absent.
    /// </summary>
    private static (string? micName, string? speakerName) ResolveDeviceNames(string? micId, string? speakerId)
    {
        string? micName = null, speakerName = null;
        try
        {
            foreach (var e in AudioEndpoints.Enumerate())
            {
                if (!string.IsNullOrEmpty(micId) && string.Equals(e.Id, micId, StringComparison.OrdinalIgnoreCase)) micName = e.Name;
                if (!string.IsNullOrEmpty(speakerId) && string.Equals(e.Id, speakerId, StringComparison.OrdinalIgnoreCase)) speakerName = e.Name;
            }
        }
        catch (Exception ex) { Log.Error("resolve device names failed", ex); }
        return (micName, speakerName);
    }

    /// <summary>
    /// Selects the audio device list item for <paramref name="deviceId"/>. Matches first by
    /// AutomationId (historically the Core Audio endpoint id) and — because newer Teams builds no
    /// longer stamp the endpoint id on the item — falls back to a menu/list item whose Name contains
    /// the device's Windows friendly name. Bounded by a wall-clock budget so a missing device never
    /// holds the shared UIA lock long enough to make other Stream Deck presses time out.
    /// </summary>
    private bool SelectAudioDevice(AutomationElement window, string deviceId, string? deviceName)
    {
        var item = FindAudioDeviceItem(window, deviceId, deviceName);
        if (item is null)
        {
            Log.Warn($"audio device not found in panel: {deviceId} name='{deviceName}'");
            DumpMenuItems();
            return false;
        }

        try
        {
            Log.Info($"select audio device -> name='{item.Current.Name}' aid='{item.Current.AutomationId}' type={item.Current.ControlType.ProgrammaticName}");
            // A real click selects the item AND dismisses the picker flyout (the programmatic
            // Select/Invoke patterns leave the flyout open). Fall back to the patterns if the item
            // has no on-screen point.
            if (ClickElement(item)) return true;
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sip))
            {
                ((SelectionItemPattern)sip).Select();
                return true;
            }
            return Invoke(item);
        }
        catch (Exception ex) { Log.Error($"select audio device failed: {deviceId}", ex); return false; }
    }

    /// <summary>
    /// Locates the audio device list item within a bounded wall-clock budget. The panel renders
    /// asynchronously, so the search is retried until the deadline; it prefers an exact AutomationId
    /// match (meeting-window subtree first, then the desktop, since the picker can open as a popup
    /// outside the window) and falls back to a menu/list/radio item whose Name contains the device's
    /// friendly name.
    /// </summary>
    private AutomationElement? FindAudioDeviceItem(AutomationElement window, string deviceId, string? deviceName)
    {
        var idCond = new PropertyCondition(AutomationElement.AutomationIdProperty, deviceId);
        long deadline = Environment.TickCount64 + AudioDeviceSearchBudgetMs;
        do
        {
            try { if (window.FindFirst(TreeScope.Descendants, idCond) is { } inWindow) return inWindow; }
            catch { }
            try { if (AutomationElement.RootElement.FindFirst(TreeScope.Descendants, idCond) is { } onDesktop) return onDesktop; }
            catch { }

            if (!string.IsNullOrEmpty(deviceName))
            {
                var byName = FindDeviceItemByName(window, deviceName!)
                             ?? FindDeviceItemByName(AutomationElement.RootElement, deviceName!);
                if (byName is not null) return byName;
            }

            Thread.Sleep(25);
        }
        while (Environment.TickCount64 < deadline);
        return null;
    }

    /// <summary>Finds a menu/list/radio item under <paramref name="root"/> whose Name contains
    /// <paramref name="deviceName"/> (the Windows friendly name Teams shows for the device).</summary>
    private static AutomationElement? FindDeviceItemByName(AutomationElement root, string deviceName)
    {
        try
        {
            var items = root.FindAll(TreeScope.Descendants, DeviceItemCondition);
            foreach (AutomationElement e in items)
            {
                try
                {
                    var n = e.Current.Name;
                    if (!string.IsNullOrEmpty(n) && n.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return e;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Logs every MenuItem/ListItem/RadioButton/ComboBox currently visible under the desktop
    /// (used to diagnose which control identifies the device picker entries when selection fails).</summary>
    private static void DumpMenuItems()
    {
        try
        {
            var scan = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
            Log.Info($"--- device picker dump: {scan.Count} item(s) ---");
            foreach (AutomationElement e in scan)
            {
                try { Log.Info($"  item aid='{e.Current.AutomationId}' name='{e.Current.Name}' type={e.Current.ControlType.ProgrammaticName}"); }
                catch { }
            }
            Log.Info("--- device picker dump end ---");
        }
        catch (Exception ex) { Log.Error("device picker dump failed", ex); }
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
        bool locked = false;
        try
        {
            // Bound how long a press waits for the shared UIA lock. If the engine is busy (e.g. a
            // slow UIA search is holding it), abandon the press rather than let it queue and fire
            // much later — a Stream Deck press should act promptly or not at all.
            locked = Monitor.TryEnter(_uia, PressLockTimeoutMs);
            if (!locked)
            {
                Log.Warn($"Trigger {d.Id} timed out waiting for the UIA lock; abandoning press");
                return false;
            }
            {
                var window = ResolveMeetingWindow(GetTeamsPids());
                if (window is null) return false;

                // On the pre-join screen the mic/camera buttons have dynamic ids, so locate them by
                // Name; everything else uses the stable in-meeting AutomationIds.
                bool preJoin = _windowIsPreJoin;
                IntPtr hwnd = (IntPtr)window.Current.NativeWindowHandle;

                result = d.Kind switch
                {
                    ActionKind.ToggleMute => preJoin
                        ? ActuateNamed(window, hwnd, "mute mic")
                        : InvokeControl(window, "microphone-button"),
                    ActionKind.ToggleCamera => preJoin
                        ? ActuateNamed(window, hwnd, "turn camera")
                        : InvokeControl(window, "video-button"),
                    ActionKind.RaiseHand => InvokeControl(window, "raisehands-button"),
                    ActionKind.ShareScreen => InvokeControl(window, "share-button"),
                    ActionKind.Leave => LeaveMeeting(window),
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
            if (locked) Monitor.Exit(_uia);
            if (restoreFocus)
            {
                // Opening the reaction flyout unavoidably foregrounds Teams, and Teams re-raises its
                // window asynchronously (sometimes more than once). Restoring inline just races that
                // raise — the stack flashes back to correct, then Teams pops forward again. So for
                // reactions we restore on a background thread after Teams settles; every other action
                // is focus-free and restores inline (a no-op when nothing moved).
                if (d.Kind == ActionKind.Reaction)
                    RestoreWindowOrderDeferred(windowOrder, previousForeground);
                else
                    RestoreWindowOrder(windowOrder, previousForeground);
            }
        }
        return result;
    }

    private bool InvokeControl(AutomationElement window, string id)
    {
        // Actuate via the focus-free MSAA default action so toolbar toggles never steal focus
        // or foreground Teams (managed InvokePattern.Invoke does both on Teams' Chromium controls).
        IntPtr hwnd = (IntPtr)window.Current.NativeWindowHandle;
        if (hwnd == IntPtr.Zero) { Log.Warn($"meeting window has no handle for {id}"); return false; }
        return ComActuate(hwnd, id);
    }

    private bool LeaveMeeting(AutomationElement window)
    {
        if (InvokeControl(window, "hangup-button")) return true;

        var leave = GetControl(window, "hangup-button");
        if (leave is null) { Log.Warn("leave control not found"); return false; }

        bool clicked = ClickElement(leave);
        Log.Info($"actuated leave via Click={clicked}");
        return clicked;
    }

    // ---- Focus-free MSAA actuation (COM UI Automation) ----------------------

    private const int UIA_AutomationIdPropertyId = 30011;
    private const int UIA_LegacyIAccessiblePatternId = 10018;
    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private IUIAutomation? _comAutomation;

    private IUIAutomation ComAutomation =>
        _comAutomation ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_CUIAutomation)!)!;

    /// <summary>
    /// Finds the control by AutomationId within <paramref name="containerHwnd"/> and actuates it
    /// with the focus-free MSAA default action (LegacyIAccessible.DoDefaultAction).
    /// </summary>
    private bool ComActuate(IntPtr containerHwnd, string automationId)
    {
        try
        {
            var automation = ComAutomation;
            var container = automation.ElementFromHandle(containerHwnd);
            if (container is null) return false;
            object cond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, automationId);
            var el = container.FindFirst(UiaTreeScope.Descendants, cond);
            if (el is null) { Log.Warn($"control not found: {automationId}"); return false; }
            return ComDoDefaultAction(el);
        }
        catch (Exception ex) { Log.Error($"actuate {automationId} failed", ex); return false; }
    }

    private static bool ComDoDefaultAction(IUIAutomationElement el)
    {
        if (el.GetCurrentPattern(UIA_LegacyIAccessiblePatternId)
            is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();
            return true;
        }
        return false;
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

        // Poll briefly for the reaction item (it lives in a popup, not the meeting window subtree),
        // then actuate it with the focus-free MSAA default action via the COM client. Managed
        // InvokePattern.Invoke foregrounds Teams (its focus change is async and races the window-
        // order restore in Trigger, leaving Teams stuck in front), so we use the same focus-free
        // path as the toolbar controls. Bounded by a wall-clock budget, stops the moment the item
        // is found (whether or not it actuated) so a slow desktop provider can't hold the lock, and
        // every COM element is released.
        bool ok = false;
        var automation = ComAutomation;
        object? idCond = null;
        IUIAutomationElement? root = null;
        try
        {
            idCond = automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, reactionId);
            root = automation.GetRootElement();
            long deadline = Environment.TickCount64 + ReactionSearchBudgetMs;
            while (!ok && Environment.TickCount64 < deadline)
            {
                IUIAutomationElement? target = null;
                try { target = root.FindFirst(UiaTreeScope.Descendants, idCond); }
                catch { }
                if (target is not null)
                {
                    try { ok = ComDoDefaultAction(target); }
                    finally { Marshal.ReleaseComObject(target); }
                    break; // found the item; never keep walking the desktop
                }
                Thread.Sleep(15);
            }
        }
        catch (Exception ex) { Log.Error($"reaction search failed: {reactionId}", ex); }
        finally
        {
            if (root is not null) Marshal.ReleaseComObject(root);
            if (idCond is not null) Marshal.ReleaseComObject(idCond);
        }

        if (!ok) Log.Warn($"reaction item not found or not actionable: {reactionId}");

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

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>
    /// Performs a real left-click at the element's centre (used for pre-join controls that expose
    /// no actionable UIA pattern). The cursor is restored afterwards. Returns false if no on-screen
    /// point is available.
    /// </summary>
    private static bool ClickElement(AutomationElement el)
    {
        try
        {
            System.Windows.Point pt;
            try { pt = el.GetClickablePoint(); }
            catch
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;
                pt = new System.Windows.Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            }

            GetCursorPos(out var prev);
            SetCursorPos((int)pt.X, (int)pt.Y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(prev.X, prev.Y);
            return true;
        }
        catch (Exception ex) { Log.Error("click element failed", ex); return false; }
    }

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
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x0008;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, ref char lpString, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int nNumWindows);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(
        IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    private static bool IsTopmost(IntPtr hWnd) => (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Snapshot visible, non-minimized, non-topmost top-level windows in z-order (topmost first).
    /// Always-on-top windows are excluded: reordering through one would make every window after
    /// it topmost too (a SetWindowPos rule), corrupting the desktop stacking.
    /// </summary>
    private static IntPtr[] CaptureWindowOrder()
    {
        var list = new List<IntPtr>(64);
        try
        {
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && !IsIconic(h) && !IsTopmost(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("CaptureWindowOrder failed", ex); }
        return list.ToArray();
    }

    /// <summary>
    /// Restore the captured window order for an action that unavoidably foregrounds Teams (reactions).
    /// Runs on a background thread so it never blocks the press, waits for Teams to actually grab the
    /// foreground and then stop moving windows (rather than racing the async raise and flashing the
    /// stack back), and only restores while Teams — not the user — still holds the foreground.
    /// </summary>
    private static void RestoreWindowOrderDeferred(IntPtr[] order, IntPtr foreground)
    {
        if (order.Length == 0 || foreground == IntPtr.Zero) return;
        Task.Run(() =>
        {
            try
            {
                var teamsPids = GetTeamsPids();
                bool ForegroundIsTeams()
                {
                    var fg = GetForegroundWindow();
                    if (fg == IntPtr.Zero) return false;
                    GetWindowThreadProcessId(fg, out uint pid);
                    return teamsPids.Contains((int)pid);
                }

                long deadline = Environment.TickCount64 + RaiseSettleMaxMs;

                // Phase 1: wait for Teams to grab the foreground (its raise is async). Bail if focus
                // moves somewhere that isn't Teams and isn't the pre-press window (the user took over).
                while (Environment.TickCount64 < deadline
                       && GetForegroundWindow() == foreground && !ForegroundIsTeams())
                    Thread.Sleep(RaiseSettlePollMs);

                if (!ForegroundIsTeams()) return; // Teams never stole focus, or the user is elsewhere

                // Phase 2: wait for Teams to finish raising (foreground stops changing).
                IntPtr last = GetForegroundWindow();
                int stable = 0;
                while (Environment.TickCount64 < deadline && stable < RaiseSettleStable)
                {
                    Thread.Sleep(RaiseSettlePollMs);
                    var fg = GetForegroundWindow();
                    if (fg == last) stable++; else { stable = 0; last = fg; }
                }

                if (!ForegroundIsTeams()) return; // user grabbed focus while it settled
                RestoreWindowOrder(order, foreground);
            }
            catch (Exception ex) { Log.Error("deferred window-order restore failed", ex); }
        });
    }

    /// <summary>
    /// Reapply a previously captured z-order so the exact window stacking from before the
    /// action is restored, then re-activate the window that was actually focused. No-op when
    /// the foreground never changed.
    /// </summary>
    private static void RestoreWindowOrder(IntPtr[] order, IntPtr foreground)
    {
        if (order.Length == 0 && foreground == IntPtr.Zero) return;

        // Reapply the captured stacking if the z-order or the foreground actually changed. We compare
        // the full order (not just the foreground window) because Teams can raise the meeting window
        // above other windows even after focus has already returned to the user's window.
        var current = CaptureWindowOrder();
        bool foregroundChanged = foreground != IntPtr.Zero && GetForegroundWindow() != foreground;
        if (order.Length > 0 && (foregroundChanged || !SameOrder(order, current)))
        {
            // Attach our input to the thread that currently owns the foreground (Teams) so the OS
            // lets us restack windows above it. A background process is otherwise blocked from
            // reordering windows above the foreground window (the foreground lock), which is why the
            // focused window came back but the windows sitting on top of Teams did not.
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            uint thisThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != thisThread
                            && AttachThreadInput(thisThread, fgThread, true);
            try
            {
                var hdwp = BeginDeferWindowPos(order.Length);
                if (hdwp != IntPtr.Zero)
                {
                    // Synchronous (no SWP_ASYNCWINDOWPOS) so the stack ends up in the exact order.
                    const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
                    IntPtr insertAfter = HWND_TOP;
                    foreach (var hwnd in order) // top -> bottom (non-topmost windows only)
                    {
                        if (IsTopmost(hwnd)) continue; // never anchor through an always-on-top window
                        hdwp = DeferWindowPos(hdwp, hwnd, insertAfter, 0, 0, 0, 0, flags);
                        if (hdwp == IntPtr.Zero) break;
                        insertAfter = hwnd;
                    }
                    if (hdwp != IntPtr.Zero) EndDeferWindowPos(hdwp);
                }
            }
            catch (Exception ex) { Log.Error("RestoreWindowOrder failed", ex); }
            finally
            {
                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
        }

        // Re-activate the window that actually had focus (also restores keyboard input).
        if (foregroundChanged)
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
