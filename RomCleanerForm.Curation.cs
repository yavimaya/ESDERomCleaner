namespace ESDERomCleaner;

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
        using (var dialog = new CurationDialog(all.Where(r => IsVisibleSystem(r.System)).ToList(), trace))
        {
            dialog.ShowDialog(this);
            toDelete = dialog.RowsForDeletion.ToArray();
        }
        // Si en el formulario se eligió eliminar candidatas, el borrado se hace ya fuera de él, con el flujo de siempre.
        if (toDelete.Length > 0) await DeleteRowsAsync(toDelete);
    }
}
