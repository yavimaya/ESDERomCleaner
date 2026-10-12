namespace ESDERomCleaner;

internal enum GamelistPlanChoice { Cancel, AddOnly, AddAndCategorize, AddAndScrape }

// Tabla solo informativa: no recibe el foco, no se edita y no muestra selección.
internal sealed class StaticGrid : DataGridView
{
    public StaticGrid() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
    protected override void OnSelectionChanged(EventArgs e) { base.OnSelectionChanged(e); ClearSelection(); }
    protected override void OnCellEnter(DataGridViewCellEventArgs e) { }
    protected override void OnMouseDown(MouseEventArgs e) { }
    protected override bool ProcessDialogKey(Keys keyData) => false;
    protected override bool ProcessDataGridViewKey(KeyEventArgs e) => false;

    // Aspecto común de las tablas informativas de los avisos.
    public void ApplyInfoStyle(int rowHeight, int headerHeight)
    {
        ReadOnly = true; AllowUserToAddRows = false; AllowUserToDeleteRows = false; AllowUserToResizeRows = false; AllowUserToResizeColumns = false;
        AllowUserToOrderColumns = false; RowHeadersVisible = false; SelectionMode = DataGridViewSelectionMode.FullRowSelect; MultiSelect = false;
        BackgroundColor = SystemColors.Window; BorderStyle = BorderStyle.FixedSingle; EnableHeadersVisualStyles = false;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; ColumnHeadersHeight = headerHeight;
        Cursor = Cursors.Default;
        RowTemplate.Height = rowHeight;
        ColumnHeadersDefaultCellStyle.SelectionBackColor = ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        ColumnHeadersDefaultCellStyle.SelectionForeColor = ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        DefaultCellStyle.SelectionBackColor = SystemColors.Window;
        DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
    }
}

// Aviso de sistemas / ROMs nuevos detectados: tabla con lo que se va a añadir y pregunta si además se quiere scrapear.
internal sealed class GamelistPlanDialog : LocalizedForm
{
    public GamelistPlanChoice Choice { get; private set; } = GamelistPlanChoice.Cancel;

    public GamelistPlanDialog(IReadOnlyList<GeneratedGamelist> plan, bool allowScrape)
    {
        bool allNew = plan.All(p => !p.Append);
        Text = allNew ? "Sistemas nuevos" : "Sistemas y ROMs nuevos";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        const int rowHeight = 20, headerHeight = 22;
        int visibleRows = Math.Min(plan.Count, 8);
        int gridHeight = headerHeight + visibleRows * rowHeight + 2;
        const int width = 520;
        int hintHeight = allowScrape ? 20 : 0;
        ClientSize = new Size(width, 8 + 20 + 4 + gridHeight + 8 + 20 + hintHeight + 4 + 40);

        var header = new Label
        {
            Text = allNew ? "Detectados los siguientes sistemas nuevos:" : "Detectados los siguientes sistemas con ROMs nuevas:",
            Location = new Point(10, 8), Size = new Size(width - 20, 20), AutoSize = false
        };
        var grid = new StaticGrid
        {
            Location = new Point(10, 32), Size = new Size(width - 20, gridHeight),
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
            AllowUserToOrderColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = headerHeight,
            ScrollBars = plan.Count > visibleRows ? ScrollBars.Vertical : ScrollBars.None, Cursor = Cursors.Default
        };
        grid.RowTemplate.Height = rowHeight;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid.DefaultCellStyle.SelectionBackColor = SystemColors.Window;
        grid.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", Width = 170, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROMs", Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Situación", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var item in plan) grid.Rows.Add(item.System, item.RomPaths.Count.ToString("N0"), item.Append ? "ROMs nuevas" : "Sistema nuevo");
        grid.ClearSelection();

        var question = new Label
        {
            Text = allowScrape ? "¿Desea categorizar estas ROMs, o categorizar y descargar también sus medios?" : "¿Desea añadir estas ROMs a sus gamelists?",
            Location = new Point(10, 32 + gridHeight + 8), Size = new Size(width - 20, 20), AutoSize = false
        };

        var hint = new Label
        {
            Text = "Puede hacerlo más adelante desde el menú de Scrapeo.", ForeColor = SystemColors.GrayText,
            Location = new Point(10, 32 + gridHeight + 8 + 20), Size = new Size(width - 20, 20), AutoSize = false, Visible = allowScrape
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6, 6, 6, 2) };
        Button Make(string text, GamelistPlanChoice choice, int minWidth)
        {
            var button = new Button { Text = text, MinimumSize = new Size(minWidth, 26), AutoSize = true, DialogResult = choice == GamelistPlanChoice.Cancel ? DialogResult.Cancel : DialogResult.OK };
            button.Click += (_, _) => Choice = choice;
            return button;
        }
        // De derecha a izquierda: Cancelar, Solo añadir, Sí (el principal queda a la izquierda).
        var cancel = Make("Cancelar", GamelistPlanChoice.Cancel, 80);
        buttons.Controls.Add(cancel);
        Button primary;
        if (allowScrape)
        {
            buttons.Controls.Add(Make("Solo añadir", GamelistPlanChoice.AddOnly, 90));
            buttons.Controls.Add(Make("Categorizar y scrapear", GamelistPlanChoice.AddAndScrape, 140));
            primary = Make("Categorizar", GamelistPlanChoice.AddAndCategorize, 90);
        }
        else primary = Make("Añadir ROMs", GamelistPlanChoice.AddOnly, 100);
        buttons.Controls.Add(primary);
        AcceptButton = primary; CancelButton = cancel;
        Controls.Add(header); Controls.Add(grid); Controls.Add(question); Controls.Add(hint); Controls.Add(buttons);
        Shown += (_, _) => primary.Focus();
    }
}
