namespace ESDERomCleaner;

// Opciones › Orden de regiones del curado…: qué región se prefiere cuando hay varias versiones del mismo juego de consola (regla «Una por juego» del curado).
public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem regionOrderMenu = new("Orden de regiones del curado…")
    {
        ToolTipText = "Región que se conserva cuando hay varias versiones del mismo juego de consola (regla «Una por juego (regiones)» del curado)."
    };

    private void WireRegionOrder()
    {
        regionOrderMenu.Click += (_, _) =>
        {
            using var dialog = new RegionOrderDialog(RegionPreferences.Load());
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            RegionPreferences.Save(dialog.Result);
            trace.Add("region_order_changed", string.Join(",", dialog.Result));
            status.Text = "Orden de regiones guardado: " + string.Join(" › ", dialog.Result.Select(k => Loc.T(RegionPreferences.Label(k))));
        };
    }
}
