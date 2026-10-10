namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    private void OpenScraperDialog()
    {
        if (roms.SelectedItem is not RomItem item) return;
        StopVideoPlayback();
        trace.Add("screenscraper_dialog_opened", $"system={item.Row.System}; rom={item.Row.RomName}");
        using var dialog = new ScreenScraperDialog(item.Row, trace);
        dialog.ShowDialog(this);
    }
}
