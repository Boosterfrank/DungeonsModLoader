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
- `--data-dir <folder>` uses an isolated app data folder (settings, manifest, logs, cache) instead of `%LOCALAPPDATA%\DungeonsModLoader`.

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
