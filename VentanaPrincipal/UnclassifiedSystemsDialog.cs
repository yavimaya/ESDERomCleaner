namespace ESDERomCleaner;

internal enum UnclassifiedChoice { None, Categorize, CategorizeAndScrape }

// Aviso tras analizar: huecos de la colección (ROMs sin categoría, sin imagen y sin vídeo) por sistema. Tabla informativa y dos opciones:
// «Categorizar» (solo metadatos, que traen la categoría) o «Categorizar y scrapear» (metadatos y medios).
internal sealed class UnclassifiedSystemsDialog : LocalizedForm
{
    public UnclassifiedChoice Choice { get; private set; } = UnclassifiedChoice.None;

    private readonly Func<string, int, IReadOnlyList<string>>? namesFor;
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 450 };
    private GapNamesPopup? popup;
    private (int Row, int Col) hover = (-1, -1);

    // namesFor(sistema, tipo) devuelve los archivos de cada hueco (tipo 0 = sin categoría, 1 = sin imagen, 2 = sin vídeo); se muestran al dejar el ratón sobre un número.
    public UnclassifiedSystemsDialog(IReadOnlyList<(string System, int Roms, int NoImage, int NoVideo)> systems, Func<string, int, IReadOnlyList<string>>? namesFor = null)
    {
        this.namesFor = namesFor;
        Text = "Huecos de la colección";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        const int rowHeight = 22, headerHeight = 24, width = 580, bannerHeight = 68, footerHeight = 52;
        int visibleRows = Math.Min(systems.Count, 8);
        int gridHeight = headerHeight + visibleRows * rowHeight + 2;
        ClientSize = new Size(width, bannerHeight + 12 + gridHeight + 10 + 44 + 12 + footerHeight);

        var grid = new StaticGrid
        {
            Dock = DockStyle.Fill,
            ScrollBars = systems.Count > visibleRows ? ScrollBars.Vertical : ScrollBars.None
        };
        grid.ApplyInfoStyle(rowHeight, headerHeight);
        RaUi.StyleGrid(grid, rowHeight, headerHeight, selectionVisible: false); // cabecera oscura y filas con fondos alternos, como en los demás formularios
        var center = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter };
        // El título de cada columna lleva entre paréntesis el número de elementos: sistemas, y el total de ROMs sin categoría, sin imagen y sin vídeo.
        grid.Columns.Add(SystemIconColumn.Create(28));
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = $"Sistema ({systems.Count:N0})", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (title, total) in new[] { ("Sin categoría", systems.Sum(s => s.Roms)), ("Sin imagen", systems.Sum(s => s.NoImage)), ("Sin vídeo", systems.Sum(s => s.NoVideo)) })
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = $"{title} ({total:N0})", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = center, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } }
            });
        foreach (var (system, roms, noImage, noVideo) in systems) grid.Rows.Add(SystemIconColumn.Icon(system, 16), system, roms.ToString("N0"), noImage.ToString("N0"), noVideo.ToString("N0"));
        grid.ClearSelection();
        grid.CellMouseEnter += (_, e) => { HidePopup(); hover = e.RowIndex >= 0 && e.ColumnIndex >= 2 ? (e.RowIndex, e.ColumnIndex) : (-1, -1); if (hover.Row >= 0) { hoverTimer.Stop(); hoverTimer.Start(); } else hoverTimer.Stop(); };
        grid.CellMouseLeave += (_, _) => { hoverTimer.Stop(); HidePopup(); hover = (-1, -1); };
        grid.MouseLeave += (_, _) => { hoverTimer.Stop(); HidePopup(); };
        grid.MouseWheel += (_, _) => HidePopup();
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (namesFor is null || hover.Row < 0 || hover.Row >= grid.Rows.Count) return;
            string system = grid.Rows[hover.Row].Cells[1].Value?.ToString() ?? "";
            var names = namesFor(system, hover.Col - 2);
            if (names.Count == 0) return;
            popup = new GapNamesPopup(names);
            popup.ShowNear(this, Cursor.Position);
        };
        FormClosed += (_, _) => { hoverTimer.Dispose(); HidePopup(); };

        // La tabla va dentro de un marco fino.
        var frame = new Panel { Location = new Point(12, bannerHeight + 12), Size = new Size(width - 24, gridHeight), Padding = new Padding(1), BackColor = RaUi.Border };
        frame.Controls.Add(grid);
        int y = bannerHeight + 12 + gridHeight + 10;
        // Los dos textos de debajo de la tabla van centrados horizontalmente.
        var question = new Label { Text = "¿Desea categorizar esas ROMs, o categorizar y descargar también sus medios?", Location = new Point(10, y), Size = new Size(width - 20, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        var hint = new Label { Text = "Puede hacerlo más adelante desde el menú de Scrapeo.", ForeColor = SystemColors.GrayText, Location = new Point(10, y + 20), Size = new Size(width - 20, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };

        var footer = RaUi.Footer(footerHeight);
        Button Make(string text, UnclassifiedChoice choice, int minWidth, DialogResult result, bool primary)
        {
            var button = new Button { Text = text, DialogResult = result, MinimumSize = new Size(minWidth, 32), AutoSize = true };
            button.Click += (_, _) => Choice = choice;
            RaUi.StyleButton(button, primary);
            return button;
        }
        var cancel = Make("Ahora no", UnclassifiedChoice.None, 90, DialogResult.Cancel, false);
        var both = Make("Categorizar y scrapear", UnclassifiedChoice.CategorizeAndScrape, 170, DialogResult.OK, true);
        var categorize = Make("Categorizar", UnclassifiedChoice.Categorize, 110, DialogResult.OK, false);
        int buttonX = width - 20;
        foreach (var button in new[] { cancel, both, categorize }) { button.Location = new Point(buttonX - button.PreferredSize.Width, 10); buttonX = button.Left - 8; footer.Controls.Add(button); }
        AcceptButton = categorize; CancelButton = cancel;
        // Cabecera oscura como en los últimos formularios, con las piezas de un puzle y una que falta.
        var banner = new RaHeader("Huecos de la colección", "A estas ROMs les falta la categoría, la imagen o el vídeo; puedes completarlas con ScreenScraper.", bannerHeight, 48, logo: false);
        banner.Icon.Image = RaUi.GapsIcon();
        Controls.Add(frame); Controls.Add(question); Controls.Add(hint); Controls.Add(footer); Controls.Add(banner);
        Shown += (_, _) => categorize.Focus();
    }

    private void HidePopup() { popup?.Close(); popup?.Dispose(); popup = null; }
}

// Ventanita sin foco con un archivo por línea; se cierra al apartar el ratón del número.
internal sealed class GapNamesPopup : Form
{
    private const int MaxLines = 30;

    public GapNamesPopup(IReadOnlyList<string> names)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        BackColor = SystemColors.ControlDark; Padding = new Padding(1);
        var lines = names.Take(MaxLines).ToList();
        if (names.Count > MaxLines) lines.Add(string.Format(Loc.T("… y {0} más"), names.Count - MaxLines));
        var font = new Font("Consolas", 9F);
        int width = Math.Min(640, lines.Max(l => TextRenderer.MeasureText(l, font).Width) + 16);
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Font = font, WordWrap = false,
            BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText, Text = string.Join("\r\n", lines), TabStop = false, Cursor = Cursors.Default
        };
        Controls.Add(box);
        ClientSize = new Size(width + 2, lines.Count * font.Height + 10);
        box.Enabled = false; // el texto no capta el ratón
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST: HTTRANSPARENT, el puntero sigue sobre la tabla
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; } } // NOACTIVATE | TOOLWINDOW

    public void ShowNear(IWin32Window owner, Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Min(cursor.X + 16, area.Right - Width), y = cursor.Y + 18;
        if (y + Height > area.Bottom) y = Math.Max(area.Top, cursor.Y - Height - 8);
        Location = new Point(Math.Max(area.Left, x), y);
        Show(owner);
    }
}
