namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    private void OpenCurationDialog()
    {
        if (all.Count == 0)
        {
            QuietMessage.Show(this, "Curado", "Primero analiza la colección: las reglas de curado trabajan sobre las ROMs de los sistemas analizados.");
            return;
        }
        trace.Add("curation_dialog_opened", root.Text);
        using var dialog = new CurationDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), trace);
        dialog.ShowDialog(this);
    }
}
