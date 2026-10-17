namespace ESDERomCleaner;

// RetroAchievements: credenciales, cotejo de las ROMs con sus juegos, lista de logros de la ROM enfocada y filtro de ROMs con logros.
public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem raScan = new("Buscar logros de RetroAchievements…") { ToolTipText = "Coteja las ROMs de la colección con RetroAchievements (por hash o por nombre) para saber cuáles tienen logros." };
    private readonly ToolStripMenuItem raCredentials = new("Credenciales de RetroAchievements…");
    private readonly ToolStripMenuItem raOnlyWith = new("Mostrar solo ROMs con logros") { CheckOnClick = true, ToolTipText = "Oculta de la lista las ROMs que no tienen logros en RetroAchievements (hay que haberlas cotejado antes)." };
    private readonly ToolStripMenuItem raCustom = new("Crear sistema custom con las ROMs con logros…") { ToolTipText = "Crea en la carpeta collections de ES-DE el sistema custom «RetroAchievements» con las ROMs cotejadas por hash (solo una lista de rutas: no copia ni mueve nada)." };
    private readonly ToolStripMenuItem raContext = new("Ver logros de RetroAchievements…");
    private RaIndex raIndex = RaIndex.Load();
    private readonly RaBadge raBadge = new() { Visible = false };

    // Logo y logros (totales y conseguidos) de la ROM enfocada, sobre el panel de medios. Los conseguidos solo si hay credenciales.
    private void UpdateRaBadge()
    {
        if (roms.SelectedItem is RomItem item && raIndex.Match(item.Row) is { Achievements: > 0 } match)
        {
            bool user = false;
            try { user = RaCredentialStore.Load().IsComplete; } catch { }
            // El progreso de tu cuenta se lee entero de una vez (hardcore, softcore y premio de cada juego) y se refresca pasadas unas horas.
            // Un juego que no aparece en esa lectura es un juego en el que todavía no has conseguido nada.
            bool fresh = DateTime.Now - raIndex.ProgressTime < TimeSpan.FromHours(6);
            var progress = !user ? null : raIndex.Progress(match.GameId) ?? (fresh ? new RaGameProgress() : null);
            // Trofeo a la izquierda del logo: dorado si está «mastered» y plateado si está «beaten».
            raBadge.SetAchievements(match.Achievements, !user ? null : progress is null ? (-1, -1) : (progress.Hardcore, progress.SoftcoreOnly), progress?.Kind ?? "");
            raBadge.Visible = true;
            if (user && !fresh) _ = RefreshRaProgressAsync();
        }
        else raBadge.Visible = false;
    }

    private void WireRetroAchievements()
    {
        raCredentials.Click += (_, _) =>
        {
            if (RaAccount.Edit(this, trace, RaCredentialStore.Load()) is not null) { status.Text = "Credenciales de RetroAchievements guardadas."; UpdateRaBadge(); }
        };
        raScan.Click += async (_, _) => await OpenRaScanAsync(null);
        raOnlyWith.CheckedChanged += (_, _) => RefreshList();
        raCustom.Click += async (_, _) => await CreateRaCustomSystemAsync();
        raContext.Click += async (_, _) => await ShowRaAchievementsAsync();
        roms.ContextMenuStrip!.Items.Add(raContext);
        roms.ContextMenuStrip.Opening += (_, _) => raContext.Enabled = roms.SelectedItem is RomItem item && raIndex.Match(item.Row) is not null;
    }

    private int raFetching;
    private DateTime raProgressFailedAt;

    // Lee de RetroAchievements el progreso de todos tus juegos (logros, hardcore y premio) y actualiza la insignia.
    private async Task RefreshRaProgressAsync()
    {
        if (DateTime.Now - raProgressFailedAt < TimeSpan.FromMinutes(5)) return;
        if (Interlocked.CompareExchange(ref raFetching, 1, 0) != 0) return; // ya se está leyendo
        try
        {
            var credentials = RaCredentialStore.Load();
            if (!credentials.IsComplete) return;
            var map = await new RaClient(credentials).GetUserProgressAsync(CancellationToken.None);
            raIndex.SetProgress(map);
            raIndex.Save();
            trace.Add("retroachievements_progress", $"juegos={map.Count}; mastered={map.Values.Count(p => p.Kind == "mastered")}; completed={map.Values.Count(p => p.Kind == "completed")}; " +
                $"beaten_hardcore={map.Values.Count(p => p.Kind == "beaten-hardcore")}; beaten_softcore={map.Values.Count(p => p.Kind == "beaten-softcore")}");
            if (!IsDisposed) UpdateRaBadge();
        }
        catch (Exception ex)
        {
            raProgressFailedAt = DateTime.Now;
            trace.Add("retroachievements_progress_error", ex.Message);
        }
        finally { Interlocked.Exchange(ref raFetching, 0); }
    }

    private async Task OpenRaScanAsync(string? system, bool createCustom = false)
    {
        if (cts != null) return;
        if (!await Requirements.CheckAsync(this, trace, "Buscar logros", Need.RetroAchievements | Need.Internet)) return;
        if (!await EnsureCollectionLoadedAsync())
        {
            if (cts == null) QuietMessage.Show(this, "Buscar logros", "No hay ROMs analizadas: el cotejo trabaja sobre las ROMs de la colección. Analiza la colección primero.");
            return;
        }
        StopVideoPlayback();
        trace.Add("retroachievements_dialog_opened", root.Text);
        bool changed;
        using (var dialog = BusyNotice.Create(this, () => new RaScanDialog(ScrapableRows().ToList(), raIndex, trace, system)))
        {
            dialog.ShowDialog(this);
            changed = dialog.Changed;
        }
        if (!changed) return;
        raIndex = RaIndex.Load();
        status.Text = "Cotejo con RetroAchievements guardado.";
        RefreshList();
        UpdateDataPanel();
        // El sistema custom se mantiene al día: si existe (o se pidió crearlo), se regenera con el nuevo cotejo.
        if (createCustom || File.Exists(RaCustomFile)) WriteRaCustomSystem(showResult: createCustom);
    }

    private async Task ShowRaAchievementsAsync()
    {
        if (roms.SelectedItem is not RomItem item || raIndex.Match(item.Row) is not { } match) return;
        if (!await Requirements.CheckAsync(this, trace, "Ver logros de RetroAchievements", Need.RetroAchievements | Need.Internet)) return;
        StopVideoPlayback();
        trace.Add("retroachievements_achievements_opened", $"system={item.Row.System}; game={match.GameId}");
        using var dialog = new RaAchievementsDialog(item.Row, match, raIndex, trace);
        dialog.ShowDialog(this);
        UpdateDataPanel();
    }

    private bool HasRaAchievements(RomRow row) => raIndex.Match(row) is not null;

    private const string RaCustomName = "RetroAchievements";

    private string RaCustomFile => Path.Combine(root.Text.Trim().Trim('"'), "ES-DE", "collections", $"custom-{RaCustomName}.cfg");

    // Sistema custom de ES-DE: ES-DE\collections\custom-RetroAchievements.cfg, una ruta por línea (%ROMPATH%/sistema/archivo). No copia ni mueve ROMs,
    // gamelists ni medios: ES-DE los sigue leyendo de cada sistema. Antes de crearlo se pregunta si se quiere repetir el análisis con RetroAchievements
    // para descubrir ROMs nuevas; y cada vez que termina un análisis, si el sistema custom existe se regenera solo.
    private async Task CreateRaCustomSystemAsync()
    {
        if (string.IsNullOrWhiteSpace(root.Text)) return;
        DateTime? last = raIndex.LastScan;
        string question = last is { } when
            ? $"El último análisis de ROMs con RetroAchievements se realizó el {when:dd/MM/yyyy HH:mm}.\r\n\r\n¿Deseas volver a realizarlo para descubrir nuevas ROMs?"
            : "Todavía no se han analizado las ROMs con RetroAchievements.\r\n\r\n¿Deseas analizarlas ahora?";
        if (QuietMessage.Confirm(this, "Sistema custom", question, "Volver a analizar", last is null ? "Cancelar" : "Usar el último análisis"))
        {
            // Al terminar el análisis se escribe el sistema custom (OpenRaScanAsync); si se cierra sin analizar no se hace nada.
            await OpenRaScanAsync(null, createCustom: true);
            return;
        }
        if (last is null) return;
        WriteRaCustomSystem(showResult: true);
    }

    // Escribe el .cfg con las ROMs cotejadas por hash. Devuelve el número de ROMs, o -1 si no se escribió.
    private int WriteRaCustomSystem(bool showResult)
    {
        string romsDir = Path.Combine(root.Text.Trim().Trim('"'), "Roms");
        var lines = new List<string>();
        foreach (var row in ScrapableRows())
        {
            if (row.Path.Length == 0 || raIndex.Match(row) is not { By: "hash" }) continue;
            string rel = Path.GetRelativePath(romsDir, row.Path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) continue;
            lines.Add("%ROMPATH%/" + rel.Replace('\\', '/'));
        }
        if (lines.Count == 0)
        {
            if (showResult) QuietMessage.Show(this, "Sistema custom", "No hay ROMs cotejadas por hash con RetroAchievements. Usa antes «Buscar logros de RetroAchievements…».");
            return -1;
        }
        lines.Sort(StringComparer.OrdinalIgnoreCase);
        string target = RaCustomFile;
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(temp, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
            QuietMessage.Show(this, "Sistema custom", "No se pudo crear el sistema custom: " + ex.Message);
            return -1;
        }
        trace.Add("ra_custom_system_created", $"name={RaCustomName}; roms={lines.Count}");
        RefreshSystemsList();
        status.Text = $"Sistema custom «{RaCustomName}» actualizado con {lines.Count} ROMs.";
        if (showResult) QuietMessage.Show(this, "Sistema custom", $"Creado «custom-{RaCustomName}.cfg» con {lines.Count} ROMs.\r\n\r\nEn ES-DE actívalo en Ajustes de colecciones de juegos › Editar colecciones personalizadas.");
        return lines.Count;
    }
}
