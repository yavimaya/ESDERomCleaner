namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    // El tipo de vídeo que se propone por defecto es el último que se eligió en la descarga masiva (normalizado si nunca se ha elegido).
    private static bool PreferNormalizedVideo() => BulkMediaSettings.Load(true).NormalizedVideo;

    // «preselected»: sistemas que se abren ya marcados (viene de la propuesta tras analizar, con la colección recién cargada).
    // «onlyRows»: la descarga trabaja solo con esas ROMs (de uno o varios sistemas), todas marcadas; es el caso de las ROMs recién añadidas.
    private async Task OpenBulkMediaDialogAsync(IReadOnlyCollection<string>? preselected = null, IReadOnlyList<RomRow>? onlyRows = null, BulkPreset preset = BulkPreset.Saved)
    {
        if (!await Requirements.CheckAsync(this, trace, "Descarga masiva de medios", Need.ScreenScraper | Need.Internet)) return;
        if (onlyRows is not null) preselected = onlyRows.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (preselected is null)
        {
            if (!await EnsureCollectionLoadedAsync())
            {
                if (cts == null) QuietMessage.Show(this, "Descarga masiva de medios", "No hay ROMs analizadas: la descarga masiva trabaja sobre las ROMs de la colección. Copia ROMs en Roms\\<sistema> y analiza la colección.");
                return;
            }
            // Los sistemas con ROMs pero sin gamelist no tienen filas que scrapear: se ofrece crear sus gamelists primero.
            await CreateGamelistsAsync(scanAfter: true, quiet: true, offerScrape: false, reason: "Hay sistemas con ROMs pero sin gamelist.xml, y la descarga masiva solo trabaja con los juegos que figuran en un gamelist.");
        }
        StopVideoPlayback();
        trace.Add("bulk_media_dialog_opened", root.Text);
        using var dialog = new BulkMediaDialog((onlyRows?.Where(r => EsDeSystemExtensions.IsGameFile(r.System, r.Path)) ?? ScrapableRows()).ToList(), root.Text, trace, PreferNormalizedVideo(), createGamelistBackups, preselected, preset);
        dialog.OpenSingleHandler = (row, owner) =>
        {
            trace.Add("screenscraper_dialog_opened", $"system={row.System}; rom={row.RomName}; desde=descarga_masiva");
            using var single = new ScreenScraperDialog(row, root.Text, trace, PreferNormalizedVideo(), createGamelistBackups);
            single.ShowDialog(owner);
            return (single.CollectionMediaChanged, single.MetadataChanged);
        };
        dialog.ShowDialog(this);
        if (!dialog.MetadataChanged && !dialog.CollectionChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        // Hubo cambios (metadatos, medios o idiomas): se vuelve a analizar para ver ya las categorías, imágenes y vídeos nuevos.
        await ScanAsync(offerUnclassified: false);
        ShowDetails();
    }

    private async Task OpenScraperDialogAsync()
    {
        if (roms.SelectedItem is not RomItem item) return;
        if (!await Requirements.CheckAsync(this, trace, "Scrapear con ScreenScraper", Need.ScreenScraper | Need.Internet)) return;
        if (roms.SelectedItem is not RomItem) return;
        StopVideoPlayback();
        trace.Add("screenscraper_dialog_opened", $"system={item.Row.System}; rom={item.Row.RomName}");
        using var dialog = new ScreenScraperDialog(item.Row, root.Text, trace, PreferNormalizedVideo(), createGamelistBackups);
        dialog.ShowDialog(this);
        if (!dialog.CollectionMediaChanged && !dialog.MetadataChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        // Si se escribieron metadatos en el gamelist.xml (juego elegido por nombre), se vuelve a analizar para verlos.
        if (dialog.MetadataChanged) { status.Text = "Metadatos actualizados: se vuelve a analizar la colección…"; _ = ScanAsync(offerUnclassified: false); return; }
        ShowDetails();
    }
}

public sealed partial class RomCleanerForm
{
    // Categorizar: descarga solo los metadatos (categoría incluida) y los idiomas de los sistemas indicados, en una ventana con la barra de
    // progreso y lo que se está haciendo (sin el formulario de scrapeo). Al terminar se vuelve a analizar la colección.
    private async Task CategorizeAsync(IReadOnlyCollection<string> systemNames, bool onlyMissingCategory = false)
    {
        if (!await Requirements.CheckAsync(this, trace, "Categorizar", Need.ScreenScraper | Need.Internet)) return;
        StopVideoPlayback();
        trace.Add("categorize_started", $"systems={string.Join(",", systemNames)}");
        var rows = ScrapableRows().Where(r => systemNames.Contains(r.System, StringComparer.OrdinalIgnoreCase) && (!onlyMissingCategory || NoCategory(r))).ToList();
        bool changed; string result;
        using (var dialog = new CategorizeDialog(rows, systemNames.ToList(), root.Text, trace, createGamelistBackups))
        {
            dialog.ShowDialog(this);
            changed = dialog.MetadataChanged; result = dialog.Result;
        }
        status.Text = result;
        if (!changed) return;
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        await ScanAsync(offerUnclassified: false);
        status.Text = result;
    }
}

public sealed partial class RomCleanerForm
{
    private async Task OpenCurationDialogAsync()
    {
        if (!await EnsureCollectionLoadedAsync())
        {
            if (cts == null) QuietMessage.Show(this, "Curado", "No hay ROMs analizadas: las reglas de curado trabajan sobre las ROMs de la colección. Copia ROMs en Roms\\<sistema> y analiza la colección.");
            return;
        }
        trace.Add("curation_dialog_opened", root.Text);
        RomRow[] toDelete;
        List<RomRow> excludedRows;
        using (var dialog = new CurationDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), trace, createGamelistBackups))
        {
            // El borrado se hace sin salir del curado: los avisos salen sobre él y el re-listado de la ventana principal va en segundo plano.
            dialog.DeleteHandler = (rows, owner, report) => DeleteRowsAsync(rows, owner, report);
            dialog.ShowDialog(this);
            toDelete = dialog.RowsForDeletion.ToArray();
            excludedRows = dialog.ExcludedRows.ToList();
        }
        // Las ROMs excluidas de la colección desde el curado se quitan ya de la ventana principal (sin volver a analizar).
        RemoveRowsFromCollection(excludedRows);
        // Si en el formulario se eligió eliminar candidatas, el borrado se hace ya fuera de él, con el flujo de siempre.
        if (toDelete.Length > 0) await DeleteRowsAsync(toDelete);
    }
}
