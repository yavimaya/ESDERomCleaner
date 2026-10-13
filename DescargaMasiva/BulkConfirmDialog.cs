namespace ESDERomCleaner;

// Resumen previo a la descarga masiva: tabla con los sistemas a scrapear y las líneas sobre sobrescritura y búsqueda por nombre.
internal sealed class BulkConfirmDialog : LocalizedForm
{
    public BulkConfirmDialog(IReadOnlyList<(string System, int Roms)> systems, IReadOnlyList<string> footer, string okText, bool confirm)
    {
        Text = "Descarga masiva de medios";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        const int width = 400, rowHeight = 20, headerHeight = 22;
        int visibleRows = Math.Min(systems.Count, 8);
        int gridHeight = headerHeight + visibleRows * rowHeight + 2;

        var header = new Label { Text = "Se van a scrapear los elementos seleccionados:", Location = new Point(10, 8), Size = new Size(width - 20, 20), AutoSize = false };
        var grid = new StaticGrid
        {
            Location = new Point(10, 32), Size = new Size(width - 20, gridHeight),
            ScrollBars = systems.Count > visibleRows ? ScrollBars.Vertical : ScrollBars.None
        };
        grid.ApplyInfoStyle(rowHeight, headerHeight);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "ROMs", Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter },
            HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } }
        });
        foreach (var (system, roms) in systems) grid.Rows.Add(system, roms.ToString("N0"));
        grid.ClearSelection();

        int y = 32 + gridHeight + 8;
        var footerLabel = new Label { Text = string.Join("\r\n", footer), Location = new Point(10, y), AutoSize = false };
        var footerSize = TextRenderer.MeasureText(footerLabel.Text, Font, new Size(width - 20, 0), TextFormatFlags.WordBreak);
        footerLabel.Size = new Size(width - 20, footerSize.Height + 4);

        ClientSize = new Size(width, y + footerLabel.Height + 8 + 40);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6, 6, 6, 2) };
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, MinimumSize = new Size(110, 26), AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, MinimumSize = new Size(80, 26), AutoSize = true };
        if (confirm) buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok; CancelButton = confirm ? cancel : ok;
        Controls.Add(header); Controls.Add(grid); Controls.Add(footerLabel); Controls.Add(buttons);
        Shown += (_, _) => ok.Focus();
    }
}
