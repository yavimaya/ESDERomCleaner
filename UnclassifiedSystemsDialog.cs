namespace ESDERomCleaner;

// Aviso tras analizar: sistemas cuyas ROMs no tienen categoría. Tabla informativa y pregunta si se quieren scrapear.
internal sealed class UnclassifiedSystemsDialog : Form
{
    public UnclassifiedSystemsDialog(IReadOnlyList<(string System, int Roms)> systems)
    {
        Text = "Sistemas sin categorización";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        const int rowHeight = 20, headerHeight = 22, width = 420;
        int visibleRows = Math.Min(systems.Count, 8);
        int gridHeight = headerHeight + visibleRows * rowHeight + 2;
        ClientSize = new Size(width, 8 + 20 + 4 + gridHeight + 8 + 20 + 20 + 4 + 40);

        var header = new Label { Text = "Sistemas sin categorización:", Location = new Point(10, 8), Size = new Size(width - 20, 20), AutoSize = false };
        var grid = new StaticGrid
        {
            Location = new Point(10, 32), Size = new Size(width - 20, gridHeight),
            ScrollBars = systems.Count > visibleRows ? ScrollBars.Vertical : ScrollBars.None
        };
        grid.ApplyInfoStyle(rowHeight, headerHeight);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROMs", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });
        foreach (var (system, roms) in systems) grid.Rows.Add(system, roms.ToString("N0"));
        grid.ClearSelection();

        int y = 32 + gridHeight + 8;
        var question = new Label { Text = "¿Desea categorizar y descargar los medios de las ROMs de estos sistemas?", Location = new Point(10, y), Size = new Size(width - 20, 20), AutoSize = false };
        var hint = new Label { Text = "Puede hacerlo más adelante desde el menú de Scrapeo.", ForeColor = SystemColors.GrayText, Location = new Point(10, y + 20), Size = new Size(width - 20, 20), AutoSize = false };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6, 6, 6, 2) };
        var cancel = new Button { Text = "Ahora no", DialogResult = DialogResult.Cancel, MinimumSize = new Size(80, 26), AutoSize = true };
        var ok = new Button { Text = "Sí, categorizar", DialogResult = DialogResult.OK, MinimumSize = new Size(110, 26), AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(header); Controls.Add(grid); Controls.Add(question); Controls.Add(hint); Controls.Add(buttons);
        Shown += (_, _) => ok.Focus();
    }
}
