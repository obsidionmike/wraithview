# WraithView

> **WraithView is a fork of [TriffView](https://github.com/NarcisussX/TriffView) by NarcisussX**, released under the GNU GPL v3.0 only, like the original. It's maintained by the [wraithtools](https://tools.wraithjack.com) crew. Changes from TriffView 2.1.1 are listed under [Changes from TriffView](#changes-from-triffview). All credit for the original design and code goes to TriffView's authors.

Fast EVE client previews, live log alerts, EVE Settings management, and ESI fleet setup in one standalone app.

WraithView is built for EVE Online multiboxers who want the best parts of preview switching, profile management, alert awareness, EVE Settings copying, and fleet setup assistance without needing multiple programs. It gives you live client previews, rapid character switching, persistent layouts, configurable combat/session alerts, imported EVE-O/EVE-X profiles for a fast transition, EVE Settings backup/copy tools, and an ESI-only Fleet Manager in a focused desktop app that is designed to stay light.

## Why It Exists

Multiboxing should feel immediate. Your preview windows should be where you left them, your hotkeys should follow your characters, and your multiboxing program should not use more resources than your clients.

WraithView is a standalone preview system paired with configurable log alerts, a custom EVE Settings management application, and a fleet layout assistant. One app that can manage how you switch between clients, how you notice important events, how your EVE character settings move between profiles/accounts/backups/fresh installs, and how saved fleet layouts become live ESI fleet invites.

## Highlights

- Live EVE client previews with fast click-to-focus switching.
- Direct character hotkeys and cycle group hotkeys.
- Multiple hotkeys per character or cycle direction.
- Persistent preview layouts per profile.
- Custom preview labels, label placement, label size, opacity, and colors.
- Lockable previews so carefully placed layouts stay put.
- WraithAlerts for attack, warp scramble, decloak, fleet invite, convo request, and system-change awareness.
- Per-alert preview flashes, colors, notifications, and more.
- Client layout save, restore, and optional launch restore.
- Optional inactive-client minimization tuned for rapid switching, no more dropping to desktop or lagging when cycling.
- EVE-O Preview JSON import.
- EVE-X Preview JSON import.
- Full WraithView settings backup export and restore.
- Fleet Manager for saving wing/squad layouts, pre-assigning characters, restructuring live fleets, moving existing members, and sending ESI invites.
- WraithSkills for scoring your authenticated characters against local skill plans, with the skills each one still needs.
- Dark themed standalone settings window with selectable GUI themes.
- Tray controls for quick enable, disable, suspend, save, restore, reload, and quit actions.

## EVE Settings Included

Use it to manage EVE profile and character settings, copy settings, overviews, window positions and layouts between characters, preserve backups, and recover working layouts without digging through EVE's settings folders by hand. For players running multiple accounts or rebuilding setups often, this lets you reload your layouts quickly, and repeatably. 

## Fleet Manager Included

WraithFleets lets you save fleet wing/squad names, pre-assign characters and roles, then hit Apply to restructure your current fleet and invite or move pilots into the correct squads through official EVE SSO and ESI.

Build repeatable fleet templates for DPS, logi, scouts, miners, rolling crews, or whatever your multiboxing setup needs. Create the fleet in-game, detect it with the authenticated fleet boss, then let WraithFleets create or rename wings and squads, move existing members into their saved positions, invite missing characters, keep unexpected pilots in a Bench / Waiting squad, and show a clear result log for every action.

Fleet Manager does not control EVE clients. It does not inject keyboard input, mouse input, chat commands, OCR, memory reads, warps, modules, or invite acceptance. Characters still accept fleet invites manually in-game.

## WraithSkills Included

Checking a skill plan across 10, 20, or 30 characters should not mean opening 10, 20, or 30 character sheets. WraithSkills puts every authenticated character and every local skill plan into one matrix, so you can immediately see who is ready, who is training, and who still has work to do.

The boxes fill as more of the plan is trained and change color based on what is holding the character back. Ready means every required skill is currently active, Training means the remaining requirements are already in the queue, and Locked catches skills that are trained but unavailable on the character’s current clone. Click any character, plan, or individual result to see the full breakdown, including missing skills, queued requirements, active and trained levels, and an ETA when EVE provides one.

Skill plans are simple `.txt` files using the same format EVE uses: one skill per line, such as `Navigation V`. Paste a plan into the importer, review exactly what WraithSkills validated, then save it locally. You can also add, edit, share, or remove plans directly from the plans folder and reload them without restarting WraithView.

Characters are authenticated through official EVE SSO, and WraithView never sees your EVE password. WraithSkills only requests permission to read skills and the skill queue. Refresh tokens are stored in Windows Credential Manager, separately from Fleet Manager, while access tokens only exist in memory. If ESI goes down or a refresh fails, your last successful data stays visible and is clearly marked stale instead of disappearing.

WraithSkills is completely read-only. It cannot train skills, change your queue, buy injectors, control an EVE client, or perform any other action on your character.

## WraithAlerts Included

WraithAlerts turns EVE's own live game logs into fast, visible preview alerts. When a character takes incoming damage, gets scrammed, decloaks, receives a fleet invite, gets a conversation request, or changes systems, WraithView can flash that character's preview so your eyes go to the right client immediately.

Each alert type has its own color, flash thickness, sound, and notification option. Attack alerts can ignore likely NPC damage so the focus stays on PvP threats. Sounds are packaged with the app, alerts are off by default, and the default mode is quiet and visual: turn it on, tune it once, and let the previews tell you which client needs attention.

WraithAlerts uses EVE log files only. It does not read memory, hook the EVE client, inject input, OCR the screen, or control the game.

## Low Resource By Design

WraithView is built around native Windows preview surfaces instead of a screenshot loop. In normal use, previews stay live without constantly capturing and repainting every client manually.

The result is a preview tool that aims to feel fast, stay smooth during rapid switching, and keep overhead low enough that you never even notice it.

## Profile First

Window-switching hotkeys use the bundled AutoHotkey v2 runtime. The hotkey editor accepts AHK expressions directly: `F14` is an ordinary binding, while `*F14` also works with modifiers held. Existing `Control+F14`-style bindings remain accepted. Details and current validation status are in [the hotkey implementation notes](native/Hotkeys/README.md).

WraithView profiles let different play styles have different layouts.

Keep one profile for fleets, one for market or industry characters, one for scouts, one for testing, or one for every group you fly. Preview positions, labels, hotkeys, cycle groups, colors, client behavior, and imported layouts can all live with the profile they belong to. Switching between cycle groups or profiles can be done hot, with 2 clicks. 

## Imports And Backups

WraithView can import EVE-O Preview and EVE-X Preview JSON settings, then turn them into a WraithView profile. It will replicate your previous layout choices, hotkeys and cycle groups with one click, before letting you customize even further with all the options WraithView offers. It also supports full WraithView settings export and restore, so you can keep a portable backup of every profile and setting.

## Built For EVE Multiboxers

It does not broadcast input, forward input, read game memory, inject into the EVE client, or hook client rendering. It focuses on the things a preview tool should do well: show clients, switch clients, remember layouts, manage settings, assist with ESI fleet invites, and get out of your way.

## Project Notes

WraithView is licensed under the GNU General Public License, version 3 only (`GPL-3.0-only`). See `LICENSE`.

Third-party dependency, font, WebView2, and EVE/CCP trademark notices are listed in `THIRD_PARTY_NOTICES.txt`.

## Release Verification

Current Windows release: `v2.1.1`

- File: `WraithView.exe`
- Size: `168,560,016 bytes` (`160.75 MiB`)
- SHA-256: `8274A9837038F31D6EA211D64299D57BAB390D7742F6BA1807B18344BFAEF6C0`

The release executable is Authenticode signed by Cooper Broderick and timestamped through Microsoft's timestamp service. The download also includes `WraithView.exe.sha256.txt` for verification.

## Thanks And Inspiration

- [EVE-O Preview](https://github.com/Proopai/eve-o-preview) for the original live preview/multiboxing inspiration.
- [EVE-MultiPreview](https://github.com/CJKondur/EVE-MultiPreview) for the alerts idea.
- [EVE Settings Manager](https://github.com/mintnick/eve-settings-manager) for the EVE settings management idea.
- [Yeramel](https://www.youtube.com/@Yeramel) for the fleet management tool idea.
- [guarzo](https://github.com/guarzo) for the Skill Planner inspiration based on CanIFly.


## Changes from TriffView

WraithView started from TriffView 2.1.1 (commit `5ef7561`, 2026). Modified by the wraithtools crew from October 2026:

- Renamed to WraithView (and WraithAlerts / WraithFleets / WraithSkills), with a new icon.
- New default **Void** GUI theme; TriffView's original palette is kept as **Classic**, alongside the faction themes. New preview profiles default to a violet active border.
- Settings live in `%APPDATA%\WraithView`. On first start, an existing TriffView setup (profiles, hotkeys, EVE Settings data and backups, fleet layouts, skill plans) is copied over once; TriffView's own files are left untouched. EVE sign-ins don't carry over because WraithView uses its own EVE SSO application.
- Warns and stands down if TriffView is running at the same time, so the two don't fight over previews and hotkeys.
- Updates are checked against this repository's releases.
- Can be cross-built on Linux (`scripts/build-linux.sh`).
- Preview snapping rewritten (`native/WraithView/PreviewSnapping.cs`, with tests). The closest edge wins. Previews stack (top to bottom) and sit side by side (left to right) as well as lining up, and only snap to previews actually beside them. Resizing snaps the dragged edge. Outer screen edges hold a preview, then let it slide off-screen if you keep pushing (at least 24 px always stays visible). Seams between monitors don't hold.
- Dragging a preview no longer smears its frame or label behind the cursor.
- The main window's edge (Windows 11 window border) uses the theme's soft accent colour instead of a near-black, so the window stays visible on dark desktops.
- Optional "Edge outline" for previews (off by default): a faint light line around each one.
- Clicking a preview to switch no longer drags it by accident (a short hold or a deliberate flick is needed). Previews parked off-screen snap to each other vertically too. "Minimize inactive clients" no longer stalls each switch while the old client minimises.
- Preview borders are drawn as solid strips, so a 1 px border is complete. Labels no longer flash at the old position after a drag. Previews parked off-screen can be pushed further without jumping back. The Opacity field accepts typed values.
- EVE Settings links each character to its account file (`core_user_<id>.dat`). Links are learned when one client logs out on its own (its character and account files are written together; several clients closing at once, or copied files, are ignored) and can be set by hand in the character table. Character copy can also copy the source's account settings to each target's account. Every target is backed up before any file is overwritten, and copy confirmations list the files they overwrite and say to close EVE first.

## Building

- Windows: `scripts\publish-release.cmd` (as upstream).
- Linux: `DOTNET=/path/to/dotnet scripts/build-linux.sh [out-dir]`. Needs Node 18+ and a Microsoft .NET 8+ SDK that ships the Windows Desktop targets. Ubuntu's source-built `dotnet-sdk-8.0` doesn't; Microsoft's SDK or conda-forge's `dotnet-sdk` does.

## License

GNU General Public License v3.0 only. See `LICENSE` and `THIRD_PARTY_NOTICES.txt`. If you share a WraithView build, share its source too (this repository).
