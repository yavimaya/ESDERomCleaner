namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
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
