# WraithView: handoff

Read this first in a new session.

## What this is

WraithView is a **GPL-3.0-only fork of TriffView 2.1.1** (https://github.com/NarcisussX/TriffView, upstream commit `5ef7561`). It's a Windows multiboxing suite for EVE Online:
- live DWM client previews with hotkeys and cycle groups
- WraithAlerts (game-log driven preview flashes)
- EVE Settings copy and backup
- WraithFleets (ESI fleet layouts and invites)
- WraithSkills (skill-plan readiness via ESI)
- EVE-O and EVE-X import

It belongs to Michael (GitHub `obsidionmike`) and sits alongside his website **wraithtools** (https://tools.wraithjack.com, repo `obsidionmike/wraithtools`, which has its own HANDOFF.md).

The user asked to fork it and "improve on it". Triff.tools (and probably TriffView's maintenance) ends on 2026-10-30.

## Licence rules (keep them)

- GPL-3.0-only. Keep `LICENSE`, upstream copyright and credits, and `THIRD_PARTY_NOTICES.txt` (with our header).
- Record modifications in README → "Changes from TriffView".
- Anyone given a build must be able to get the source. The plan is a **public** repo `obsidionmike/wraithview`, with releases there.
- Bundled third parties: AutoHotkey v2 runtime (GPL-2 or later, with its source zip, in `native/Hotkeys/Runtime`), EVE-X-derived AHK script (`native/Hotkeys/EVE-X-LICENSE.txt`), WebView2 and IdentityModel (MIT). Don't drop their notices.

## Architecture

- `native/`: WPF/.NET 8 (`net8.0-windows`) host.
  - `MainWindow.xaml.cs`: WebView2 shell, tray, theme bridge (`GuiThemePalette`).
  - `WraithView/WraithViewSubsystem.cs`: previews, profiles, settings.
  - `Hotkeys/`: AHK v2 helper process for hotkeys.
  - `WraithAlerts/`, `WraithFleets/`, `WraithSkills/`, `EveSettings/`, `Eve/` (SSO: PKCE plus loopback callback, Credential Manager, JWT validation).
- `app/`: React UI (Vite), served inside WebView2 from the embedded `native/Assets/overlay-dist.zip` (built from `app/dist`, gitignored).
  - The native↔web message protocol is in `app/src/nativeBridge.js`.
  - The UI also runs standalone in a browser in demo mode, which is handy for screenshots.
- Themes: CSS tokens `--tv-*` in `app/src/styles.css`. `:root` is the "Classic" (TriffView) palette. `[data-theme=void]` is the default (first in `GUI_THEMES` in `app/src/App.jsx`). Native startup colours are `WraithToolsGuiTheme` in `MainWindow.xaml.cs`.

## Build

- Linux: `DOTNET=/path/to/dotnet scripts/build-linux.sh [out-dir]` builds a self-contained single-file exe (about 74 MB).
  - For a small test build (about 7 MB, needs the .NET 8 Desktop Runtime), run the script's `dotnet publish` line with `--self-contained false`.
  - **Ubuntu's `dotnet-sdk-8.0` lacks the WindowsDesktop targets.** The cloud container has no access to Microsoft's download hosts, so session 1 unpacked conda-forge's `dotnet-sdk-10.0.401` and `dotnet-runtime-10.0.12` (`conda.anaconda.org/conda-forge/linux-64/*.conda`, zip containing `pkg-*.tar.zst`) into `/opt/dotnet10/lib/dotnet` and used `DOTNET_ROOT` from there. Install zstd with apt.
  - The NuGet restore uses api.nuget.org, which works.
- Windows: `scripts\publish-release.cmd` (upstream script; it signs if certificates are configured).
- Tests (`native/WraithView.Tests`, xunit, `net8.0-windows`) only run on Windows.

## State (end of session 1, 2026-10-14)

Done (commit "Fork TriffView 2.1.1 as WraithView"):
- Full rename (tokens Triff→Wraith across code, CSS, file names). User-facing "TriffHUD" text became WraithView. The app checks for a running **TriffView** and stands down.
- Settings in `%APPDATA%\WraithView`. `native/LegacyImport.cs` copies an existing TriffView setup once (marker file `imported-from-triffview.txt`). EVE Settings restore accepts TriffView backup manifests.
- Void theme (default) plus Classic. New preview profiles get a violet active border. New icon at `native/Assets/WraithView.ico` and `.svg`.
- `native/Eve/EveApp.cs` holds the **single EVE SSO client ID**: `66ce217a8228469bbedd1f97f484c744` (WraithView's own EVE developer app, registered by the user on 2026-10-14; a client ID isn't secret). The env var `WRAITHVIEW_EVE_CLIENT_ID` overrides it. The callback is `http://127.0.0.1:51777/wraithfleets/callback/`. Scopes: `esi-fleets.read_fleet.v1 esi-fleets.write_fleet.v1 esi-skills.read_skills.v1 esi-skills.read_skillqueue.v1`.
- The update checker points at `obsidionmike/wraithview` releases. Version 0.9.0.
- It builds on Linux. The UI was checked in a browser (demo mode). **Not yet run on Windows.**

Done since:
- Public repo **github.com/obsidionmike/wraithview** created; `main` pushed with full upstream history (remote `upstream` = TriffView).
- CI (`.github/workflows/ci.yml`, from upstream) builds and runs the xunit tests on windows-latest for every push to main and every PR. Use this as the Windows test signal.
- `.github/workflows/release.yml`: push a tag `vX.Y.Z` (or run it manually) to build the self-contained exe on Windows, run the tests, and publish a GitHub release with `WraithView.exe` and its SHA-256. Tags starting `v0.` or containing `-` are marked as pre-releases.

v0.9.1 (from the user's first test, 2026-10-14):
- Their verdict: previews, alerts and switching work.
- Snapping rewritten as `PreviewSnapping` (pure, xunit-tested in `PreviewSnappingTests`). The old code only matched same edges (top↔top), so dragging a preview up snapped it onto the other's top. It never snapped side by side, applied the last match rather than the closest, and clamped everything to the screen.
- Off-screen parking (a user request): outer screen edges hold for `BreakoutFor(snap)` = max(40, 3×snap) px, then follow the cursor. `KeepOnDesktop` keeps 24 px visible, including for saved layouts after a monitor change.
- Drag smear fixed: `ApplyDragFrame` paints the GDI frame synchronously before moving the DWM thumbnail. The transparent-label overlay is hidden during a drag.
- `EdgeOutline` profile option (default **off**: the user only wanted the settings window's edge, the previews were fine), with the "Edge outline" toggle in Preview layout.
- v0.9.2: the settings window's DWM border colour (Windows 11 only; `DWMWA_BORDER_COLOR`) comes from `--tv-border-accent-soft` (Void #4b3878) instead of `--tv-border-topbar` (#241f37, invisible on dark desktops). See `currentNativeTheme` in `app/src/App.jsx`.
- Not yet tried by the user: cycle groups, Fleet Manager, Skill Planner.
- They asked about an unknown character (2122885899) in EVE Settings. It comes from a `core_char_<id>.dat` in their own EVE settings folder (the name is resolved via ESI). An idea, not done: show each file's last-modified date in the character list.

Waiting on the user:
1. Test the build on Windows: previews, hotkeys, alerts, the TriffView import, and EVE SSO sign-in for Fleet Manager and Skill Planner.
2. Agree on what "improve on it" should cover.

Next ideas ("improve on it"): agree them with the user first.
- A releases workflow: GitHub Actions on windows-latest building and attaching the self-contained exe, since chat can't send files over 30 MB.
- Code signing (unsigned exes trigger SmartScreen).
- Link a download from wraithtools (the `previewer` entry in its registry is still "Soon").
- Integration with wraithtools data, such as D-scan or local intel from the client.
