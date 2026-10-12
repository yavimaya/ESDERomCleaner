namespace ESDERomCleaner;

// RetroAchievements: credenciales, cotejo de las ROMs con sus juegos, lista de logros de la ROM enfocada y filtro de ROMs con logros.
public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem raScan = new("Buscar logros de RetroAchievements…") { ToolTipText = "Coteja las ROMs de la colección con RetroAchievements (por hash o por nombre) para saber cuáles tienen logros." };
    private readonly ToolStripMenuItem raCredentials = new("Credenciales de RetroAchievements…");
    private readonly ToolStripMenuItem raOnlyWith = new("Mostrar solo ROMs con logros") { CheckOnClick = true, ToolTipText = "Oculta de la lista las ROMs que no tienen logros en RetroAchievements (hay que haberlas cotejado antes)." };
    private readonly ToolStripMenuItem raContext = new("Ver logros de RetroAchievements…");
    private RaIndex raIndex = RaIndex.Load();

    private void WireRetroAchievements()
    {
        raCredentials.Click += (_, _) =>
        {
            if (RaAccount.Edit(this, trace, RaCredentialStore.Load()) is not null) status.Text = "Credenciales de RetroAchievements guardadas.";
        };
        raScan.Click += async (_, _) => await OpenRaScanAsync(null);
        raOnlyWith.CheckedChanged += (_, _) => RefreshList();
        raContext.Click += (_, _) => ShowRaAchievements();
        roms.ContextMenuStrip!.Items.Add(raContext);
        roms.ContextMenuStrip.Opening += (_, _) => raContext.Enabled = roms.SelectedItem is RomItem item && raIndex.Match(item.Row) is not null;
    }

    private async Task OpenRaScanAsync(string? system)
    {
        if (cts != null) return;
        if (!await EnsureCollectionLoadedAsync())
        {
            if (cts == null) QuietMessage.Show(this, "Buscar logros", "No hay ROMs analizadas: el cotejo trabaja sobre las ROMs de la colección. Analiza la colección primero.");
            return;
        }
        StopVideoPlayback();
        trace.Add("retroachievements_dialog_opened", root.Text);
        bool changed;
        using (var dialog = new RaScanDialog(ScrapableRows().ToList(), raIndex, trace, system))
        {
            dialog.ShowDialog(this);
            changed = dialog.Changed;
        }
        if (!changed) return;
        raIndex = RaIndex.Load();
        status.Text = "Cotejo con RetroAchievements guardado.";
        RefreshList();
        UpdateDataPanel();
    }

    private void ShowRaAchievements()
    {
        if (roms.SelectedItem is not RomItem item || raIndex.Match(item.Row) is not { } match) return;
        StopVideoPlayback();
        trace.Add("retroachievements_achievements_opened", $"system={item.Row.System}; game={match.GameId}");
        using var dialog = new RaAchievementsDialog(item.Row, match, raIndex, trace);
        dialog.ShowDialog(this);
        UpdateDataPanel();
    }

    private bool HasRaAchievements(RomRow row) => raIndex.Match(row) is not null;
}
