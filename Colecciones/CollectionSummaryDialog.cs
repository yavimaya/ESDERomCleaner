using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace ESDERomCleaner;

internal sealed record SummarySystem(string System, int Roms, long Bytes, GapCount Gaps);

internal sealed record CollectionSummary(string Root, int Systems, int Roms, int Ambiguous, int Files, int Gamelists, long RomBytes, long MediaBytes, GapCount Gaps,
    IReadOnlyList<SummarySystem> PerSystem, IReadOnlyList<(string Category, int Roms)> Categories);

// Resumen de la colección en tres pestañas (General, Sistemas, Categorías), con el texto listo para compartir y una tarjeta en imagen.
internal sealed class CollectionSummaryDialog : LocalizedForm
{
    private static readonly Color Alert = Color.Firebrick;
    private readonly CollectionSummary s;
    private Bitmap? card;

    // Con removeSystem, clic derecho en un sistema de la pestaña «Sistemas» ofrece eliminarlo (devuelve true si se borró).
    private readonly Func<string, Task<bool>>? removeSystem;

    public CollectionSummaryDialog(CollectionSummary summary, Func<string, Task<bool>>? removeSystem = null)
    {
        s = summary;
        this.removeSystem = removeSystem;
        Text = "Resumen de la colección";
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(680, 760 + 68);
        MinimumSize = new Size(420, 520 + 68);
        BackColor = RaUi.Body;

        // La propia tarjeta (la que se guarda como imagen) es lo que se ve en la ventana, ajustada a su tamaño.
        card = DrawCard();
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = card, BackColor = Color.FromArgb(18, 20, 44), Margin = Padding.Empty };
        FormClosed += (_, _) => { picture.Image = null; card?.Dispose(); card = null; };
        var close = new Button { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(90, 28), DialogResult = DialogResult.Cancel };
        var image = new Button { Text = "Copiar imagen", AutoSize = true, MinimumSize = new Size(120, 28) };
        var copy = new Button { Text = "Copiar texto", AutoSize = true, MinimumSize = new Size(120, 28) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 8, 8, 4), BackColor = RaUi.FooterBack };
        buttons.Paint += (_, e) => { using var pen = new Pen(RaUi.Border); e.Graphics.DrawLine(pen, 0, 0, buttons.Width, 0); };
        RaUi.StyleButton(close, true); RaUi.StyleButton(image, false); RaUi.StyleButton(copy, false);
        buttons.Controls.AddRange([close, image, copy]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        // Pestañas: la tarjeta (lo que se guarda como imagen) y el detalle en tablas.
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var cardPage = new TabPage("Tarjeta") { Padding = Padding.Empty };
        cardPage.Controls.Add(picture);
        tabs.TabPages.Add(cardPage);
        tabs.TabPages.Add(BuildGeneral());
        tabs.TabPages.Add(BuildSystems());
        tabs.TabPages.Add(BuildCategories());
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        // Cabecera oscura con un gráfico de barras, como en los demás formularios.
        var banner = new RaHeader("Resumen de la colección", $"{s.Roms:N0} ROMs · {s.Systems:N0} {Loc.T("Sistemas").ToLowerInvariant()} · {SizeText(s.RomBytes)}", 68, 48, logo: false);
        banner.Subtitle.Tag = "noloc";
        banner.Icon.Image = RaUi.ChartIcon();
        Controls.Add(banner);
        CancelButton = close;

        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(ShareText()); copy.Text = Loc.T("Copiado"); }
            catch (Exception ex) { QuietMessage.Show(this, "Copiar resumen", ex.Message); }
        };
        image.Click += (_, _) =>
        {
            try { using var copyOfCard = new Bitmap(card ?? throw new InvalidOperationException()); Clipboard.SetImage(copyOfCard); image.Text = Loc.T("Copiado"); }
            catch (Exception ex) { QuietMessage.Show(this, "Copiar imagen", ex.Message); }
        };
    }

    // ---- Pestañas

    private static string Pct(int n, int total) => total > 0 ? $"{n:N0} ({n * 100.0 / total:0.#} %)" : $"{n:N0}";

    private TabPage BuildGeneral()
    {
        var page = new TabPage("General") { Padding = new Padding(12), AutoScroll = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Heading(string text)
        {
            var label = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, table.RowCount > 0 ? 14 : 0, 0, 4) };
            table.Controls.Add(label); table.SetColumnSpan(label, 2);
        }
        void Row(string key, string value, bool alert = false)
        {
            table.Controls.Add(new Label { Text = key, AutoSize = true, Margin = new Padding(8, 2, 3, 2) });
            table.Controls.Add(new Label
            {
                Text = value, AutoSize = true, Tag = "noloc", Margin = new Padding(3, 2, 3, 2),
                ForeColor = alert ? Alert : SystemColors.ControlText, Font = alert ? new Font(Font, FontStyle.Bold) : Font
            });
        }
        Heading("Colección");
        Row("Carpeta raíz", s.Root);
        Row("Sistemas", s.Systems.ToString("N0"));
        Row("ROMs identificadas", s.Roms.ToString("N0"));
        if (s.Ambiguous > 0) Row("ROMs ambiguas", s.Ambiguous.ToString("N0"), alert: true);
        Row("Ficheros en Roms", s.Files.ToString("N0"));
        Row("Gamelists", s.Gamelists.ToString("N0"));
        Row("Tamaño de las ROMs", SizeText(s.RomBytes));
        Row("Tamaño de los medios", SizeText(s.MediaBytes));
        Heading("Pendiente de completar");
        Row("Sin categoría", Pct(s.Gaps.NoMetadata, s.Roms), s.Gaps.NoMetadata > 0);
        Row("Sin imagen", Pct(s.Gaps.NoImage, s.Roms), s.Gaps.NoImage > 0);
        Row("Sin vídeo", Pct(s.Gaps.NoVideo, s.Roms), s.Gaps.NoVideo > 0);
        var topSystem = s.PerSystem.OrderByDescending(p => p.Roms).FirstOrDefault();
        var topCategory = s.Categories.FirstOrDefault();
        if (topSystem is not null || topCategory.Category is not null)
        {
            Heading("Destacados");
            if (topSystem is not null) Row("Sistema con más ROMs", $"{topSystem.System} ({topSystem.Roms:N0})");
            if (topCategory.Category is not null) Row("Categoría más habitual", $"{topCategory.Category} ({topCategory.Roms:N0})");
        }
        page.Controls.Add(table);
        return page;
    }

    private static StaticGrid NewGrid()
    {
        var grid = new StaticGrid { Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        grid.ApplyInfoStyle(22, 24);
        RaUi.StyleGrid(grid, 22, 24, selectionVisible: false);
        return grid;
    }

    private TabPage BuildSystems()
    {
        var page = new TabPage("Sistemas") { Padding = new Padding(8) };
        var grid = NewGrid();
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        var right = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        grid.Columns.Add(new DataGridViewImageColumn { HeaderText = "", Width = 30, SortMode = DataGridViewColumnSortMode.NotSortable, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", AutoSizeMode = fill, FillWeight = 26, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (title, weight) in new[] { ("ROMs", 12), ("Tamaño", 16), ("Sin categoría", 16), ("Sin imagen", 16), ("Sin vídeo", 14) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, AutoSizeMode = fill, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = right });
        void Add(Image? icon, string name, int roms, long bytes, GapCount g, bool total = false)
        {
            int i = grid.Rows.Add(icon!, name, roms.ToString("N0"), SizeText(bytes), g.NoMetadata.ToString("N0"), g.NoImage.ToString("N0"), g.NoVideo.ToString("N0"));
            var row = grid.Rows[i];
            if (total) { row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold); row.DefaultCellStyle.BackColor = SystemColors.Control; }
            for (int c = 4; c <= 6; c++)
            {
                int value = c == 4 ? g.NoMetadata : c == 5 ? g.NoImage : g.NoVideo;
                if (value > 0) { row.Cells[c].Style.ForeColor = Alert; row.Cells[c].Style.Font = new Font(Font, FontStyle.Bold); }
            }
        }
        foreach (var p in s.PerSystem.OrderByDescending(p => p.Roms).ThenBy(p => p.System, StringComparer.OrdinalIgnoreCase))
            Add(PixelSystemIcons.Get(p.System, 20), p.System, p.Roms, p.Bytes, p.Gaps);
        Add(null, Loc.T("Total"), s.Roms, s.RomBytes, s.Gaps, total: true);
        if (removeSystem is not null)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            var remove = new ToolStripMenuItem("Eliminar sistema completo…") { ForeColor = Color.Firebrick };
            menu.Items.Add(remove);
            grid.ContextMenuStrip = menu;
            // El clic derecho también selecciona la fila; la fila Total (sin icono) no tiene menú.
            grid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; grid.CurrentCell = grid.Rows[e.RowIndex].Cells[1]; } };
            menu.Opening += (_, e) => { if (grid.CurrentRow is null || !grid.CurrentRow.Selected || grid.CurrentRow.Index >= grid.Rows.Count - 1) e.Cancel = true; };
            remove.Click += async (_, _) =>
            {
                var row = grid.CurrentRow;
                if (row is null || row.Index >= grid.Rows.Count - 1 || row.Cells[1].Value is not string name) return;
                if (await removeSystem(name)) grid.Rows.Remove(row);
            };
        }
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildCategories()
    {
        var page = new TabPage("Categorías") { Padding = new Padding(8) };
        var grid = NewGrid();
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        var right = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Categoría", AutoSizeMode = fill, FillWeight = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROMs", AutoSizeMode = fill, FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = right });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "%", AutoSizeMode = fill, FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = right });
        const int top = 15;
        foreach (var (category, roms) in s.Categories.Take(top))
            grid.Rows.Add(category, roms.ToString("N0"), s.Roms > 0 ? $"{roms * 100.0 / s.Roms:0.#} %" : "");
        int others = s.Categories.Skip(top).Sum(c => c.Roms);
        if (others > 0) grid.Rows.Add(Loc.T("Otras"), others.ToString("N0"), s.Roms > 0 ? $"{others * 100.0 / s.Roms:0.#} %" : "");
        page.Controls.Add(grid);
        return page;
    }
    // ---- Compartir

    private static string SizeText(long n)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"]; double x = n; int i = 0;
        while (x >= 1024 && i < 4) { x /= 1024; i++; }
        return i == 0 ? $"{n:N0} B" : $"{x:0.##} {u[i]}";
    }

    private static string T(string es, string en) => Loc.Translating ? en : es;

    // Texto corto con emojis, pensado para pegar en una red social.
    private string ShareText()
    {
        var topSystems = string.Join(", ", s.PerSystem.OrderByDescending(p => p.Roms).Take(3).Select(p => $"{p.System} ({p.Roms:N0})"));
        var sb = new StringBuilder();
        sb.AppendLine(T("🎮 Mi colección de ES-DE", "🎮 My ES-DE collection"));
        sb.AppendLine(T($"🕹️ {s.Roms:N0} ROMs en {s.Systems:N0} sistemas", $"🕹️ {s.Roms:N0} ROMs across {s.Systems:N0} systems"));
        sb.AppendLine(T($"💾 {SizeText(s.RomBytes)} de ROMs y {SizeText(s.MediaBytes)} de medios", $"💾 {SizeText(s.RomBytes)} of ROMs and {SizeText(s.MediaBytes)} of media"));
        if (topSystems.Length > 0) sb.AppendLine(T($"🏆 Más ROMs: {topSystems}", $"🏆 Most ROMs: {topSystems}"));
        if (s.Categories.Count > 0) sb.AppendLine(T($"🎯 Categoría estrella: {s.Categories[0].Category}", $"🎯 Top category: {s.Categories[0].Category}"));
        sb.AppendLine("#ESDE #RetroGaming #ESDERomCleaner");
        return sb.ToString().TrimEnd();
    }

    // Tarjeta de 1080 px de ancho con las cifras principales y todos los sistemas (icono, nombre y ROMs) en cuatro columnas; el alto crece con el número de sistemas.
    private Bitmap DrawCard()
    {
        const int width = 1080, margin = 60, columns = 4, tileHeight = 84, headerHeight = 400;
        var systems = s.PerSystem.OrderByDescending(p => p.Roms).ThenBy(p => p.System, StringComparer.OrdinalIgnoreCase).ToList();
        int rows = (systems.Count + columns - 1) / columns;
        int height = Math.Min(8000, headerHeight + rows * tileHeight + 150);
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var back = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(18, 20, 44), Color.FromArgb(62, 24, 96), 90f)) g.FillRectangle(back, 0, 0, width, height);
        using var title = new Font("Segoe UI", 50, FontStyle.Bold); using var big = new Font("Segoe UI", 38, FontStyle.Bold);
        using var label = new Font("Segoe UI", 24); using var name = new Font("Segoe UI", 19, FontStyle.Bold); using var small = new Font("Segoe UI", 17);
        using var white = new SolidBrush(Color.White); using var gray = new SolidBrush(Color.FromArgb(190, 190, 215)); using var accent = new SolidBrush(Color.FromArgb(130, 230, 130));
        g.DrawString(T("Mi colección ES-DE", "My ES-DE collection"), title, white, margin, 50);

        // Cuatro cifras principales.
        var stats = new[]
        {
            (s.Roms.ToString("N0"), "ROMs"), (s.Systems.ToString("N0"), T("sistemas", "systems")),
            (SizeText(s.RomBytes), T("de ROMs", "of ROMs")), (SizeText(s.MediaBytes), T("de medios", "of media"))
        };
        // Las cifras se colocan una tras otra con un hueco fijo (no en columnas iguales, donde «138,41 GB» invadía a la siguiente) y, si no caben, se reduce la letra.
        float statSize = 38f, statGap = 36f, available = width - margin * 2;
        var statFont = new Font("Segoe UI", statSize, FontStyle.Bold);
        float[] widths;
        while (true)
        {
            widths = stats.Select(s => Math.Max(g.MeasureString(s.Item1, statFont).Width - 12, g.MeasureString(s.Item2, label).Width)).ToArray();
            if (widths.Sum() + statGap * (stats.Length - 1) <= available || statSize <= 20f) break;
            statFont.Dispose(); statSize -= 2f; statFont = new Font("Segoe UI", statSize, FontStyle.Bold);
        }
        float statX = margin;
        for (int i = 0; i < stats.Length; i++)
        {
            g.DrawString(stats[i].Item1, statFont, accent, statX - 6, 175);
            g.DrawString(stats[i].Item2, label, gray, statX + 4, 250);
            statX += widths[i] + statGap;
        }
        statFont.Dispose();
        g.DrawString(T("Sistemas", "Systems"), label, gray, margin, 340);
        int columnWidth = (width - margin * 2) / columns;
        using var ellipsis = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int i = 0; i < systems.Count; i++)
        {
            int x = margin + (i % columns) * columnWidth, y = headerHeight + (i / columns) * tileHeight;
            g.DrawImage(PixelSystemIcons.Get(systems[i].System, 64), x, y, 64, 64);
            var textBox = new RectangleF(x + 72, 0, columnWidth - 80, 30);
            g.DrawString(systems[i].System, name, white, textBox with { Y = y + 2 }, ellipsis);
            g.DrawString(T($"{systems[i].Roms:N0} ROMs", $"{systems[i].Roms:N0} ROMs"), small, gray, textBox with { Y = y + 34 }, ellipsis);
        }
        int bottom = headerHeight + rows * tileHeight + 20;
        if (s.Categories.Count > 0)
            g.DrawString(T($"Categoría estrella: {s.Categories[0].Category}", $"Top category: {s.Categories[0].Category}"), label, white, margin, bottom);
        // Pie: el programa a la izquierda y la fecha de generación centrada abajo.
        float footerY = height - 56;
        g.DrawString("ESDERomCleaner", small, gray, margin, footerY);
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString($"{T("Generado el", "Generated on")} {DateTime.Now:d}", small, gray, new RectangleF(0, footerY, width, 34), centered);
        return bmp;
    }
}