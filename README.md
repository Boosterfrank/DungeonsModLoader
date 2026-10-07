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
(`minecraftdungeons2`). Clicking a mod opens it as a full page (picture, Description / Files / Requirements tabs,
Install, Open on Nexus Mods; Back or Esc returns to the list). Browsing works **without an account** through the
public GraphQL API; downloads and update checks need your personal API key.

**Connecting the account** is one guided dialog, reachable from the Browse page, Settings, the first-run wizard, or
simply by clicking Install on a mod (the download continues once connected):

1. "Open the API keys page" opens your Nexus Mods account page in the browser (log in if asked).
2. Scroll to *Personal API Key*, click *Request an API key* if there is none yet, then *Copy*.
3. Click *Paste* in the dialog and *Connect*. The key is checked with Nexus and stored DPAPI-encrypted for your
   Windows account in `%LOCALAPPDATA%\DungeonsModLoader\nexus-apikey.bin`; it is only ever sent to
   `api.nexusmods.com` and is masked in the log.

**Downloading**: Premium members download inside the app. For free accounts the app opens the file's download page on
the Nexus website; click **"Slow download"** there (or **"Mod Manager Download"** if you see the file list) and, if the
browser asks, allow it to open DungeonsModLoader. The file then comes back through an `nxm://` link and the install
continues on its own (a "Waiting for Nexus Mods..." notice stays in the corner meanwhile). For that to work the app
must be the handler for `nxm://` links; the first free download offers to turn this on (Settings > "Handle nxm://
links" shows which program currently has it, e.g. Vortex, and lets you switch back). The app is single-instance: a
second launch forwards the link to the running one.

Once the app is registered with Nexus Mods (`NexusConstants.AppSlug`), "Log in with Nexus" appears next to the key
steps. Updates: on startup (at most once an hour) and on demand, installed Nexus mods are compared with the mod's
file list (author "newer version of" chains, then the newest main file); "Update" replaces the folder in place,
keeps the name, enabled state and profile membership, and keeps the last two versions in `backups\`. API responses
and thumbnails are cached under `cache\` with short lifetimes; rate-limit headers are honoured.

## Hints and notifications

- **Dependency hints**: a Nexus mod's requirements are recorded when it is installed (and fetched from the public
  API for mods that have none recorded). When a required mod is not installed or is disabled, the row shows a
  "Needs X" badge; clicking it opens the mod on the Browse page, or enables it when it is only disabled.
- **Conflict hints**: when two *enabled* mods contain `.pak/.ucas/.utoc` files with the same base name, both rows
  show "Conflicts with ..." with the shared names in the tooltip (the game loads only one of them).
- **Toasts** in the bottom-right report installs, updates, update-check results and the "waiting for Nexus Mods"
  state; dialogs are only used for questions and for errors with details.

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
