# Architecture

## Overview

`MS Teams Local` is a native Stream Deck plugin written in C# (`net8.0-windows`).
It runs as a single long-lived process and drives the new Microsoft Teams desktop
client through **Windows UI Automation (UIA)**. Running in-process is the key design
decision: it removes the per-press process spawn and assembly load that a
PowerShell-based approach pays on every action, which in turn fixes press latency,
state flicker, and icon-update lag.

```
┌──────────────┐   websocket    ┌────────────────────┐   UI Automation   ┌─────────────┐
│ Stream Deck  │ ◄───────────►  │  msteams-local.exe │ ◄──────────────►  │ Teams window │
│  software    │   events /     │                    │   read + invoke   │  (toolbar)   │
└──────────────┘   setImage     └────────────────────┘                   └─────────────┘
```

## Components

| File | Responsibility |
| --- | --- |
| `Program.cs` | Parse Stream Deck launch args; `--emit-icons` mode; wire up and run. |
| `StreamDeckConnection.cs` | Stream Deck WebSocket transport (register, receive, `setImage`, `showAlert`, `showOk`). BCL only — `ClientWebSocket` + `System.Text.Json`. |
| `TeamsAutomation.cs` | The UIA engine: meeting-window discovery, state polling, control invocation, reactions, optimistic state, focus restore. |
| `IconRenderer.cs` | Builds modern flat SVG key icons and returns them as `data:image/svg+xml;base64`. |
| `IconEmitter.cs` | Writes the static manifest icon files (idle key icons + category icon). |
| `PluginHost.cs` | Tracks visible keys, repaints on state change, handles key presses. |
| `ActionCatalog.cs` | The set of actions and their UUID suffixes. |
| `TeamsSnapshot.cs` | Immutable observed-state record. |
| `Logger.cs` | Minimal rolling file logger (`logs/plugin.log`). |

## State polling

A background thread polls Teams' state on an **adaptive cadence**: ~600 ms while a
meeting or the pre-join screen is live (so mute/camera/hand/share changes reflect
promptly), and ~2.5 s when idle (Teams running, no meeting — the common case), so the
process barely uses CPU and the meeting-window probe runs far less often.

1. Determine whether Teams is running (process check for `ms-teams` / `msteams`).
2. Resolve the **meeting window**: a top-level window owned by a Teams process that
   contains a `hangup-button` (in-meeting) or a `prejoin-join-button` (pre-join). The
   resolved window is cached and re-validated cheaply on subsequent polls.
3. Read control state from the cached window by inspecting each toolbar button's
   `Name` (see the keyword table below). Each toolbar element is cached so a poll
   does not re-walk the UIA tree.
4. Publish an immutable `TeamsSnapshot`; the host repaints any keys whose image
   would change.

### Detection uses the COM UIA client, not the managed wrapper

The idle poll must search the (large, Chromium-based) Teams window for a meeting
toolbar that usually isn't there. The managed `System.Windows.Automation` client leaks
native memory under continuous descendant searches, so meeting-window **detection** uses
the COM `IUIAutomation` client directly: top-level windows are enumerated with cheap
Win32 calls (`EnumWindows`), each is probed with a **single** descendant search
(`hangup-button` OR `prejoin-join-button`), and every COM element is freed with
`Marshal.ReleaseComObject`. A managed `AutomationElement` is materialized (via
`AutomationElement.FromHandle`) only once a real meeting window is found, keeping the
leaky managed client off the always-running idle path.


### Robustness

- **Miss debounce.** If the meeting window can't be found, the previous state is held
  for up to `MissThreshold` (3) consecutive polls before declaring "no meeting", so a
  single transient UIA failure never knocks the keys offline.
- **Preserve-on-failure.** A control read that fails returns `null` and the previous
  value is kept rather than being reset.
- **Element cache invalidation.** The control cache is cleared whenever the meeting
  window changes, and individual stale elements are re-found on demand.

## Presses

On `keyDown`:

1. If there is no active meeting, `showAlert` and stop.
2. For toggles, apply an **optimistic** state flip (held ~1500 ms) so the key updates
   instantly.
3. Invoke the control in-process on a worker thread.
4. Run two fast confirmation polls (≈180 ms and ≈580 ms) to reconcile real state.

### Focus restoration

`Trigger()` records the foreground window before acting and, in a `finally`, restores
it if Teams stole focus — using `AttachThreadInput` to bypass the Windows foreground
lock without synthetic keystrokes. The exception is **Leave Meeting**, where a focus
change is expected.

## Reactions

Teams has no per-reaction hotkeys, so reactions go through UIA:

1. Find `reaction-menu-button` and open the flyout via **`ExpandCollapsePattern.Expand()`**
   (the React button does **not** support `InvokePattern`).
2. Poll briefly for the reaction item in the popup and `InvokePattern.Invoke()` it.
3. **Close the flyout** via `ExpandCollapsePattern.Collapse()` (Escape fallback, only if
   Teams holds focus).

## Verified UI Automation identifiers

Confirmed against a live new-Teams meeting:

| Control | AutomationId | Pattern | State keyword (English) |
| --- | --- | --- | --- |
| Microphone | `microphone-button` | Invoke | Name contains `unmute` ⇒ muted |
| Camera | `video-button` | Invoke | Name contains `camera on` ⇒ camera off |
| Raise hand | `raisehands-button` | Invoke | Name contains `lower` ⇒ raised |
| Share | `share-button` | Invoke | Name contains `stop` ⇒ sharing |
| Leave | `hangup-button` | Invoke | — |
| React | `reaction-menu-button` | **ExpandCollapse** | opens the reaction flyout |

Reaction flyout items (support `InvokePattern`):
`like-button`, `heart-button`, `applause-button`, `laugh-button`, `surprised-button`.

> State detection relies on English control names. Localized Teams builds would need
> the keyword table extended.

## Icons

Live key icons are rendered as inline SVG (rounded-rect background + a Phosphor glyph)
and sent via `setImage` as a base64 data URI. The static manifest icons (the action
list icons, default key images, and category icon) must be PNG for the Elgato packaging
tool, so `IconEmitter` rasterizes the **same** `IconRenderer.GetVisual` output with WPF
(`DrawingVisual` / `RenderTargetBitmap`) at the standard Stream Deck sizes. Sharing
`GetVisual` keeps the live and static icons visually identical.
