namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    private async Task OpenCurationDialogAsync()
    {
        if (all.Count == 0)
        {
            QuietMessage.Show(this, "Curado", "Primero analiza la colección: las reglas de curado trabajan sobre las ROMs de los sistemas analizados.");
            return;
        }
        trace.Add("curation_dialog_opened", root.Text);
        RomRow[] toDelete;
        using (var dialog = new CurationDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), trace))
        {
            dialog.ShowDialog(this);
            toDelete = dialog.RowsForDeletion.ToArray();
        }
        // Si en el formulario se eligió eliminar candidatas, el borrado se hace ya fuera de él, con el flujo de siempre.
        if (toDelete.Length > 0) await DeleteRowsAsync(toDelete);
    }
}
