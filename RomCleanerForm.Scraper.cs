namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    // El tipo de vídeo que se propone por defecto es el último que se eligió en la descarga masiva (normalizado si nunca se ha elegido).
    private static bool PreferNormalizedVideo() => BulkMediaSettings.Load(true).NormalizedVideo;

    private void OpenBulkMediaDialog()
    {
        if (all.Count == 0)
        {
            QuietMessage.Show(this, "Descarga masiva de medios", "Primero analiza la colección: la descarga masiva trabaja sobre las ROMs de los sistemas analizados.");
            return;
        }
        StopVideoPlayback();
        trace.Add("bulk_media_dialog_opened", root.Text);
        using var dialog = new BulkMediaDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), root.Text, trace, PreferNormalizedVideo(), createGamelistBackups);
        dialog.ShowDialog(this);
        if (dialog.MetadataChanged) status.Text = "Metadatos actualizados en los gamelist.xml; analiza de nuevo la colección para verlos en la lista.";
        if (!dialog.CollectionChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
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
