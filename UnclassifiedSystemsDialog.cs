namespace ESDERomCleaner;

internal enum UnclassifiedChoice { None, Categorize, CategorizeAndScrape }

// Aviso tras analizar: sistemas cuyas ROMs no tienen categoría. Tabla informativa y dos opciones:
// «Categorizar» (solo metadatos, que traen la categoría) o «Categorizar y scrapear» (metadatos y medios).
internal sealed class UnclassifiedSystemsDialog : LocalizedForm
{
    public UnclassifiedChoice Choice { get; private set; } = UnclassifiedChoice.None;

    public UnclassifiedSystemsDialog(IReadOnlyList<(string System, int Roms)> systems)
    {
        Text = "Sistemas sin categorización";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        const int rowHeight = 20, headerHeight = 22, width = 480;
        int visibleRows = Math.Min(systems.Count, 8);
        int gridHeight = headerHeight + visibleRows * rowHeight + 2;
        ClientSize = new Size(width, 8 + 20 + 4 + gridHeight + 8 + 20 + 20 + 4 + 40);

        var header = new Label { Text = $"Sistemas con ROMs sin categoría: {systems.Count:N0} sistemas · {systems.Sum(s => s.Roms):N0} ROMs", Location = new Point(10, 8), Size = new Size(width - 20, 20), AutoSize = false };
        var grid = new StaticGrid
        {
            Location = new Point(10, 32), Size = new Size(width - 20, gridHeight),
            ScrollBars = systems.Count > visibleRows ? ScrollBars.Vertical : ScrollBars.None
        };
        grid.ApplyInfoStyle(rowHeight, headerHeight);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROMs sin categoría", Width = 130, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });
        foreach (var (system, roms) in systems) grid.Rows.Add(system, roms.ToString("N0"));
        grid.ClearSelection();

        int y = 32 + gridHeight + 8;
        var question = new Label { Text = "¿Desea categorizar esas ROMs, o categorizar y descargar también sus medios?", Location = new Point(10, y), Size = new Size(width - 20, 20), AutoSize = false };
        var hint = new Label { Text = "Puede hacerlo más adelante desde el menú de Scrapeo.", ForeColor = SystemColors.GrayText, Location = new Point(10, y + 20), Size = new Size(width - 20, 20), AutoSize = false };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6, 8, 6, 2) };
        Button Make(string text, UnclassifiedChoice choice, int minWidth, DialogResult result)
        {
            var button = new Button { Text = text, DialogResult = result, MinimumSize = new Size(minWidth, 26), AutoSize = true };
            button.Click += (_, _) => Choice = choice;
            return button;
        }
        var cancel = Make("Ahora no", UnclassifiedChoice.None, 80, DialogResult.Cancel);
        var both = Make("Categorizar y scrapear", UnclassifiedChoice.CategorizeAndScrape, 150, DialogResult.OK);
        var categorize = Make("Categorizar", UnclassifiedChoice.Categorize, 90, DialogResult.OK);
        buttons.Controls.Add(cancel); buttons.Controls.Add(both); buttons.Controls.Add(categorize);
        AcceptButton = categorize; CancelButton = cancel;
        Controls.Add(header); Controls.Add(grid); Controls.Add(question); Controls.Add(hint); Controls.Add(buttons);
        Shown += (_, _) => categorize.Focus();
    }
}
