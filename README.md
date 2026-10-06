# DungeonsModLoader

A Windows mod manager for **Minecraft Dungeons II** (Steam, Xbox app / Minecraft Launcher) with **Nexus Mods** as its online mod library.

> Status: in development. See the milestone list below.

## Requirements

- Windows 10 / 11 (x64)
- To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer, Visual Studio 2022 (optional)
- To package: [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## Build

```bash
dotnet build DungeonsModLoader.sln
dotnet test DungeonsModLoader.sln
dotnet run --project src/DungeonsModLoader.App
```

Or open `DungeonsModLoader.sln` in Visual Studio 2022 and press F5.

Developer switches:

- `--swatch` opens the theme swatch window (design tokens, typography, control gallery).
- `--data-dir <folder>` uses an isolated app data folder (settings, manifest, profiles, logs, cache) instead of `%LOCALAPPDATA%\DungeonsModLoader`.

## Solution layout

```
DungeonsModLoader.sln
src/
  DungeonsModLoader.App/      WPF app: Views, ViewModels, Themes, Controls, App.xaml
  DungeonsModLoader.Core/     Models, mod store, install/update/profile services, game detection (no WPF)
  DungeonsModLoader.Nexus/    Nexus Mods API client, SSO, nxm:// link parsing, DTOs
tests/
  DungeonsModLoader.Core.Tests/
installer/
  setup.iss                   Inno Setup script (milestone 7)
```

## How mods are stored

- Game root: for Steam `...\steamapps\common\Minecraft Dungeons II`, for the Xbox app / Minecraft Launcher `...\XboxGames\Minecraft Dungeons II\Content` (both contain the `Dungeons` folder)
- Enabled mods: `<GameRoot>\Dungeons\Content\Paks\~mods\<ModFolder>\`
- Disabled mods: `<GameRoot>\Dungeons\DungeonsModLoader_Disabled\<ModFolder>\` (same drive, outside `Paks`, so the game ignores them)
- App data: `%LOCALAPPDATA%\DungeonsModLoader\` (`settings.json`, `manifest.json`, `profiles\`, `cache\`, `downloads\`, `logs\`, `backups\`)

## Mods installed by hand

Anything placed in `~mods` outside the app is picked up automatically: on every start, before launching the game,
and (while the app runs) a couple of seconds after the folder changes. A folder becomes a local mod with its files
recorded in `manifest.json`; loose `Name_P.pak/.ucas/.utoc` files lying directly in `~mods` are moved into a
`~mods\Name\` folder first so they can be enabled and disabled. Nothing is deleted. A folder that cannot be read
yet (still copying, files in use) shows up as "Unmanaged" and is added as soon as it can be read.

## Profiles

A profile is a named set of enabled mods, stored as `profiles\<name>.json`. The active profile always mirrors the
Installed page: every toggle, install and uninstall updates it. Activating another profile (Profiles page, or the
dropdown on the Installed page) enables its mods and disables all others by moving folders; if a move fails the
moves already made are undone and the previous profile stays active. New profiles start with the mods enabled at
that moment. Export writes a small shareable `.json` (mod names + Nexus ids); Import creates a new profile from
such a file and lists the mods that are not installed on this PC.

## Nexus Mods

The Browse page lists Trending / Latest added / Recently updated mods and searches Nexus Mods for the game
(`minecraftdungeons2`). Browsing works **without an account** through the public GraphQL API; downloads and update
checks need your personal API key:

1. Open the Nexus Mods [API Access](https://www.nexusmods.com/users/myaccount?tab=api%20access) page and create a
   personal key.
2. Paste it on the Settings page ("Verify & save") or during first-run setup. The key is stored DPAPI-encrypted for
   your Windows account in `%LOCALAPPDATA%\DungeonsModLoader\nexus-apikey.bin`, is only ever sent to
   `api.nexusmods.com`, and is masked in the log.
3. **Premium** members download inside the app. **Free** accounts click "Mod Manager Download" on the Nexus website;
   with the "Handle nxm:// links" switch on (Settings), that button opens the download in the app (the app is
   single-instance: a second launch forwards the link to the running one).

Once the app is registered with Nexus Mods (`NexusConstants.AppSlug`), "Log in with Nexus" appears and the key
field moves behind "Advanced". Updates: on startup (at most once an hour) and on demand, installed Nexus mods are
compared with the mod's file list (author "newer version of" chains, then the newest main file); "Update" replaces
the folder in place, keeps the name, enabled state and profile membership, and keeps the last two versions in
`backups\`. API responses and thumbnails are cached under `cache\` with short lifetimes; rate-limit headers are
honoured.

## Installing mods from files

Drop a `.zip`, `.7z` or `.rar` archive (or loose `.pak`/`.ucas`/`.utoc` files, or a folder) onto the window, or use
**Install from file...** on the Installed page.

- The package is extracted to a temporary folder and scanned for mod file sets (`.pak` + `.ucas` + `.utoc` sharing a
  base name; a lone `.pak` is also accepted). Single-folder wrappers such as `Dungeons\Content\Paks\~mods\Name\` are
  stripped automatically.
- When a package offers several file sets or variant folders (`Option A\`, `Option B\`), a picker lets you choose.
- A package that contains only pak sets (plus readme files) is **flattened** into `~mods\<Name>\`. A package that also
  ships data files (textures, configs, `skins\` folders ...) keeps its folder layout, because such mods read those
  folders at runtime.
- If the target folder already exists you are asked whether to replace it (a managed mod keeps its name and
  enabled state) or to install next to it as `<Name> (2)`.
- Nothing is written outside `~mods` and the disabled folder; the new mod starts enabled.

## Milestones

1. Solution skeleton, DI, logging, theme tokens + swatch window, custom window chrome, sidebar navigation
2. Game detection, first-run setup, manifest + reconcile, Installed page with enable/disable, Play button
3. Install pipeline from local archives / drag & drop, uninstall, unit tests
4. Profiles (create/switch/duplicate/delete/export/import) with rollback
5. Nexus Mods: auth, browse/search/detail, downloads, nxm:// handler, update checking
6. Polish: conflict/dependency hints, toasts, empty states, DPI checks
7. Inno Setup installer, `build.ps1`, GitHub release self-update, release docs

## Fonts

The app bundles [Pixelify Sans](https://fonts.google.com/specimen/Pixelify+Sans) and [Inter](https://rsms.me/inter/),
both under the SIL Open Font License (see `src/DungeonsModLoader.App/Assets/Fonts/`).

## License

MIT - see [LICENSE](LICENSE).
