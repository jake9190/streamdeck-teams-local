# Development

## Prerequisites

- .NET SDK 8.0 or newer (`dotnet --version`)
- The .NET 8 **Windows Desktop** targeting pack (ships with the SDK on Windows; provides
  the UI Automation / WPF assemblies used at build time)
- Elgato Stream Deck software
- The new Microsoft Teams desktop client (for live testing)

> End users do **not** need a .NET runtime installed — the plugin is published
> self-contained (`build.ps1` uses `--self-contained true`), bundling the runtime.

## Build

```powershell
dotnet build src/MsTeamsLocal.csproj -c Release
```

## Publish into the plugin package

`build.ps1` publishes a self-contained build into the `.sdPlugin` package and
(re)generates the icon files:

```powershell
./build/build.ps1
```

This is equivalent to:

```powershell
dotnet publish src/MsTeamsLocal.csproj -c Release -r win-x64 --self-contained true `
    -o com.local.msteams-local.sdPlugin
./com.local.msteams-local.sdPlugin/msteams-local.exe --emit-icons
```

## Deploy and run

`deploy.ps1` builds, copies the package into the Stream Deck plugins folder, and
restarts Stream Deck so it reloads the plugin:

```powershell
./build/deploy.ps1
# optional: target a non-default plugins folder
./build/deploy.ps1 -StreamDeckPluginsDir "D:\StreamDeck\Plugins"
```

Because the running plugin executable is locked while Stream Deck is open, the deploy
script stops Stream Deck (and the plugin) first, then republishes and relaunches it.

## Iteration loop

1. Edit code in `src/`.
2. `./build/deploy.ps1`
3. In Stream Deck, exercise the keys (ideally in a real Teams meeting).
4. Check `logs/plugin.log` inside the deployed package folder.

## Logging

`Logger.cs` writes `logs/plugin.log` next to the executable. It records `INFO` and
above and rolls the file when it passes ~512 KB. Stream Deck's own log
(`%APPDATA%\Elgato\StreamDeck\logs\StreamDeck.log`) shows plugin connect/registration
lines such as `[com.local.msteams-local] Plugin connected`.

## Regenerating icons only

```powershell
./com.local.msteams-local.sdPlugin/msteams-local.exe --emit-icons
```

Icons are committed to the repo so the package is complete without a build, but they
are fully reproducible from `IconRenderer`/`IconEmitter`.

## Notes / gotchas

- The plugin is a `WinExe`, so launching it from a shell returns immediately. Use
  `Start-Process -Wait` when you need to wait for `--emit-icons` to finish.
- State detection uses **English** Teams control names; see
  [architecture.md](architecture.md) for the keyword table.
- `UseWPF=true` in the csproj is what makes the `System.Windows.Automation` assemblies
  available; the app does not actually use WPF UI.
