namespace ESDERomCleaner;

// Compara el contenido de una ROM nueva con el del archivo que ya existe en la colección (mismo nombre), en dos grupos lado a lado: el nuevo y el existente,
// con las filas alineadas (el mismo archivo interno en la misma altura; si solo está en un lado, el otro queda en blanco). Debajo de cada grupo, su botón:
// «Reemplazar por el nuevo» (DialogResult.OK) o «Mantener el existente» (se cierra sin más).
internal sealed class RomCompareDialog : LocalizedForm
{
    private readonly string newPath, existingPath;
    private readonly DataGridView newGrid = MakeGrid(), oldGrid = MakeGrid();
    private readonly Label newTitle = new() { AutoSize = false, Tag = "noloc", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label oldTitle = new() { AutoSize = false, Tag = "noloc", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label summary = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText, Tag = "noloc" };
    private readonly CancellationTokenSource cts = new();
    private bool syncing;
    private static readonly Color NewColor = Color.FromArgb(34, 130, 70), OldColor = Color.FromArgb(63, 111, 159);

    private static void ColorButton(Button b, Color color)
    {
        b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false; b.BackColor = color; b.ForeColor = Color.White;
        b.FlatAppearance.BorderColor = color; b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.2f); b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, 0.1f);
        b.Font = new Font(b.Font, FontStyle.Bold);
    }

    public RomCompareDialog(string newPath, string existingPath, string existingDisplay)
    {
        this.newPath = newPath; this.existingPath = existingPath;
        Text = "Comparar contenido";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(1000, 560);

        var banner = new RaHeader("Comparar contenido", "El mismo nombre de archivo, ¿con el mismo contenido? Las filas que difieren van primero.", 68, 48, logo: false);
        banner.Icon.Image = RaUi.MagnifierIcon(3);

        newTitle.Text = "  " + Path.GetFileName(newPath);
        oldTitle.Text = "  " + existingDisplay;

        // Cada botón lleva el color de la píldora de su grupo: verde para el nuevo, azul para el existente.
        var replace = RaUi.MakeButton("Reemplazar por el nuevo", false, 210); replace.DialogResult = DialogResult.OK; ColorButton(replace, NewColor);
        var keep = RaUi.MakeButton("Mantener el existente", true, 210); keep.DialogResult = DialogResult.Cancel; ColorButton(keep, OldColor);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = RaUi.Body, Padding = new Padding(10, 10, 10, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(Group("NUEVO", NewColor, newTitle, newGrid), 0, 0);
        layout.Controls.Add(Group("EXISTENTE", OldColor, oldTitle, oldGrid), 1, 0);
        layout.Controls.Add(summary, 0, 1); layout.SetColumnSpan(summary, 2); summary.Dock = DockStyle.Fill;
        replace.Anchor = AnchorStyles.None; keep.Anchor = AnchorStyles.None;
        layout.Controls.Add(replace, 0, 2); layout.Controls.Add(keep, 1, 2);

        Controls.Add(layout); Controls.Add(banner);
        AcceptButton = keep; CancelButton = keep;

        // Las dos tablas se desplazan juntas para que las filas queden siempre a la misma altura.
        newGrid.Scroll += (_, e) => Sync(newGrid, oldGrid, e);
        oldGrid.Scroll += (_, e) => Sync(oldGrid, newGrid, e);
        Shown += async (_, _) => await LoadAsync();
        FormClosed += (_, _) => cts.Cancel();
        CollectionTitle.Attach(this);
    }

    private void Sync(DataGridView from, DataGridView to, ScrollEventArgs e)
    {
        if (syncing || e.ScrollOrientation != ScrollOrientation.VerticalScroll) return;
        syncing = true;
        try { if (from.FirstDisplayedScrollingRowIndex >= 0 && from.FirstDisplayedScrollingRowIndex < to.RowCount) to.FirstDisplayedScrollingRowIndex = from.FirstDisplayedScrollingRowIndex; }
        catch (InvalidOperationException) { }
        finally { syncing = false; }
    }

    private static DataGridView MakeGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 22
        };
        RaUi.StyleGrid(g, 22, 22, selectionVisible: false);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Archivo interno", AutoSizeMode = fill, FillWeight = 38, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tamaño", AutoSizeMode = fill, FillWeight = 18, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CRC", AutoSizeMode = fill, FillWeight = 20, MinimumWidth = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Estado", AutoSizeMode = fill, FillWeight = 24, MinimumWidth = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        return g;
    }

    // Tarjeta con una franja de título («NUEVO» / «EXISTENTE» y el nombre del archivo) y su tabla debajo.
    private static Panel Group(string tag, Color color, Label title, DataGridView grid)
    {
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = RaUi.Border, Margin = new Padding(0, 0, 5, 0) };
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var tagLabel = new PillLabel(tag, color) { Dock = DockStyle.Left, Width = 96 };
        var head = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.White };
        title.Dock = DockStyle.Fill;
        head.Controls.Add(title); head.Controls.Add(tagLabel);
        inner.Controls.Add(grid); inner.Controls.Add(head);
        frame.Controls.Add(inner);
        return frame;
    }

    private async Task LoadAsync()
    {
        summary.Text = Loc.T("Leyendo el contenido de los dos archivos…");
        List<(string Name, uint Crc, long Size)>? a, b;
        try
        {
            (a, b) = await Task.Run(() => (RomIdentifier.ContentEntries(newPath, cts.Token), RomIdentifier.ContentEntries(existingPath, cts.Token)), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        if (IsDisposed) return;
        if (a is null || b is null) { summary.Text = Loc.T("No se pudo leer el contenido de uno de los archivos (o es una ROM suelta demasiado grande)."); return; }

        newTitle.Text = "  " + Path.GetFileName(newPath) + "  ·  " + Loc.T("{0} archivos", a.Count);
        oldTitle.Text = "  " + oldTitle.Text.Trim() + "  ·  " + Loc.T("{0} archivos", b.Count);

        // Un archivo interno del nuevo coincide con el del existente si tiene el mismo CRC y tamaño (aunque cambie de nombre) o, si no, el mismo nombre con otro CRC.
        var rows = new List<(string State, string NewName, string NewSize, string NewCrc, string OldName, string OldSize, string OldCrc)>();
        var unmatched = b.ToList();
        foreach (var n in a.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            int same = unmatched.FindIndex(o => o.Crc == n.Crc && o.Size == n.Size);
            if (same >= 0)
            {
                var o = unmatched[same]; unmatched.RemoveAt(same);
                rows.Add(("Igual", n.Name, Bytes(n.Size), Crc(n.Crc), o.Name, Bytes(o.Size), Crc(o.Crc)));
                continue;
            }
            int named = unmatched.FindIndex(o => Path.GetFileName(o.Name).Equals(Path.GetFileName(n.Name), StringComparison.OrdinalIgnoreCase));
            if (named >= 0)
            {
                var o = unmatched[named]; unmatched.RemoveAt(named);
                rows.Add(("Distinto", n.Name, Bytes(n.Size), Crc(n.Crc), o.Name, Bytes(o.Size), Crc(o.Crc)));
            }
            else rows.Add(("Solo en el nuevo", n.Name, Bytes(n.Size), Crc(n.Crc), "", "", ""));
        }
        foreach (var o in unmatched.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(("Solo en el existente", "", "", "", o.Name, Bytes(o.Size), Crc(o.Crc)));

        // Primero lo que difiere, para que se vea sin desplazarse.
        foreach (var r in rows.OrderBy(r => r.State == "Igual" ? 1 : 0).ThenBy(r => r.NewName.Length > 0 ? r.NewName : r.OldName, StringComparer.OrdinalIgnoreCase))
        {
            string newState = r.State switch { "Solo en el existente" => "", "Solo en el nuevo" => "Solo aquí", _ => r.State };
            string oldState = r.State switch { "Solo en el nuevo" => "", "Solo en el existente" => "Solo aquí", _ => r.State };
            int i = newGrid.Rows.Add(r.NewName, r.NewSize, r.NewCrc, newState);
            int j = oldGrid.Rows.Add(r.OldName, r.OldSize, r.OldCrc, oldState);
            Color back = r.State switch { "Distinto" => Color.FromArgb(255, 236, 204), "Igual" => Color.White, _ => Color.FromArgb(255, 245, 214) };
            newGrid.Rows[i].DefaultCellStyle.BackColor = r.State == "Solo en el existente" ? Color.FromArgb(244, 245, 248) : back;
            oldGrid.Rows[j].DefaultCellStyle.BackColor = r.State == "Solo en el nuevo" ? Color.FromArgb(244, 245, 248) : back;
            if (r.State != "Igual")
            {
                newGrid.Rows[i].Cells[3].Style.Font = new Font(newGrid.Font, FontStyle.Bold);
                oldGrid.Rows[j].Cells[3].Style.Font = new Font(oldGrid.Font, FontStyle.Bold);
            }
        }
        int equal = rows.Count(r => r.State == "Igual"), different = rows.Count(r => r.State == "Distinto");
        int onlyNew = rows.Count(r => r.State == "Solo en el nuevo"), onlyOld = rows.Count(r => r.State == "Solo en el existente");
        summary.Text = Loc.T("Iguales: {0} · distintos: {1} · solo en el nuevo: {2} · solo en el existente: {3}", equal, different, onlyNew, onlyOld);
    }

    private static string Bytes(long size) => size.ToString("N0");
    private static string Crc(uint crc) => crc.ToString("X8");
}

// Etiqueta en forma de píldora (rectángulo con las esquinas totalmente redondeadas) con el texto en blanco y negrita.
internal sealed class PillLabel : Control
{
    private readonly Color color;

    public PillLabel(string text, Color color)
    {
        this.color = color; Text = text; Tag = "noloc";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var pill = new Rectangle(6, (Height - 20) / 2, Width - 12, 20);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = pill.Height;
        path.AddArc(pill.X, pill.Y, d, d, 90, 180); path.AddArc(pill.Right - d, pill.Y, d, d, 270, 180); path.CloseFigure();
        using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
        using var font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
        TextRenderer.DrawText(g, Text, font, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
