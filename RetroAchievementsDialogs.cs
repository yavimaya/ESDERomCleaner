using System.Diagnostics;

namespace ESDERomCleaner;

internal static class RaAccount
{
    public const string KeysUrl = "https://retroachievements.org/settings";

    public static void OpenKeysPage(IWin32Window? owner)
    {
        try { Process.Start(new ProcessStartInfo(KeysUrl) { UseShellExecute = true }); }
        catch (Exception ex) { QuietMessage.Show(owner, "RetroAchievements", $"No se pudo abrir el navegador ({ex.Message}).\r\n\r\nAbre esta dirección a mano:\r\n{KeysUrl}"); }
    }

    // Abre el formulario de credenciales y, si se acepta, las guarda cifradas. Devuelve null si se cancela o no se pueden guardar.
    public static RaCredentials? Edit(IWin32Window owner, TraceLog trace, RaCredentials current)
    {
        using var dialog = new RaCredentialsDialog(current);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Result is null) return null;
        try
        {
            RaCredentialStore.Save(dialog.Result);
            trace.Add("retroachievements_credentials_saved", $"user={dialog.Result.UserName}");
        }
        catch (Exception ex)
        {
            QuietMessage.Show(owner, "Credenciales de RetroAchievements", "No se pudieron guardar las credenciales: " + ex.Message);
            return null;
        }
        return dialog.Result;
    }

    // Credenciales vigentes; si faltan, ofrece registrarlas ahora.
    public static RaCredentials? Ensure(IWin32Window owner, TraceLog trace)
    {
        var credentials = RaCredentialStore.Load();
        if (credentials.IsComplete) return credentials;
        if (!QuietMessage.Confirm(owner, "RetroAchievements", "Faltan el usuario y la clave de la Web API de RetroAchievements. ¿Quieres introducirlos ahora?", "Introducir")) return null;
        return Edit(owner, trace, credentials);
    }
}

internal sealed class RaCredentialsDialog : LocalizedForm
{
    private readonly TextBox user = new() { Dock = DockStyle.Fill };
    private readonly TextBox key = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    public RaCredentials? Result { get; private set; }

    public RaCredentialsDialog(RaCredentials current)
    {
        Text = "Credenciales de RetroAchievements";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 200);
        user.Text = current.UserName; key.Text = current.ApiKey;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 80, ColumnCount = 2, RowCount = 2, Padding = new Padding(12, 12, 12, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(int index, string label, Control box)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index);
            grid.Controls.Add(box, 1, index);
        }
        Row(0, "Usuario de RetroAchievements:", user);
        Row(1, "Clave de la Web API:", key);
        var note = new Label
        {
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(12, 4, 90, 0),
            Text = "La clave se copia de tu perfil en retroachievements.org › Ajustes › Claves (Web API Key). Se guarda cifrada con tu usuario de Windows (DPAPI), solo en este equipo."
        };
        var ok = new Button { Text = "Guardar", DialogResult = DialogResult.OK, Location = new Point(300, 162), Size = new Size(80, 28) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(386, 162), Size = new Size(80, 28) };
        ok.Click += (_, _) => Result = new RaCredentials(user.Text.Trim(), key.Text.Trim());
        var open = new Button { Text = "Abrir mis claves", Location = new Point(12, 162), Size = new Size(160, 28) };
        open.Click += (_, _) => RaAccount.OpenKeysPage(this);
        // El avatar del usuario guardado aparece a la derecha de los campos.
        var avatar = new PictureBox { Location = new Point(404, 14), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Control };
        grid.Padding = new Padding(12, 12, 90, 0);
        var avatarName = new Label { Location = new Point(394, 82), Size = new Size(84, 20), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Text = current.UserName, Tag = "noloc" };
        user.TextChanged += (_, _) => avatarName.Text = user.Text.Trim();
        var avatarStats = new Label { Location = new Point(394, 104), Size = new Size(84, 54), TextAlign = ContentAlignment.TopCenter, ForeColor = SystemColors.GrayText, Tag = "noloc" };
        Controls.AddRange([avatar, avatarName, avatarStats, open, ok, cancel, note, grid]);
        avatar.BringToFront();
        if (current.IsComplete)
            Shown += async (_, _) =>
            {
                var client = new RaClient(current);
                try { if (await client.GetAvatarAsync(CancellationToken.None) is { } file) { using var image = Image.FromFile(file); avatar.Image = new Bitmap(image); } } catch { }
                try
                {
                    if (await client.GetUserInfoAsync(CancellationToken.None) is { } info && !IsDisposed)
                        avatarStats.Text = (info.Rank > 0 ? "#" + info.Rank.ToString("N0") : "â€”") + "\r\nHC " + info.HardcorePoints.ToString("N0") + "\r\nSC " + info.SoftcorePoints.ToString("N0");
                }
                catch { }
            };
        AcceptButton = ok; CancelButton = cancel;
    }
}

// Busca en RetroAchievements las ROMs de los sistemas elegidos (por hash o por nombre) y guarda cuáles tienen logros.
internal sealed class RaScanDialog : LocalizedForm
{
    private readonly IReadOnlyList<RomRow> rows;
    private readonly RaIndex index;
    private readonly TraceLog trace;
    private readonly CheckedListBox systemList = new() { CheckOnClick = true, IntegralHeight = false };
    private readonly Label statusLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100 };
    private readonly Button start = new() { Text = "Analizar", Size = new Size(100, 28) };
    private readonly Button close = new() { Text = "Cerrar", Size = new Size(90, 28) };
    private CancellationTokenSource? cts;
    public bool Changed { get; private set; }

    public RaScanDialog(IReadOnlyList<RomRow> rows, RaIndex index, TraceLog trace, string? preselectSystem)
    {
        this.rows = rows; this.index = index; this.trace = trace;
        Text = "Buscar logros — RetroAchievements";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(520, 430);

        var intro = new Label
        {
            Location = new Point(12, 10), Size = new Size(496, 54), AutoSize = false,
            Text = "Marca los sistemas que quieres cotejar con RetroAchievements. Las ROMs de cartucho y los discos de PlayStation, PS2, Sega CD y Saturn (CHD, ISO, BIN/CUE) se identifican por hash; el resto, por el nombre del juego (menos fiable). Lo que no tiene logros queda anotado para el curado."
        };
        systemList.Location = new Point(12, 70); systemList.Size = new Size(496, 250);
        foreach (var group in rows.Where(r => r.Status == RomStatus.Ok && RaConsoles.Of(r.System) is not null).GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            int i = systemList.Items.Add(new SystemItem(group.Key, group.Count(), index.SystemChecked(group.Key)));
            if (preselectSystem is null || group.Key.Equals(preselectSystem, StringComparison.OrdinalIgnoreCase)) systemList.SetItemChecked(i, true);
        }
        var all = new Button { Text = "Todos", Location = new Point(12, 326), Size = new Size(80, 26) };
        var none = new Button { Text = "Ninguno", Location = new Point(98, 326), Size = new Size(80, 26) };
        all.Click += (_, _) => { for (int i = 0; i < systemList.Items.Count; i++) systemList.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < systemList.Items.Count; i++) systemList.SetItemChecked(i, false); };
        statusLabel.Location = new Point(12, 362); statusLabel.Size = new Size(496, 22);
        bar.Location = new Point(12, 388); bar.Size = new Size(300, 20);
        start.Location = new Point(318, 392 - 4); close.Location = new Point(422, 392 - 4);
        Controls.AddRange([intro, systemList, all, none, statusLabel, bar, start, close]);
        CancelButton = close;
        start.Click += async (_, _) => await RunAsync();
        close.Click += (_, _) => { if (cts is not null) { cts.Cancel(); close.Enabled = false; statusLabel.Text = "Cancelando…"; } else Close(); };
        if (systemList.Items.Count == 0) { statusLabel.Text = "Ningún sistema de la colección tiene consola en RetroAchievements."; start.Enabled = false; }
    }

    private sealed record SystemItem(string System, int Roms, bool Done)
    {
        public override string ToString() => $"{System} — {Roms:N0} ROMs" + (Done ? " (ya analizado)" : "");
    }

    private async Task RunAsync()
    {
        var chosen = systemList.CheckedItems.Cast<SystemItem>().Select(i => i.System).ToList();
        if (chosen.Count == 0) { statusLabel.Text = "Marca al menos un sistema."; return; }
        var credentials = RaAccount.Ensure(this, trace);
        if (credentials is null) return;
        cts = new CancellationTokenSource();
        start.Enabled = false; systemList.Enabled = false; close.Text = "Cancelar";
        trace.Add("retroachievements_scan_started", $"systems={string.Join(",", chosen)}");
        try
        {
            var report = new Progress<string>(s => statusLabel.Text = s);
            var percent = new Progress<int>(p => bar.Value = Math.Clamp(p, 0, 100));
            var result = await RaScanner.RunAsync(rows, chosen, credentials, index, report, percent, cts.Token);
            Changed = true;
            statusLabel.Text = $"Hecho: {result.ByHash:N0} por hash, {result.ByName:N0} por nombre, {result.WithoutAchievements:N0} sin logros.";
            trace.Add("retroachievements_scan_finished", $"systems={result.Systems}; roms={result.Roms}; by_hash={result.ByHash}; by_name={result.ByName}; none={result.WithoutAchievements}; not_hashable={result.Skipped}");
            QuietMessage.Show(this, "Buscar logros",
                $"ROMs revisadas: {result.Roms:N0} en {result.Systems} sistema(s).\r\nCon logros, identificadas por hash: {result.ByHash:N0}\r\nCon logros, identificadas por nombre: {result.ByName:N0}\r\nSin logros: {result.WithoutAchievements:N0}" +
                (result.Skipped > 0 ? $"\r\n\r\n{result.Skipped:N0} ROMs no se pudieron cotejar por hash (disco, contenedor no admitido o consola sin hash propio) ni por nombre." : ""));
        }
        catch (OperationCanceledException) { Changed = true; statusLabel.Text = "Cancelado (lo ya cotejado se conserva)."; index.Save(); }
        catch (RaException ex) { statusLabel.Text = ex.Message; trace.Add("retroachievements_scan_error", ex.Message); QuietMessage.Show(this, "Buscar logros", ex.Message); }
        catch (Exception ex) { statusLabel.Text = "Error: " + ex.Message; trace.Add("retroachievements_scan_error", ex.ToString()); QuietMessage.Show(this, "Buscar logros", "No se pudo completar: " + ex.Message); }
        finally
        {
            cts?.Dispose(); cts = null;
            if (!IsDisposed) { start.Enabled = true; systemList.Enabled = true; close.Text = "Cerrar"; close.Enabled = true; bar.Value = 0; }
        }
    }
}

// Lista de logros de la ROM enfocada, con su insignia, puntos, rareza y, si hay usuario, cuáles has conseguido.
internal sealed class RaAchievementsDialog : LocalizedForm
{
    private readonly RaMatch match;
    private readonly RaIndex index;
    private readonly TraceLog trace;
    private readonly Label header = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), Tag = "noloc" };
    private readonly Label subtitle = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
    private readonly DataGridView grid = new();
    private readonly PictureBox avatar = new() { Location = new Point(14, 0), Size = new Size(52, 52), SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Control };
    private readonly Label avatarName = new() { Location = new Point(0, 54), Size = new Size(80, 18), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Tag = "noloc" };
    private readonly Label userStats = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Tag = "noloc" };
    private readonly Panel avatarBox = new() { Size = new Size(80, 74), Margin = new Padding(0, 0, 8, 0) };
    private readonly CancellationTokenSource cts = new();

    public RaAchievementsDialog(RomRow row, RaMatch match, RaIndex index, TraceLog trace)
    {
        this.match = match; this.index = index; this.trace = trace;
        Text = "Logros de RetroAchievements";
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(860, 600); MinimumSize = new Size(640, 400);
        header.Text = match.Title;
        subtitle.Text = "Cargando logros…";

        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
        // Sin foco visible: ni fila resaltada ni rectángulo de celda. Las filas se identifican con el ratón (doble clic y menú contextual).
        grid.DefaultCellStyle.SelectionBackColor = SystemColors.Window; grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.CellEnter += (_, _) => BeginInvoke(() => { if (!grid.IsDisposed) grid.CurrentCell = null; });
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowDetail(RowAchievement(e.RowIndex)); };
        grid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) menuRow = e.RowIndex; };
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var details = new ToolStripMenuItem("Ver detalles del logro…");
        var copy = new ToolStripMenuItem("Copiar la descripción");
        var openWeb = new ToolStripMenuItem("Abrir el logro en retroachievements.org");
        menu.Items.AddRange([details, copy, openWeb]);
        details.Click += (_, _) => ShowDetail(RowAchievement(menuRow));
        copy.Click += (_, _) => { if (RowAchievement(menuRow) is { } a) try { Clipboard.SetText(a.Description); } catch { } };
        openWeb.Click += (_, _) => { if (RowAchievement(menuRow) is { } a) OpenUrl($"https://retroachievements.org/achievement/{a.Id}"); };
        menu.Opening += (_, e) => { if (RowAchievement(menuRow) is null) e.Cancel = true; };
        grid.ContextMenuStrip = menu;
        grid.BackgroundColor = SystemColors.Window; grid.RowTemplate.Height = 56; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.Columns.Add(new DataGridViewImageColumn { Name = "badge", HeaderText = "", Width = 56, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Logro", Width = 220, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Descripción", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "points", HeaderText = "Puntos", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rare", HeaderText = "Jugadores", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mode", HeaderText = "Modo", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "earned", HeaderText = "Conseguido", Width = 130 });

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 86, ColumnCount = 2, RowCount = 3, Padding = new Padding(10, 4, 10, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); top.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); top.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        avatar.Location = new Point(14, 0);
        avatarBox.Controls.Add(avatar); avatarBox.Controls.Add(avatarName);
        top.Controls.Add(avatarBox, 0, 0); top.SetRowSpan(avatarBox, 3);
        top.Controls.Add(header, 1, 0); top.Controls.Add(subtitle, 1, 1); top.Controls.Add(userStats, 1, 2);
        var link = new LinkLabel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(10, 4, 0, 0), Text = "Abrir el juego en retroachievements.org" };
        link.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo($"https://retroachievements.org/game/{match.GameId}") { UseShellExecute = true }); } catch { } };
        var filterPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 2, 0, 0), WrapContents = false };
        foreach (var radio in new[] { filterAll, filterEarned, filterPending }) { radio.AutoSize = true; radio.Margin = new Padding(0, 3, 18, 0); radio.CheckedChanged += (_, _) => { if (((RadioButton)radio).Checked) ApplyFilter(); }; filterPanel.Controls.Add(radio); }
        Controls.Add(grid); Controls.Add(filterPanel); Controls.Add(top); Controls.Add(link);
        Shown += async (_, _) => await LoadAsync();
        FormClosed += (_, _) => cts.Cancel();
    }

    private readonly RadioButton filterAll = new() { Text = "Todos", Checked = true, Tag = "noloc" };
    private readonly RadioButton filterEarned = new() { Text = "Conseguidos", Tag = "noloc" };
    private readonly RadioButton filterPending = new() { Text = "Pendientes", Tag = "noloc" };
    private RaGameDetail? lastDetail;

    // Muestra todos los logros, solo los conseguidos o solo los pendientes. Los textos llevan el recuento, asÃ­ que se traducen a mano.
    private void ApplyFilter()
    {
        grid.CurrentCell = null;
        foreach (DataGridViewRow row in grid.Rows)
        {
            bool earned = row.Tag is RaAchievement { Earned: true };
            row.Visible = filterEarned.Checked ? earned : !filterPending.Checked || !earned;
        }
    }

    private void UpdateFilterCounts(int total, int earned)
    {
        filterAll.Text = $"{Loc.T("Todos")} ({total})";
        filterEarned.Text = $"{Loc.T("Conseguidos")} ({earned})";
        filterPending.Text = $"{Loc.T("Pendientes")} ({total - earned})";
    }
    private int menuRow = -1;

    private RaAchievement? RowAchievement(int row) => row >= 0 && row < grid.Rows.Count ? grid.Rows[row].Tag as RaAchievement : null;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void ShowDetail(RaAchievement? achievement)
    {
        if (achievement is null || lastDetail is null) return;
        using var dialog = new RaAchievementDetailDialog(achievement, lastDetail, cts.Token);
        dialog.ShowDialog(this);
    }

    private async Task LoadUserStatsAsync(RaClient client, string userName)
    {
        try
        {
            if (await client.GetUserInfoAsync(cts.Token) is not { } info || IsDisposed) return;
            string rank = info.Rank <= 0 ? Loc.T("Sin puesto")
                : info.TotalRanked > 0 ? Loc.T("Puesto {0} de {1}", info.Rank.ToString("N0"), info.TotalRanked.ToString("N0")) : Loc.T("Puesto {0}", info.Rank.ToString("N0"));
            userStats.Text = string.Join(" · ", rank, Loc.T("{0} puntos hardcore", info.HardcorePoints.ToString("N0")), Loc.T("{0} puntos softcore", info.SoftcorePoints.ToString("N0")));
        }
        catch { }
    }

    private async Task LoadAvatarAsync(RaClient client)
    {
        try
        {
            string? file = await client.GetAvatarAsync(cts.Token);
            if (file is null || IsDisposed) return;
            using var image = Image.FromFile(file);
            avatar.Image = new Bitmap(image);
        }
        catch { }
    }

    private async Task LoadAsync()
    {
        try
        {
            var credentials = RaAccount.Ensure(this, trace);
            if (credentials is null) { subtitle.Text = "Faltan las credenciales de RetroAchievements."; return; }
            var detail = await new RaClient(credentials).GetGameAsync(match.GameId, true, cts.Token);
            if (IsDisposed) return;
            var client = new RaClient(credentials);
            avatarName.Text = credentials.UserName;
            int won = detail.Achievements.Count(a => a.Earned), hard = detail.Achievements.Count(a => a.Hardcore);
            int pointsWon = detail.Achievements.Where(a => a.Earned).Sum(a => a.Points);
            index.SetAwarded(match.GameId, won); index.Save();
            subtitle.Text = $"{detail.ConsoleName} · {detail.Achievements.Count} logros · {detail.Achievements.Sum(a => a.Points)} puntos · tú: {won} logros ({pointsWon} puntos), {hard} en hardcore" +
                (match.By == "nombre" ? " · identificada por nombre, puede ser otra versión" : "");
            foreach (var a in detail.Achievements)
            {
                int i = grid.Rows.Add(null, a.Title, a.Description, a.Points, a.NumAwarded.ToString("N0"), a.Hardcore ? "Hardcore" : a.Earned ? "Softcore" : "—", a.Earned ? a.When : "—");
                grid.Rows[i].Tag = a;
                var style = grid.Rows[i].DefaultCellStyle;
                if (a.Hardcore) { style.BackColor = Color.FromArgb(255, 241, 196); grid.Rows[i].Cells["mode"].Style.Font = new Font(Font, FontStyle.Bold); }
                else if (a.Earned) style.BackColor = Color.FromArgb(232, 245, 233);
                // La fila enfocada no se resalta: la selección conserva los colores de la propia fila.
                style.SelectionBackColor = style.BackColor.IsEmpty ? SystemColors.Window : style.BackColor;
                style.SelectionForeColor = SystemColors.ControlText;
            }
            lastDetail = detail;
            UpdateFilterCounts(detail.Achievements.Count, won);
            grid.CurrentCell = null;
            _ = LoadAvatarAsync(client);
            _ = LoadUserStatsAsync(client, credentials.UserName);
            grid.Columns["title"]!.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            // Las insignias se bajan después, de una en una, para que la lista aparezca ya.
            foreach (DataGridViewRow gridRow in grid.Rows.Cast<DataGridViewRow>().ToList())
            {
                if (gridRow.Tag is not RaAchievement a) continue;
                string? file = await RaClient.GetBadgeAsync(a.BadgeName, !a.Earned, cts.Token);
                if (file is null || IsDisposed) continue;
                try { using var image = Image.FromFile(file); gridRow.Cells["badge"].Value = new Bitmap(image); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (RaException ex) { if (!IsDisposed) subtitle.Text = ex.Message; }
        catch (Exception ex) { if (!IsDisposed) subtitle.Text = "No se pudieron leer los logros: " + ex.Message; }
    }
}

// Información de un logro: insignia, descripción, puntos, rareza, tipo, autoría y cuándo lo conseguiste (y en qué modo).
internal sealed class RaAchievementDetailDialog : LocalizedForm
{
    public RaAchievementDetailDialog(RaAchievement a, RaGameDetail game, CancellationToken token)
    {
        Text = "Detalle del logro";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(560, 380);

        var badge = new PictureBox { Location = new Point(14, 14), Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Control };
        var title = new Label { Location = new Point(98, 14), Size = new Size(448, 26), AutoEllipsis = true, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), Text = a.Title, Tag = "noloc" };
        var description = new Label { Location = new Point(98, 44), Size = new Size(448, 44), Text = a.Description, Tag = "noloc" };

        string players = game.Players > 0 ? $"{a.NumAwarded:N0} de {game.Players:N0} jugadores ({a.NumAwarded * 100.0 / game.Players:0.0} %); {a.NumAwardedHardcore:N0} en hardcore" : $"{a.NumAwarded:N0} jugadores";
        string mine = a.Hardcore ? $"Sí, en hardcore · {a.DateHardcore}" : a.Earned ? $"Sí, en softcore · {a.DateEarned}" : "Todavía no";
        var rows = new List<(string Label, string Value)>
        {
            ("Juego", game.Title),
            ("Puntos", $"{a.Points}" + (a.TrueRatio > 0 ? $" (valor de rareza: {a.TrueRatio})" : "")),
            ("Conseguido por", players),
            ("Lo tienes", mine),
            ("Tipo", TypeName(a.Type)),
            ("Autor", a.Author.Length > 0 ? a.Author : "—"),
            ("Creado", a.Created.Length > 0 ? a.Created : "—"),
            ("Modificado", a.Modified.Length > 0 ? a.Modified : "—")
        };
        var grid = new TableLayoutPanel { Location = new Point(14, 100), Size = new Size(532, 230), ColumnCount = 2, RowCount = rows.Count };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            grid.Controls.Add(new Label { Text = rows[i].Label, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left }, 0, i);
            grid.Controls.Add(new Label { Text = rows[i].Value, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Tag = i == 0 ? "noloc" : null }, 1, i);
        }
        var link = new LinkLabel { Location = new Point(14, 346), AutoSize = true, Text = "Abrir el logro en retroachievements.org" };
        link.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo($"https://retroachievements.org/achievement/{a.Id}") { UseShellExecute = true }); } catch { } };
        var close = new Button { Text = "Cerrar", DialogResult = DialogResult.Cancel, Location = new Point(456, 342), Size = new Size(90, 28) };
        CancelButton = close; AcceptButton = close;
        Controls.AddRange([badge, title, description, grid, link, close]);
        Shown += async (_, _) =>
        {
            string? file = await RaClient.GetBadgeAsync(a.BadgeName, !a.Earned, token);
            if (file is null || IsDisposed) return;
            try { using var image = Image.FromFile(file); badge.Image = new Bitmap(image); } catch { }
        };
    }

    private static string TypeName(string type) => type.ToLowerInvariant() switch
    {
        "progression" => "Progresión (avanza la historia)",
        "win_condition" => "Condición de victoria (final del juego)",
        "missable" => "Se puede perder (hay que conseguirlo a tiempo)",
        "" => "Sin tipo",
        _ => type
    };
}
