# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ES-DE ROM Cleaner: a Windows Forms desktop tool (.NET 10, `net10.0-windows`, x64 only) that scans an ES-DE (EmulationStation Desktop Edition) collection. With it you can browse ROMs by genre, preview miximages and videos, edit `gamelist.xml` metadata, scrape media and metadata from ScreenScraper (one ROM or whole systems in bulk), "curate" the romset (find candidate ROMs to discard by genre, keywords and language), mirror-clone the collection, and permanently delete ROMs along with their accessory and media files. The UI text, user messages and code comments are in **Spanish**; keep new user-facing strings in Spanish.

## Commands

```bash
dotnet build                       # Debug build -> bin/Debug/net10.0-windows/ESDERomCleaner.exe
dotnet build -c Release
dotnet run                         # launches the GUI

# Release: a single self-contained exe (LibVLC bundled and extracted to %TEMP%\.net on first run)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o <dir>
```

The version lives in `ESDERomCleaner.csproj` (`<Version>`); releases are tagged `vX.Y.Z` on GitHub and the repo is public (https://github.com/yavimaya/ESDERomCleaner, default branch `main`, GPL-3.0).

There is no test project, linter or solution file. Everything lives in the single `ESDERomCleaner.csproj` in the repo root. The csproj excludes `Entrega_ESDERomCleaner\**\*.cs`, so a delivery copy of the sources can sit inside the folder without being compiled. `Modelfile` is an Ollama model definition (qwen3:8b) and isn't used by the app.

Working preference: after a change, close any running `ESDERomCleaner.exe`, build in the usual `bin\Debug` folder and relaunch the exe. If the user says not to close the app (a long bulk download may be running), only verify with `dotnet build -o <scratch dir>` and wait for their go-ahead.

## Expected collection layout

Every operation works on a root folder that contains:

- `Roms/<system>/...`: ROM files or directories
- `ES-DE/gamelists/<system>/gamelist.xml`
- `ES-DE/downloaded_media/<system>/<mediatype>/...`: miximages, videos and other media

The root defaults to `C:\Temp\NOVA` (`DefaultRoot`) the first time; afterwards the last root that scanned successfully is remembered in `settings.json`. `ValidateRoot` checks the path (exists, has `Roms` and `ES-DE\gamelists`) before scanning and shows a clear message.

## Architecture

- **`RomCleanerForm`** is one `partial` class split by concern: `.cs` (fields, options, event wiring), `.Layout.cs` (menus: Opciones, Scrapeo, Curado, Trazabilidad), `.List.cs` (filtering, search, selection, system list), `.Details.cs`, `.Video.cs` (LibVLC preview playback), `.Deletion.cs`, `.Scraper.cs` (opens the scraper and bulk dialogs) and `.Curation.cs`. The UI is built in code. There are no `.Designer.cs` or `.resx` files. The option "Mostrar sistemas accesorios" hides non-game folders (`androidapps`, `emulators`, `desktop`; `AccessorySystemNames`) from the system list and from the bulk and curation dialogs.
- **`CollectionScanner`** produces a `ScanResult`. Each gamelist `<game>` is matched to a physical file by its relative `<path>` first, then falls back to a normalized-name match (`PathRules.Name`). If several files match a name, the result is a `RomStatus.Ambiguous` row with an empty `Path`. Files that no game claims become `AccessoryFile`s, keyed by `PathRules.AccessoryMatchKey`. When a scan finishes, it writes a CSV cache to `%LOCALAPPDATA%\ESDERomCleaner\database.csv`.
- **`GamelistParser`** is a read-only parser with a tolerant regex fallback (the "v79" behaviour).
- **`PathRules`** is the shared normalization and containment logic: `Relative`, `Name`, `AccessoryMatchKey`, `Inside`. Matching, deletion and editing all depend on these producing identical keys. `RomKey.Of(row)` (in `RomLanguages.cs`) is the stable per-ROM key (`system|normalized gamelist path`) used for exclusions and the language index.
- **`DeletionService`** works in two phases. `BuildPlan` validates every selected row and records the SHA-256 of each affected gamelist. `Execute` re-checks those hashes, ROM sizes and path safety before touching anything, and aborts with no deletion if anything changed. It then optionally backs up each gamelist, deletes files **permanently** (no Recycle Bin), and removes the `<game>` entries. `HasReparsePointInPath` is `internal` and reused by the media installer and the gamelist writer. `RomCleanerForm.DeleteRowsAsync(RomRow[])` is the single deletion flow (plan, summary + confirmation, execute, partial rescan); both the main selection and the curation dialog use it.
- **`RomMetadataEditor`** (a dense, single-line style file) edits a single `<game>`: compact 4-column grid, genre combo filled with the collection's genres, file name read-only, "Guardar" enabled only when something changed, fixed-size window fitted to its content.
- **`MirrorSyncDialog`** clones the root with `robocopy /MIR` and validates that source and destination don't overlap.
- **`TraceLog`** appends JSONL audit records to `traceability.jsonl` (thread-safe: bulk runs write from several threads). User-visible actions call `trace.Add("snake_case_action", details)`. Follow this convention for new features.

### ScreenScraper

- **`ScreenScraperClient`**: system list (cached 30 days in `screenscraper_systems.json`), `LookupAsync` (hash), `LookupByGameIdAsync`, `SearchGamesAsync` (`jeuRecherche.php`, name search), `RefreshUserAsync` (`ssuserInfos.php`, reads threads/quota), `DownloadMediaAsync` (temp file + MD5 check + atomic move; no MD5 check for non-game media such as pictograms). `ScreenScraperQuota` keeps the account counters (`screenscraper_quota.json`, `MaxThreads` comes from the server, i.e. the membership) and `ScreenScraperQuotaStrip` is the bottom bar shown in every ScreenScraper form (one fixed-width cell per concept).
- **Credentials** are managed only from the main window: menu **Scrapeo › Credenciales de ScreenScraper…** and **Alta en ScreenScraper…** (opens `https://www.screenscraper.fr/membreinscription.php`). No ScreenScraper form has its own credentials button; if they are missing at open, `ScreenScraperAccount.Ensure` offers to register them or sign up. They are DPAPI-encrypted (`ScreenScraperCredentialStore`) and never written to traces or shown.
- **`SsMediaSelector`** holds the shared rules to pick media per ES-DE folder: ROM region first (or no region), fallback regions `wor, eu, us, jp`, type order per folder (`videos`: normalized or original; `miximages`: `mixrbv2` only in bulk). `SsSystemResolver.Find` maps an ES-DE folder to a ScreenScraper system: explicit ids first (`FolderIds`: `mame`, `arcade`, `fba`, `fbneo`, `cps*` go to the generic arcade system #75, shown as "Arcade (MAME)"; `snes-msu1` to #210), then RetroPie/Recalbox/common names (these fields can be comma-separated lists; more than 50 arcade systems share `mame`/`arcade`, so never rely on name matching for arcade), then `FolderAliases` (regional or hack variants such as `megacdjp`, `wswanc`, `gbh` point to their base folder). When several systems match a name, the one matching RetroPie and Recalbox with the lowest id wins (`snes` is also used by Satellaview and others). Folders that stay unresolved are skipped in the bulk run and shown as "No reconocido".
- **`ScreenScraperDialog`** (single ROM, right click › Scrapear): it queries automatically on open (button "Refrescar consulta"), shows the system only if it can't be deduced or the ROM isn't recognized by hash, and has tabs Metadatos, Medios disponibles (download all/one into `%LOCALAPPDATA%\ESDERomCleaner\media_cache\<gameId>` for comparison, preview image/video with `MediaPreviewPane`), **Elegir y guardar en la colección** (`ScreenScraperMediaSelectionPanel`) and Respuesta JSON. Nothing is checked by default; replaced files go to `media_backups`.
- **`EsDeMediaInstaller`** copies a media file into `ES-DE/downloaded_media/<system>/<folder>/<rom name>.<ext>` (same relative subfolder as the ROM), atomically, with path-safety checks, moving any existing media of that stem to `%LOCALAPPDATA%\ESDERomCleaner\media_backups\<stamp>\...` (optional in bulk). ES-DE only reads: `3dboxes, backcovers, covers, custom, fanart, manuals, marquees, miximages, physicalmedia, screenshots, titlescreens, videos`; other ScreenScraper types (bezels, box sides…) have no folder.
- **Bulk download** (menu **Scrapeo › Descarga masiva de medios…**, `BulkMediaDialog` + `BulkMediaRunner`): pick systems and items (media folders, **Metadatos**, **Idiomas (para el curado)**), video type, overwrite mode (`No` / `Always` / `IfSizeDiffers`), optional backup, name search fallback. It runs ROMs in parallel with as many threads as the membership allows (`ScreenScraperQuota.MaxThreads`), is resumable (without overwrite, ROMs already complete in destination aren't even queried; `ExistingMediaIndex` reads each folder once), stops on quota/ban errors, shows total % in the title and a throttled (5 s) remaining-time estimate, and ends with `BulkResultDialog` (space saved/added per media type). Metadata is written to each `gamelist.xml` once per file by `GamelistMetadataWriter` (wrapped parsing, atomic, backup follows the main option). Languages go to `RomLanguageIndex` (`rom_languages.json`), never into gamelists.
- `MediaPreviewPane` (4:3 image/video preview; LibVLC with software decoding and GDI output because the D3D11 path crashed the app in `dxgi.dll` on this machine), `QuietDialogs` (`QuietMessage.Show/Confirm/Prompt` and `MediaSaveConfirmDialog`: dialogs without icon, so **no system sound**; use them instead of `MessageBox` in new scraper/curation UI).

### Curado (menu Curado › Reglas de curado)

Read-only analysis plus deletion of "candidate" ROMs to discard, over all visible systems (per-system choice is made in the main window). `CurationDialog` lists the collection's genres and the **supracategorías** (`SupraDefinition`, editable with `SupraEditorDialog`, stored in `curation_supracategories.json`; defaults come from `CurationRuleSet.Defaults`), plus a built-in language rule. Marking items shows candidates (deduplicated, with the reason), per-system summary, selectable columns, double click on a candidate excludes it (stored in `curation.json`), named **profiles** (`curation_profiles.json`: checked rules, exclusions and supracategories) and CSV export. The button "Eliminar N candidatas…" confirms and hands the rows to the main window's `DeleteRowsAsync`. Languages: gamelists have no language field, so they come from ScreenScraper (bulk option "Idiomas") with fallbacks from file-name tags/region (`LanguageResolver`); the user chooses understood languages in `CurationLanguagesDialog` (`curation_languages.json`).

## Conventions that must be preserved when touching gamelists

- **Wrapped parsing:** ES-DE gamelists may have several root-level elements, which isn't valid XML. Writers therefore remove the `<?xml?>` declaration, wrap the content in a synthetic `<__esde_document>` root, load it with `PreserveWhitespace`, and serialize only the root's child nodes back after the declaration (`LoadWrapped`/`ParseWrapped`/`SerializeWrapped` in `DeletionService`, equivalents in `RomMetadataEditor` and `GamelistMetadataWriter`). This keeps unrelated formatting byte-for-byte intact.
- **XML safety:** always use `DtdProcessing.Prohibit` and `XmlResolver = null`.
- **Atomic writes:** write to a temp file in the same directory, re-parse it to validate, then `File.Move(temp, target, overwrite: true)`. The same pattern is used for every JSON store in `%LOCALAPPDATA%\ESDERomCleaner`, the CSV cache and credentials. Gamelists are written as UTF-8 without a BOM. The CSV cache and exported CSVs are written with a BOM.
- **Path safety for destructive operations:** every target must satisfy `PathRules.Inside` for its system folder, and `HasReparsePointInPath` must be false (symlinks and junctions are refused).
- Gamelist backups are opt-in (`CreateGamelistBackups` in `settings.json`, which also stores `ShowVideos`, `VideoSound`, `ShowAccessorySystems` and `RootPath`).

## Datos de la aplicación (`%LOCALAPPDATA%\ESDERomCleaner`)

`settings.json`, `database.csv`, `traceability.jsonl`, `screenscraper_systems.json`, `screenscraper_quota.json`, credenciales cifradas, `bulk_media.json` (preferencias de la descarga masiva; incluye el tipo de vídeo por defecto), `media_cache\`, `media_backups\`, `rom_languages.json`, `curation.json`, `curation_supracategories.json`, `curation_profiles.json`, `curation_languages.json`.

## Estado del scraper y del curado

- Hecho y probado por el usuario: consulta por hash, vista de medios, descarga y comparación, guardado en `downloaded_media`, descarga masiva de medios (con metadatos), cuotas y comprobación del MD5.
- Sin probar contra la API real: la búsqueda por nombre (`jeuRecherche.php`), la lectura de la cuenta (`ssuserInfos.php`), la lectura de idiomas de la ROM (`rom.langues`, algunas ROMs no los traen) y el formato del género por jerarquía en los metadatos.
- Pendiente: visor con flechas al pulsar la imagen o el vídeo del formulario principal; qué hacer con las candidatas del curado además de borrarlas (ocultarlas con `hidden` en el gamelist o generar una copia depurada con la clonación); contar con los idiomas reales una vez descargados.
- Las credenciales nunca se escriben en trazas, JSON mostrado ni repositorio.
- Respetar las cuotas de la API: las descargas se hacen con los hilos que permite la membresía y se detienen ante errores de cuota o bloqueo.
