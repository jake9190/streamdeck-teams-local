# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0] - 2026-06-24

### Added
- Self-contained publishing: the .NET runtime is bundled, so **end users no longer
  need the .NET Desktop Runtime installed**.

### Changed
- The Surprised reaction icon is now an open-mouthed "wow" face instead of X-eyes.
- Reactions can be pressed rapidly in a row: the reaction flyout is kept open across a
  burst of presses and closed shortly after the last one, so repeats reuse the open
  flyout instead of reopening each time. Reactions also skip the post-press
  confirmation polls to keep UI Automation free during bursts.

## [1.0.0] - 2026-06-24

### Added
- Native C# (`net8.0-windows`) Stream Deck plugin driving the new Microsoft Teams
  client via in-process Windows UI Automation.
- Actions: Mute/Unmute, Camera On/Off, Raise/Lower Hand, Share Screen, Leave Meeting,
  and five reactions (Like, Love, Applause, Laugh, Surprised).
- Live state icons with a modern flat design rendered as SVG.
- Optimistic key updates on press, with fast confirmation polls.
- Debounced state polling that preserves last-known state across transient UI
  Automation failures (no more "offline" flicker mid-meeting).
- Reaction flyout auto-closes after a reaction is sent.
- Focus restoration: returns focus to the previously active window if Teams steals it.
- Cached UI Automation control elements for faster polls and presses.
- `build.ps1` / `deploy.ps1` tooling and project documentation.
- GitHub Actions **CI** workflow (build + artifact on every push) and **Release**
  workflow that packages a `.streamDeckPlugin` on `v*.*.*` tags.
- `build/package.ps1` to produce `.streamDeckPlugin` + `.zip` distributables.
