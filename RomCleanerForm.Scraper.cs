namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    private void OpenBulkMediaDialog()
    {
        if (all.Count == 0)
        {
            QuietMessage.Show(this, "Descarga masiva de medios", "Primero analiza la colección: la descarga masiva trabaja sobre las ROMs de los sistemas analizados.");
            return;
        }
        StopVideoPlayback();
        trace.Add("bulk_media_dialog_opened", root.Text);
        using var dialog = new BulkMediaDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), root.Text, trace, normalizedVideo.Checked, createGamelistBackups);
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
        using var dialog = new ScreenScraperDialog(item.Row, root.Text, trace, normalizedVideo.Checked);
        dialog.ShowDialog(this);
        if (!dialog.CollectionMediaChanged) return;
        // Los índices de miximages y vídeos se construyen una vez por carpeta: hay que olvidarlos para ver los nuevos.
        MiximageFileIndex.Clear();
        VideoFileIndex.Clear();
        ShowDetails();
    }
}
