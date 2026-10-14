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
    private readonly TextBox user = new() { Location = new Point(24, 110), Size = new Size(324, 27) };
    private readonly TextBox key = new() { Location = new Point(24, 178), Size = new Size(324, 27), UseSystemPasswordChar = true };
    public RaCredentials? Result { get; private set; }

    public RaCredentialsDialog(RaCredentials current)
    {
        Text = "Credenciales de RetroAchievements";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(560, 372);
        user.Text = current.UserName; key.Text = current.ApiKey;

        var banner = new RaHeader("Credenciales de RetroAchievements", "Para consultar tus logros y tu progreso") { RightReserve = 16 };
        Label Caption(string text, int y) => new() { Text = text, Location = new Point(24, y), AutoSize = true, ForeColor = RaUi.Muted };
        var note = new Label
        {
            Location = new Point(24, 220), Size = new Size(324, 92), ForeColor = RaUi.Muted,
            Text = "La clave se copia de tu perfil en retroachievements.org › Ajustes › Claves (Web API Key). Se guarda cifrada con tu usuario de Windows (DPAPI), solo en este equipo."
        };

        // Tarjeta del usuario guardado: avatar, nombre y puntos.
        var card = RaUi.Card(new Rectangle(372, 90, 164, 214));
        var avatar = new PictureBox { Location = new Point(46, 16), Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom, BackColor = RaUi.Body };
        var avatarName = new Label { Location = new Point(6, 96), Size = new Size(152, 22), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Text = current.UserName, Tag = "noloc", Font = new Font(Font, FontStyle.Bold) };
        var avatarStats = new Label { Location = new Point(6, 122), Size = new Size(152, 80), TextAlign = ContentAlignment.TopCenter, ForeColor = RaUi.Muted, Tag = "noloc" };
        user.TextChanged += (_, _) => avatarName.Text = user.Text.Trim();
        card.Controls.AddRange([avatar, avatarName, avatarStats]);

        var footer = RaUi.Footer();
        var open = RaUi.MakeButton("Abrir mis claves", false, 150); open.Location = new Point(24, 10);
        var ok = RaUi.MakeButton("Guardar", true); ok.DialogResult = DialogResult.OK; ok.Location = new Point(330, 10);
        var cancel = RaUi.MakeButton("Cancelar", false); cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(436, 10);
        ok.Click += (_, _) => Result = new RaCredentials(user.Text.Trim(), key.Text.Trim());
        open.Click += (_, _) => RaAccount.OpenKeysPage(this);
        footer.Controls.AddRange([open, ok, cancel]);

        Controls.AddRange([note, card, user, key, Caption("Usuario de RetroAchievements:", 86), Caption("Clave de la Web API:", 154), footer, banner]);
        if (current.IsComplete)
            Shown += async (_, _) =>
            {
                var client = new RaClient(current);
                try { if (await client.GetAvatarAsync(CancellationToken.None) is { } file) { using var image = Image.FromFile(file); avatar.Image = new Bitmap(image); } } catch { }
                try
                {
                    if (await client.GetUserInfoAsync(CancellationToken.None) is { } info && !IsDisposed)
                        avatarStats.Text = (info.Rank > 0 ? "#" + info.Rank.ToString("N0") : "—") + "\r\nHC " + info.HardcorePoints.ToString("N0") + "\r\nSC " + info.SoftcorePoints.ToString("N0");
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
    private readonly CheckableList systemList = new() { IntegralHeight = false, ItemHeight = 34, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly Label statusLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = RaUi.Muted };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100 };
    private readonly Button start = RaUi.MakeButton("Analizar", true);
    private readonly Button close = RaUi.MakeButton("Cerrar", false);
    private readonly Label[] tileValue = new Label[3];
    private readonly Font rowBold;
    private CancellationTokenSource? cts;
    public bool Changed { get; private set; }

    public RaScanDialog(IReadOnlyList<RomRow> rows, RaIndex index, TraceLog trace, string? preselectSystem)
    {
        this.rows = rows; this.index = index; this.trace = trace;
        Text = "Buscar logros — RetroAchievements";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(600, 630);
        rowBold = new Font(Font, FontStyle.Bold);

        var banner = new RaHeader("Buscar logros", "Coteja las ROMs de tu colección con RetroAchievements");
        var intro = new Label
        {
            Location = new Point(24, 80), Size = new Size(552, 52), AutoSize = false, ForeColor = RaUi.Muted,
            Text = "Marca los sistemas que quieres cotejar con RetroAchievements. Las ROMs de cartucho y los discos de PlayStation, PS2, Sega CD y Saturn (CHD, ISO, BIN/CUE) se identifican por hash; el resto, por el nombre del juego (menos fiable). Lo que no tiene logros queda anotado para el curado."
        };
        var listCard = RaUi.Card(new Rectangle(24, 136, 552, 250));
        systemList.Location = new Point(1, 1); systemList.Size = new Size(550, 248);
        systemList.DrawItem += DrawSystem;
        listCard.Controls.Add(systemList);
        foreach (var group in rows.Where(r => r.Status == RomStatus.Ok && RaConsoles.Of(r.System) is not null).GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            int i = systemList.Items.Add(new SystemItem(group.Key, group.Count(), index.SystemChecked(group.Key)));
            if (preselectSystem is null || group.Key.Equals(preselectSystem, StringComparison.OrdinalIgnoreCase)) systemList.SetItemChecked(i, true);
        }
        var all = RaUi.MakeButton("Todos", false, 84); all.Location = new Point(24, 394); all.Height = 28;
        var none = RaUi.MakeButton("Ninguno", false, 84); none.Location = new Point(114, 394); none.Height = 28;
        all.Click += (_, _) => { for (int i = 0; i < systemList.Items.Count; i++) systemList.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < systemList.Items.Count; i++) systemList.SetItemChecked(i, false); };

        // Resultado del último análisis, en tres tarjetas.
        (string Caption, Color Accent)[] tiles = [("Por hash", RaUi.Green), ("Por nombre", Color.FromArgb(190, 130, 10)), ("Sin logros", RaUi.Muted)];
        for (int t = 0; t < tiles.Length; t++)
        {
            var tile = RaUi.Card(new Rectangle(24 + t * 188, 436, 176, 62));
            tileValue[t] = new Label { Location = new Point(10, 6), Size = new Size(156, 30), Text = "—", Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), ForeColor = tiles[t].Accent, Tag = "noloc" };
            tile.Controls.Add(tileValue[t]);
            tile.Controls.Add(new Label { Location = new Point(10, 38), Size = new Size(156, 20), Text = tiles[t].Caption, ForeColor = RaUi.Muted });
            Controls.Add(tile);
        }
        statusLabel.Location = new Point(24, 506); statusLabel.Size = new Size(552, 22);
        bar.Location = new Point(24, 532); bar.Size = new Size(552, 10);

        var footer = RaUi.Footer();
        start.Location = new Point(366, 10); close.Location = new Point(476, 10);
        footer.Controls.AddRange([start, close]);
        Controls.AddRange([banner, intro, listCard, all, none, statusLabel, bar, footer, new CpuUsageStrip()]); // la barra de CPU va debajo del todo, como en la descarga masiva
        CancelButton = close;
        start.Click += async (_, _) => await RunAsync();
        close.Click += (_, _) => { if (cts is not null) { cts.Cancel(); close.Enabled = false; statusLabel.Text = "Cancelando…"; } else Close(); };
        if (systemList.Items.Count == 0) { statusLabel.Text = "Ningún sistema de la colección tiene consola en RetroAchievements."; start.Enabled = false; }
    }

    // Una fila por sistema: casilla, icono, nombre, número de ROMs y, si ya se analizó, una marca verde.
    private void DrawSystem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var item = (SystemItem)systemList.Items[e.Index];
        var g = e.Graphics; var r = e.Bounds;
        using (var back = new SolidBrush(e.Index % 2 == 0 ? Color.White : Color.FromArgb(247, 248, 250))) g.FillRectangle(back, r);
        bool on = systemList.GetItemChecked(e.Index);
        CheckBoxRenderer.DrawCheckBox(g, new Point(r.X + 12, r.Y + (r.Height - 13) / 2), on ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
        g.DrawImage(PixelSystemIcons.Get(item.System, 24), new Rectangle(r.X + 38, r.Y + 5, 24, 24));
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, item.System, rowBold, new Rectangle(r.X + 72, r.Y, 230, r.Height), RaUi.Dark, flags);
        TextRenderer.DrawText(g, Loc.T("{0} ROMs", item.Roms.ToString("N0")), Font, new Rectangle(r.X + 300, r.Y, 110, r.Height), RaUi.Muted, flags | TextFormatFlags.Right);
        if (item.Done) TextRenderer.DrawText(g, "✓ " + Loc.T("analizado"), Font, new Rectangle(r.X + 420, r.Y, 120, r.Height), RaUi.Green, flags);
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
        foreach (var value in tileValue) value.Text = "…";
        trace.Add("retroachievements_scan_started", $"systems={string.Join(",", chosen)}");
        try
        {
            var report = new Progress<string>(s => statusLabel.Text = s);
            var percent = new Progress<int>(p => bar.Value = Math.Clamp(p, 0, 100));
            // Las tarjetas se van actualizando con lo que se encuentra.
            var live = new Progress<(int ByHash, int ByName, int None)>(c => { tileValue[0].Text = c.ByHash.ToString("N0"); tileValue[1].Text = c.ByName.ToString("N0"); tileValue[2].Text = c.None.ToString("N0"); });
            RaScanner.Log = s => trace.Add("retroachievements_debug", s);
            var result = await RaScanner.RunAsync(rows, chosen, credentials, index, report, percent, cts.Token, live);
            Changed = true;
            tileValue[0].Text = result.ByHash.ToString("N0"); tileValue[1].Text = result.ByName.ToString("N0"); tileValue[2].Text = result.WithoutAchievements.ToString("N0");
            statusLabel.Text = $"Hecho: {result.Roms:N0} ROMs revisadas en {result.Systems} sistema(s)." +
                (result.Skipped > 0 ? $" {result.Skipped:N0} no se pudieron cotejar por hash ni por nombre." : "");
            trace.Add("retroachievements_scan_finished", $"systems={result.Systems}; roms={result.Roms}; by_hash={result.ByHash}; by_name={result.ByName}; none={result.WithoutAchievements}; not_hashable={result.Skipped}");
        }
        catch (OperationCanceledException) { Changed = true; statusLabel.Text = "Cancelado (lo ya cotejado se conserva)."; index.Save(); foreach (var value in tileValue) value.Text = "—"; }
        catch (RaException ex) { statusLabel.Text = ex.Message; trace.Add("retroachievements_scan_error", ex.Message); QuietMessage.Show(this, "Buscar logros", ex.Message); }
        catch (Exception ex) { statusLabel.Text = "Error: " + ex.Message; trace.Add("retroachievements_scan_error", ex.ToString()); QuietMessage.Show(this, "Buscar logros", "No se pudo completar: " + ex.Message); }
        finally
        {
            cts?.Dispose(); cts = null;
            if (!IsDisposed) { start.Enabled = true; systemList.Enabled = true; close.Text = "Cerrar"; close.Enabled = true; bar.Value = 0; systemList.Invalidate(); }
        }
    }
}

// Lista de logros de la ROM enfocada, con su insignia, puntos, rareza y, si hay usuario, cuáles has conseguido.
internal sealed class RaAchievementsDialog : LocalizedForm
{
    private readonly RaMatch match;
    private readonly RaIndex index;
    private readonly TraceLog trace;
    private readonly RaHeader banner;
    private readonly DataGridView grid = new();
    private readonly PictureBox avatar = new() { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(48, 52, 64), Anchor = AnchorStyles.Top | AnchorStyles.Right };
    private readonly Label avatarName = new() { Size = new Size(170, 48), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, ForeColor = Color.White, BackColor = Color.Transparent, Anchor = AnchorStyles.Top | AnchorStyles.Right, Tag = "noloc" };
    private readonly Label summary = new() { Location = new Point(20, 8), Size = new Size(380, 22), AutoEllipsis = true, Tag = "noloc" };
    private readonly Label userStats = new() { Size = new Size(440, 22), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = RaUi.Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right, Tag = "noloc" };
    private readonly Panel strip = new() { Dock = DockStyle.Top, Height = 62, BackColor = Color.White };
    private int wonCount, totalCount;
    private readonly CancellationTokenSource cts = new();

    private Label header => banner.Title;
    private Label subtitle => banner.Subtitle;

    public RaAchievementsDialog(RomRow row, RaMatch match, RaIndex index, TraceLog trace)
    {
        this.match = match; this.index = index; this.trace = trace;
        Text = "Logros de RetroAchievements";
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(900, 640); MinimumSize = new Size(700, 420);
        banner = new RaHeader(match.Title, "Cargando logros…", 80) { RightReserve = 260 };
        header.Tag = "noloc";
        avatar.Location = new Point(ClientSize.Width - 16 - 48, 16);
        avatarName.Location = new Point(ClientSize.Width - 16 - 48 - 8 - 170, 16);
        banner.Controls.AddRange([avatar, avatarName]);

        // Franja con el progreso: logros y puntos conseguidos, y la clasificación del usuario.
        userStats.Location = new Point(ClientSize.Width - 20 - 440, 8);
        strip.Controls.AddRange([summary, userStats]);
        strip.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using var line = new Pen(RaUi.Border); g.DrawLine(line, 0, strip.Height - 1, strip.Width, strip.Height - 1);
            var track = new Rectangle(20, 38, strip.Width - 40, 10);
            using var trackBrush = new SolidBrush(Color.FromArgb(228, 231, 237)); g.FillRectangle(trackBrush, track);
            if (totalCount > 0 && wonCount > 0)
            {
                using var fill = new SolidBrush(RaUi.Gold);
                g.FillRectangle(fill, new Rectangle(track.X, track.Y, Math.Max(4, (int)((long)track.Width * wonCount / totalCount)), track.Height));
            }
        };
        strip.Resize += (_, _) => strip.Invalidate();

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
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(232, 234, 239);
        grid.RowTemplate.Height = 60; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 34;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(52, 56, 68); grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor; grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        var center = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Padding = new Padding(0) };
        grid.Columns.Add(new DataGridViewImageColumn { Name = "badge", HeaderText = "", Width = 64, ImageLayout = DataGridViewImageCellLayout.Zoom, DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(6) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Logro", Width = 230, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Padding = new Padding(6, 0, 6, 0) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Descripción", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, ForeColor = Color.FromArgb(70, 76, 90), Padding = new Padding(6, 0, 6, 0) } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "points", HeaderText = "Puntos", Width = 70, DefaultCellStyle = center });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rare", HeaderText = "Jugadores", Width = 90, DefaultCellStyle = center });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mode", HeaderText = "Modo", Width = 90, DefaultCellStyle = center });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "earned", HeaderText = "Conseguido", Width = 140, DefaultCellStyle = center });

        // Filtro como tres botones planos (el activo en dorado).
        var filterPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(20, 8, 0, 0), WrapContents = false, BackColor = RaUi.Body };
        foreach (var radio in new[] { filterAll, filterEarned, filterPending })
        {
            radio.Appearance = Appearance.Button; radio.FlatStyle = FlatStyle.Flat; radio.TextAlign = ContentAlignment.MiddleCenter; radio.Size = new Size(140, 30); radio.Margin = new Padding(0, 0, 8, 0);
            radio.BackColor = Color.White; radio.ForeColor = RaUi.Dark; radio.Cursor = Cursors.Hand; radio.UseVisualStyleBackColor = false;
            radio.FlatAppearance.BorderColor = RaUi.Border; radio.FlatAppearance.CheckedBackColor = RaUi.Gold; radio.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 240, 245);
            radio.CheckedChanged += (_, _) => { if (radio.Checked) ApplyFilter(); };
            filterPanel.Controls.Add(radio);
        }
        var footer = RaUi.Footer(44);
        var link = new LinkLabel { Location = new Point(20, 13), AutoSize = true, Text = "Abrir el juego en retroachievements.org", LinkColor = Color.FromArgb(150, 105, 0) };
        link.LinkClicked += (_, _) => OpenUrl($"https://retroachievements.org/game/{match.GameId}");
        footer.Controls.Add(link);
        Controls.Add(grid); Controls.Add(filterPanel); Controls.Add(strip); Controls.Add(banner); Controls.Add(footer);
        Shown += async (_, _) => await LoadAsync();
        FormClosed += (_, _) => cts.Cancel();
    }

    private readonly RadioButton filterAll = new() { Text = "Todos", Checked = true, Tag = "noloc" };
    private readonly RadioButton filterEarned = new() { Text = "Conseguidos", Tag = "noloc" };
    private readonly RadioButton filterPending = new() { Text = "Pendientes", Tag = "noloc" };
    private RaGameDetail? lastDetail;

    // Muestra todos los logros, solo los conseguidos o solo los pendientes. Los textos llevan el recuento, así que se traducen a mano.
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
            int pointsWon = detail.Achievements.Where(a => a.Earned).Sum(a => a.Points), pointsTotal = detail.Achievements.Sum(a => a.Points);
            var progress = RaGameProgress.FromAchievements(detail.Achievements, detail.HighestAward);
            progress.Kind = RaGameProgress.Best(progress.Kind, index.Progress(match.GameId)?.Kind ?? "");
            index.SetGameProgress(match.GameId, progress); index.Save();
            subtitle.Text = $"{detail.ConsoleName} · {detail.Achievements.Count} logros · {pointsTotal} puntos · {hard} en hardcore" +
                (match.By == "nombre" ? " · identificada por nombre, puede ser otra versión" : "");
            wonCount = won; totalCount = detail.Achievements.Count;
            summary.Text = Loc.T("Has conseguido {0} de {1} logros · {2} de {3} puntos", won, detail.Achievements.Count, pointsWon, pointsTotal);
            strip.Invalidate();
            foreach (var a in detail.Achievements)
            {
                int i = grid.Rows.Add((object)null!, a.Title, a.Description, a.Points, a.NumAwarded.ToString("N0"), a.Hardcore ? "Hardcore" : a.Earned ? "Softcore" : "—", a.Earned ? a.When : "—");
                grid.Rows[i].Tag = a;
                var style = grid.Rows[i].DefaultCellStyle;
                if (a.Hardcore) { style.BackColor = Color.FromArgb(255, 245, 210); grid.Rows[i].Cells["mode"].Style.Font = new Font(Font, FontStyle.Bold); }
                else if (a.Earned) style.BackColor = Color.FromArgb(234, 246, 237);
                // La fila enfocada no se resalta: la selección conserva los colores de la propia fila.
                style.SelectionBackColor = style.BackColor.IsEmpty ? SystemColors.Window : style.BackColor;
                style.SelectionForeColor = SystemColors.ControlText;
                grid.Rows[i].Cells["points"].Style.Font = new Font(Font, FontStyle.Bold);
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
        BackColor = RaUi.Body;

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
        const int rowHeight = 30;
        ClientSize = new Size(620, 100 + 16 + rows.Count * rowHeight + 16 + 52);

        // El texto del título y de la descripción es del logro (no se traduce).
        var banner = new RaHeader(a.Title, a.Description, 100, 72, logo: false);
        banner.Title.Tag = "noloc"; banner.Subtitle.Tag = "noloc";
        var badge = banner.Icon;

        var card = RaUi.Card(new Rectangle(16, 116, 588, rows.Count * rowHeight + 8));
        for (int i = 0; i < rows.Count; i++)
        {
            int y = 4 + i * rowHeight;
            if (i % 2 == 1) card.Controls.Add(new Panel { Location = new Point(1, y), Size = new Size(586, rowHeight), BackColor = Color.FromArgb(248, 249, 251) });
            var label = new Label { Text = rows[i].Label, Location = new Point(12, y), Size = new Size(130, rowHeight), TextAlign = ContentAlignment.MiddleLeft, ForeColor = RaUi.Muted, BackColor = Color.Transparent };
            var value = new Label { Text = rows[i].Value, Location = new Point(146, y), Size = new Size(430, rowHeight), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Color.Transparent, Tag = i == 0 ? "noloc" : null };
            if (i == 3) { value.ForeColor = a.Hardcore ? Color.FromArgb(160, 110, 0) : a.Earned ? RaUi.Green : RaUi.Muted; value.Font = new Font(Font, FontStyle.Bold); }
            card.Controls.Add(label); card.Controls.Add(value);
            label.BringToFront(); value.BringToFront();
        }

        var footer = RaUi.Footer();
        var link = new LinkLabel { Location = new Point(16, 17), AutoSize = true, Text = "Abrir el logro en retroachievements.org", LinkColor = Color.FromArgb(150, 105, 0) };
        link.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo($"https://retroachievements.org/achievement/{a.Id}") { UseShellExecute = true }); } catch { } };
        var close = RaUi.MakeButton("Cerrar", true); close.DialogResult = DialogResult.Cancel; close.Location = new Point(504, 10);
        footer.Controls.AddRange([link, close]);
        CancelButton = close; AcceptButton = close;
        Controls.AddRange([card, footer, banner]);
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

// Lista con casillas dibujada a mano. El CheckedListBox de WinForms pinta él mismo sus filas y no deja añadir iconos ni columnas.
internal sealed class CheckableList : ListBox
{
    private readonly HashSet<int> marked = [];

    public CheckableList() { DrawMode = DrawMode.OwnerDrawFixed; DoubleBuffered = true; }

    public IEnumerable<object> CheckedItems => marked.OrderBy(i => i).Where(i => i < Items.Count).Select(i => Items[i]);
    public bool GetItemChecked(int index) => marked.Contains(index);

    public void SetItemChecked(int index, bool value)
    {
        if (index < 0 || index >= Items.Count) return;
        if (value) marked.Add(index); else marked.Remove(index);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0) SetItemChecked(index, !GetItemChecked(index));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && SelectedIndex >= 0) { SetItemChecked(SelectedIndex, !GetItemChecked(SelectedIndex)); e.Handled = true; }
    }
}
