# Nexus Mods page

Text and settings for the DungeonsModLoader page on Nexus Mods (Minecraft Dungeons II section).
`description.bbcode` is the page description (paste it into the BBCode editor).

| Field | Value |
| --- | --- |
| Name | DungeonsModLoader - Mod Manager for Minecraft Dungeons II |
| Summary | Free, open-source mod manager: browse and install Nexus mods in one click, enable/disable, profiles, mod updates, nxm:// links, Steam and Xbox app installs. Never touches game files. |
| Category | Utilities |
| Classification | Mod manager / utility (not a mod) |
| Language | English |
| Version | same as `Directory.Build.props` (`0.1.3`) |
| Tags | Utilities for Players, Utilities for Modders |
| Permissions | Open source (MIT): others may redistribute and modify with credit; assets (fonts) under their own OFL licences |
| Files | `DungeonsModLoader-Setup-<version>.exe` (main file) and the matching `.sha256` as a miscellaneous file; one zip of both if the site refuses .exe |
| Images | `nexus/images/*.png` (Installed, Browse, mod page, Profiles, Settings) |

Releasing a new version: upload the new installer as the main file (mark the old one as old), set the page version,
and add a line to the Changelog tab (copy the GitHub release notes).
