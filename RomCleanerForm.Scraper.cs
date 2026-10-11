namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    // El tipo de vídeo que se propone por defecto es el último que se eligió en la descarga masiva (normalizado si nunca se ha elegido).
    private static bool PreferNormalizedVideo() => BulkMediaSettings.Load(true).NormalizedVideo;

    // «preselected»: sistemas que se abren ya marcados (viene de la propuesta tras analizar, con la colección recién cargada).
    // «onlyRows»: la descarga trabaja solo con esas ROMs (de uno o varios sistemas), todas marcadas; es el caso de las ROMs recién añadidas.
    private async Task OpenBulkMediaDialogAsync(IReadOnlyCollection<string>? preselected = null, IReadOnlyList<RomRow>? onlyRows = null, BulkPreset preset = BulkPreset.Saved)
    {
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
        using var dialog = new BulkMediaDialog(onlyRows?.ToList() ?? all.Where(r => IsVisibleSystem(r.System)).ToList(), root.Text, trace, PreferNormalizedVideo(), createGamelistBackups, preselected, preset);
        dialog.ShowDialog(this);
        if (!dialog.MetadataChanged && !dialog.CollectionChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        // Hubo cambios (metadatos, medios o idiomas): se vuelve a analizar para ver ya las categorías, imágenes y vídeos nuevos.
        await ScanAsync(offerUnclassified: false);
        ShowDetails();
    }

    private void OpenScraperDialog()
    {
        if (roms.SelectedItem is not RomItem item) return;
        StopVideoPlayback();
        trace.Add("screenscraper_dialog_opened", $"system={item.Row.System}; rom={item.Row.RomName}");
        using var dialog = new ScreenScraperDialog(item.Row, root.Text, trace, PreferNormalizedVideo());
        dialog.ShowDialog(this);
        if (!dialog.CollectionMediaChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        ShowDetails();
    }
}
