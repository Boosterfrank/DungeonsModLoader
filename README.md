# DungeonsModLoader

A Windows mod manager for **Minecraft Dungeons II** (Steam) with **Nexus Mods** as its online mod library.

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

Run the app with `--swatch` to open the theme swatch window (design tokens, typography, control gallery).

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
design-reference/             Visual reference material for the theme (read-only input)
```

## How mods are stored

- Enabled mods: `<GameRoot>\Dungeons\Content\Paks\~mods\<ModFolder>\`
- Disabled mods: `<GameRoot>\Dungeons\DungeonsModLoader_Disabled\<ModFolder>\` (same drive, outside `Paks`, so the game ignores them)
- App data: `%LOCALAPPDATA%\DungeonsModLoader\` (`settings.json`, `manifest.json`, `profiles\`, `cache\`, `downloads\`, `logs\`, `backups\`)

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
