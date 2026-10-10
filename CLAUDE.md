# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ES-DE ROM Cleaner: a Windows Forms desktop tool (.NET 10, `net10.0-windows`, x64 only) that scans an ES-DE (EmulationStation Desktop Edition) collection. With it you can browse ROMs by genre, preview miximages and videos, edit `gamelist.xml` metadata, scrape from ScreenScraper, mirror-clone the collection, and permanently delete ROMs along with their accessory and media files. The UI text, user messages and code comments are in **Spanish**; keep new user-facing strings in Spanish.

## Commands

```bash
dotnet build                       # Debug build -> bin/Debug/net10.0-windows/ESDERomCleaner.exe
dotnet build -c Release
dotnet run                         # launches the GUI
```

There is no test project, linter or solution file. Everything lives in the single `ESDERomCleaner.csproj` in the repo root. The csproj excludes `Entrega_ESDERomCleaner\**\*.cs`, so a delivery copy of the sources can sit inside the folder without being compiled. `Modelfile` is an Ollama model definition (qwen3:8b) and isn't used by the app.

## Expected collection layout

Every operation works on a root folder (it defaults to `C:\Temp\NOVA`, hardcoded in the `RomCleanerForm` constructor) that contains:

- `Roms/<system>/...`: ROM files or directories
- `ES-DE/gamelists/<system>/gamelist.xml`
- `ES-DE/downloaded_media/<system>/<mediatype>/...`: miximages, videos and other media

## Architecture

- **`RomCleanerForm`** is one `partial` class split by concern: `.cs` (fields, options, event wiring), `.Layout.cs`, `.List.cs` (filtering, search, selection), `.Details.cs` (details panel, miximage lookup, metadata refresh), `.Video.cs` (LibVLC preview playback), `.Deletion.cs`, and `.Scraper.cs`. The UI is built in code. There are no `.Designer.cs` or `.resx` files.
- **`CollectionScanner`** produces a `ScanResult`. Each gamelist `<game>` is matched to a physical file by its relative `<path>` first, then falls back to a normalized-name match (`PathRules.Name`: removes diacritics, lowercases, turns `_` into a space). If several files match a name, the result is a `RomStatus.Ambiguous` row with an empty `Path`. Files that no game claims become `AccessoryFile`s (for example `.cue`/`.bin` companions and saves), keyed by `PathRules.AccessoryMatchKey`. When a scan finishes, it writes a CSV cache to `%LOCALAPPDATA%\ESDERomCleaner\database.csv`.
- **`GamelistParser`** is a read-only parser. If the XML is malformed, it falls back to a tolerant regex parser (the "v79" behaviour).
- **`PathRules`** is the shared normalization and containment logic: `Relative`, `Name`, `AccessoryMatchKey`, `Inside`. Matching, deletion and editing all depend on these producing identical keys, so changes here affect every feature.
- **`DeletionService`** works in two phases. `BuildPlan` validates every selected row and records the SHA-256 of each affected gamelist. `Execute` re-checks those hashes, ROM sizes and path safety before touching anything, and aborts with no deletion if anything changed. It then optionally backs up each gamelist (`gamelist.xml.bak_<timestamp>_<guid>`), deletes files **permanently** (no Recycle Bin), and removes the `<game>` entries.
- **`RomMetadataEditor`** (a dense, single-line style file) and the ScreenScraper dialog edit a single `<game>` entry. They require exactly one match by normalized path.
- **`ScreenScraperClient`, `ScreenScraperDialog`, `RomHasher`**: the dialog computes CRC32/MD5/SHA1 (`RomHasher` can hash the contents of an archive) and looks the game up through the ScreenScraper API. The system list is cached in `screenscraper_systems.json`. Credentials are encrypted with DPAPI (`ScreenScraperCredentialStore`, CurrentUser scope). `MaskSecrets` strips them from error text.
- **`MirrorSyncDialog`** clones the root to a destination with `robocopy /MIR` (`/XJ`, `/FFT`, `/COPY:DAT`, ...) and validates that the source and destination don't overlap.
- **`TraceLog`** appends JSONL audit records to `%LOCALAPPDATA%\ESDERomCleaner\traceability.jsonl`, and `TraceViewerDialog` displays them. User-visible actions call `trace.Add("snake_case_action", details)`. Follow this convention for new features.

## Conventions that must be preserved when touching gamelists

- **Wrapped parsing:** ES-DE gamelists may have several root-level elements (for example `<alternativeEmulator>` next to `<gameList>`), which isn't valid XML. Writers therefore remove the `<?xml?>` declaration, wrap the content in a synthetic `<__esde_document>` root, load it with `PreserveWhitespace`, and serialize only the root's child nodes back after the declaration (`LoadWrapped`/`ParseWrapped`/`SerializeWrapped` in `DeletionService`, and equivalents in `RomMetadataEditor`). This keeps unrelated formatting byte-for-byte intact.
- **XML safety:** always use `DtdProcessing.Prohibit` and `XmlResolver = null`.
- **Atomic writes:** write to a temp file in the same directory, re-parse it to validate, then `File.Move(temp, target, overwrite: true)`. The same pattern is used for `settings.json`, the CSV cache and credentials. Gamelists are written as UTF-8 without a BOM. The CSV cache is written with a BOM.
- **Path safety for destructive operations:** every target must satisfy `PathRules.Inside` for its system folder, and `HasReparsePointInPath` must be false (symlinks and junctions are refused).
- Gamelist backups are opt-in. The setting `CreateGamelistBackups` is stored in `%LOCALAPPDATA%\ESDERomCleaner\settings.json` along with `ShowVideos` and `VideoSound`.

## Estado del scraper de ScreenScraper

- Hecho: consulta de una ROM por hash y vista de metadatos y medios. No descarga nada.
- El parseo del JSON de jeuInfos.php se escribió sin probarlo contra la API real:
  verificarlo con respuestas reales.
- Pendiente: descarga de medios (elegir tipos, región y calidad), guardado en
  ES-DE/downloaded_media y actualización del gamelist.xml sin pisar datos
  existentes sin avisar.
- Las credenciales nunca se escriben en trazas, JSON mostrado ni repositorio.
- Respetar las cuotas de la API.