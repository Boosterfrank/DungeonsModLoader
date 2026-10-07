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

## Build the installer

```powershell
.\build.ps1
```

One command: runs the tests, publishes the app as a self-contained single-file executable (`publish\win-x64\`,
no .NET runtime needed on the target PC), compiles the Inno Setup script and writes
`dist\DungeonsModLoader-Setup-<version>.exe` plus a `.sha256` file. Switches: `-SkipTests`, `-SkipInstaller`
(publish only), `-Sign` (code signing; a placeholder until a certificate is set up, see the block in the script).
Requires the .NET 8 SDK and Inno Setup 6 (`ISCC.exe` is looked up in the usual install folders and on `PATH`).

The installer (`installer\setup.iss`):

- installs per user into `%LOCALAPPDATA%\Programs\DungeonsModLoader` (no admin rights), with a Start menu entry and an
  optional desktop shortcut;
- offers to open Nexus Mods `nxm://` links with the app. The task is pre-selected when no other program handles
  them and left off when another mod manager (e.g. Vortex) does, so an install never takes the links over silently;
- shows the licence, offers "Launch now", and closes a running copy of the app before replacing it;
- on uninstall **never touches your mods** in the game folder, removes the `nxm://` registration only if it still
  points to this install, and asks whether to delete the app data (`%LOCALAPPDATA%\DungeonsModLoader`).

## Code signing

The installer is not signed yet, so Windows SmartScreen shows "Windows protected your PC" on first run (users click
*More info*, then *Run anyway*). The only way to remove that warning is an Authenticode signature from a
certificate authority Windows trusts; `build.ps1 -Sign` already does the signing (SHA-256, timestamped) once a
certificate is available:

- **Azure Trusted Signing** (Microsoft, pay-as-you-go, individual or organisation validation): certificates with
  SmartScreen reputation built in; signtool uses it through the Trusted Signing dlib.
- **SignPath Foundation**: free signing for open-source projects (public repository required), through SignPath's
  pipeline rather than a local certificate.
- **A code-signing certificate from a CA** (DigiCert, Sectigo, GlobalSign, ...): an EV certificate removes the
  SmartScreen warning immediately; a standard (OV) certificate removes "Unknown publisher" and earns reputation as
  downloads accumulate. Install it (or its hardware token) on the build PC and run
  `.\build.ps1 -Sign -CertificateThumbprint <sha1>` (or `-PfxPath file.pfx`).

Self-signed certificates do not help: Windows does not trust them, so the warning stays.

## Releasing a new version

1. Set `<Version>` in `Directory.Build.props` (the one place the version lives) and commit.
2. Run `.\build.ps1` and test `dist\DungeonsModLoader-Setup-<version>.exe` (fresh install and update over the
   previous version).
3. Tag and publish a GitHub release with the installer attached, for example:

   ```powershell
   git tag v0.2.0
   git push origin main --tags
   gh release create v0.2.0 dist\DungeonsModLoader-Setup-0.2.0.exe dist\DungeonsModLoader-Setup-0.2.0.exe.sha256 --title "DungeonsModLoader 0.2.0" --notes "What changed..."
   ```

The app's **self-update** reads the latest release of `Boosterfrank/DungeonsModLoader` through the GitHub API
(`releases/latest`) every time it starts (can be turned off in Settings) and on demand from Settings > About.
Drafts and pre-releases are ignored; the release's tag (`v0.2.0`) is compared with the running version and the
attached `DungeonsModLoader-Setup-*.exe` is the download. A newer version shows a banner with "What's new" and
"Update now": the installer is downloaded to the app's `downloads\` folder (size-checked) and run silently; the app
closes and the installer brings the new version back up. The banner is optional for two starts; from the third
start on an outdated version the update is mandatory (a one-button notice, then the install).

## Solution layout

```
DungeonsModLoader.sln
build.ps1                     Tests -> publish -> installer (see "Build the installer")
src/
  DungeonsModLoader.App/      WPF app: Views, ViewModels, Themes, Controls, App.xaml
  DungeonsModLoader.Core/     Models, mod store, install/update/profile services, game detection, app updates (no WPF)
  DungeonsModLoader.Nexus/    Nexus Mods API client, SSO, nxm:// link parsing, downloads, DTOs
tests/
  DungeonsModLoader.Core.Tests/
installer/
  setup.iss                   Inno Setup script
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
(`minecraftdungeons2`), 24 per page with page navigation. Clicking a mod opens it as a full page (picture,
Description / Files / Requirements tabs, Install, Open on Nexus Mods; Back or Esc returns to the list). Clicking
the picture shows it large. The page's image gallery is not part of the official API (the `Mod` type exposes only
the header picture), so only that picture and the pictures embedded in the description are shown. Browsing works
**without an account** through the public GraphQL API; downloads and update checks need your personal API key.

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
