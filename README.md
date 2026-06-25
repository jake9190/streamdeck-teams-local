# Stream Deck — MS Teams Local

A fast, native **Elgato Stream Deck** plugin that controls the **new Microsoft Teams**
desktop client directly through Windows UI Automation — no global hotkeys, no
window focus stealing, and live state right on the keys.

> Windows only. Works with the **new Teams** client (`ms-teams.exe`).

---

## Features

| Action | Behaviour | Live state on key |
| --- | --- | --- |
| **Mute / Unmute** | Toggles the meeting microphone | ✅ mic / muted |
| **Camera On / Off** | Toggles the meeting camera | ✅ camera / off |
| **Raise / Lower Hand** | Toggles your raised hand | ✅ raised badge |
| **Share Screen** | Opens the share tray | ✅ sharing |
| **Leave Meeting** | Leaves the current meeting | — |
| **Reactions** | Like, Love, Applause, Laugh, Surprised | — |

Highlights:

- **Instant presses.** Everything runs in one long-lived process that talks to UI
  Automation in-process — there is no per-press `powershell.exe` spawn.
- **No focus theft.** If Teams grabs the foreground while reacting, the plugin
  hands focus back to the window you were using.
- **Optimistic icons.** Toggles update the key the moment you press, then reconcile
  with the real state on the next poll.
- **Stable state.** A debounced poll preserves the last-known state across transient
  UI Automation hiccups, so the keys don't flicker "offline" during a meeting.
- **Reaction flyout auto-closes** after the reaction is sent.
- **Modern flat icons** rendered as SVG at runtime, with state colours.

---

## Requirements

- Windows 10 / 11
- [Elgato Stream Deck](https://www.elgato.com/stream-deck) software 6.5+
- The **new** Microsoft Teams desktop client
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
  — the plugin is a framework-dependent `net8.0-windows` app
- [.NET SDK 8+](https://dotnet.microsoft.com/download) to build from source

---

## Quick start

```powershell
# from the repo root
./build/deploy.ps1
```

`deploy.ps1` builds the plugin, generates the icons, copies the package into the
Stream Deck plugins folder, and restarts Stream Deck. Then, in the Stream Deck
app, find the **MS Teams Local** category and drag the actions onto your keys.

To build without deploying:

```powershell
./build/build.ps1
```

---

## How it works

```
Stream Deck app  ──websocket──►  msteams-local.exe  ──UI Automation──►  Teams meeting window
       ▲                               │
       └──────── setImage ─────────────┘   (live state icons)
```

1. Stream Deck launches `msteams-local.exe` (the `CodePath` in `manifest.json`) and
   passes it a port + registration token.
2. The plugin connects back over a local WebSocket and registers.
3. A background thread polls the Teams meeting window roughly every 600 ms, reading
   the toolbar buttons (`microphone-button`, `video-button`, …) via UI Automation.
4. Key presses invoke the matching control in-process; toggles update optimistically.

See [docs/architecture.md](docs/architecture.md) for the detailed design and the
verified UI Automation identifiers.

---

## Project layout

```
streamdeck-teams-local/
├─ src/                                  # C# source (net8.0-windows)
│  ├─ MsTeamsLocal.csproj
│  ├─ Program.cs                         # entry point + --emit-icons mode
│  ├─ StreamDeckConnection.cs            # Stream Deck WebSocket protocol
│  ├─ TeamsAutomation.cs                 # UI Automation engine (poll + actions)
│  ├─ IconRenderer.cs / IconEmitter.cs   # SVG key icons
│  ├─ PluginHost.cs                      # orchestration
│  └─ …
├─ com.local.msteams-local.sdPlugin/     # the Stream Deck plugin package
│  ├─ manifest.json                      # action + plugin metadata (tracked)
│  ├─ imgs/                              # generated key/category icons (tracked)
│  └─ msteams-local.exe + *.dll          # build output (ignored)
├─ build/
│  ├─ build.ps1                          # publish + emit icons
│  └─ deploy.ps1                         # build + install into Stream Deck + restart
└─ docs/
   ├─ architecture.md
   └─ development.md
```

---

## Development

```powershell
dotnet build src/MsTeamsLocal.csproj -c Release    # compile
./build/build.ps1                                   # publish into the package
./build/deploy.ps1                                  # install + restart Stream Deck
```

Logs are written to `logs/plugin.log` next to the deployed executable. See
[docs/development.md](docs/development.md) for the iteration loop and troubleshooting.

---

## Troubleshooting

- **Keys look dimmed / "offline".** There's no active Teams meeting, or Teams isn't
  running. The keys light up once a meeting is detected.
- **A reaction does nothing.** Make sure you are in a meeting and the reactions bar is
  available. The plugin opens the React flyout and clicks the reaction via UI
  Automation; localized Teams builds may use different control names.
- **Plugin not appearing.** Confirm the package folder was copied into
  `%APPDATA%\Elgato\StreamDeck\Plugins\` and that Stream Deck was restarted.

---

## License

[MIT](LICENSE). Icon path data from [Phosphor Icons](https://phosphoricons.com/) (MIT).

> Not affiliated with or endorsed by Microsoft or Elgato.
