namespace ESDERomCleaner;

internal sealed class TraceViewerDialog : LocalizedForm
{
    private const int MaxRecords = 2000;
    private const string AllActions = "(todas las acciones)";

    private readonly TraceLog trace;
    private readonly DataGridView grid = new();
    private readonly ComboBox actionFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly TextBox textFilter = new() { Width = 260, PlaceholderText = "Buscar en acción o detalles" };
    private readonly Button refresh = new() { Text = "Actualizar", AutoSize = true };
    private readonly Label summary = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 0) };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font("Consolas", 9F) };
    private List<TraceRecord> records = new();
    private bool loading;

    public TraceViewerDialog(TraceLog trace)
    {
        this.trace = trace;
        Text = "Trazabilidad";
        Width = 1000;
        Height = 640;
        MinimumSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        WireEvents();
        Load += (_, _) => Reload();
    }

    private void BuildLayout()
    {
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(6, 4, 6, 4) };
        toolbar.Controls.Add(new Label { Text = "Acción:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        toolbar.Controls.Add(actionFilter);
        toolbar.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(12, 7, 3, 0) });
        toolbar.Controls.Add(textFilter);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(summary);

        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.BackgroundColor = SystemColors.Window;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "when", HeaderText = "Fecha", Width = 140, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "action", HeaderText = "Acción", Width = 200, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "details", HeaderText = "Detalles", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150, SortMode = DataGridViewColumnSortMode.Automatic });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Padding.Empty, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        Controls.Add(layout);
    }

    private void WireEvents()
    {
        refresh.Click += (_, _) => Reload();
        actionFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        textFilter.TextChanged += (_, _) => ApplyFilter();
        grid.SelectionChanged += (_, _) => ShowSelected();
    }

    private void Reload()
    {
        records = trace.ReadRecords(MaxRecords);
        string previous = actionFilter.SelectedItem as string ?? AllActions;
        loading = true;
        try
        {
            actionFilter.Items.Clear();
            actionFilter.Items.Add(AllActions);
            foreach (var action in records.Select(r => r.Action).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                actionFilter.Items.Add(action);
            int index = actionFilter.Items.IndexOf(previous);
            actionFilter.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { loading = false; }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (loading) return;
        string? action = actionFilter.SelectedIndex > 0 ? actionFilter.SelectedItem as string : null;
        string text = textFilter.Text.Trim();
        var rows = records
            .Where(r => (action is null || r.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
                && (text.Length == 0 || r.Action.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Details.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Utc)
            .ToList();

        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var r in rows)
            {
                int i = grid.Rows.Add(r.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), r.Action, r.Details.ReplaceLineEndings(" "));
                grid.Rows[i].Tag = r;
                if (r.Action.Contains("error", StringComparison.OrdinalIgnoreCase)) grid.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
            }
        }
        finally { grid.ResumeLayout(); }

        summary.Text = records.Count == 0 ? "Aún no hay trazas." : $"{rows.Count:N0} de {records.Count:N0} eventos (últimos {MaxRecords:N0} como máximo)";
        if (grid.Rows.Count > 0)
        {
            grid.ClearSelection();
            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[0];
        }
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (grid.CurrentRow?.Tag is TraceRecord r)
            detail.Text = $"{r.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {r.Action}{Environment.NewLine}{Environment.NewLine}{r.Details}";
        else detail.Clear();
    }
}
