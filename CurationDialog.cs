using System.Text;
using System.Text.Json;

namespace ESDERomCleaner;

// Análisis de curado: aplica a toda la colección las categorías (géneros) y supracategorías marcadas y resume cuántas ROMs
// señalarían, para valorar si merece la pena. Es de solo lectura: no oculta, copia ni borra nada. La elección de sistemas se hace
// en la ventana principal.
internal sealed class CurationDialog : Form
{
    private sealed record Hit(RomRow Row, CurationRule Rule, string Reason);

    private readonly IReadOnlyList<RomRow> roms;
    private readonly TraceLog trace;
    // Las supracategorías son editables (botón «Editar supracategorías…»); sus definiciones se guardan aparte.
    private List<SupraDefinition> supraDefs = new();
    private CurationRule[] supra = Array.Empty<CurationRule>();
    private readonly List<CurationRule> genreRules = new();
    private readonly Dictionary<string, CurationRule> ruleById = new();
    private readonly List<Hit> hits = new();
    private readonly Dictionary<string, int> counts = new();
    private readonly HashSet<string> checkedIds = new();
    // ROMs que el usuario ha excluido a mano de la selección (doble clic); se recuerdan entre sesiones.
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private Font? strikeFont;
    private string? activeRuleId;
    private bool updating;

    private readonly DataGridView supraGrid = new();
    private readonly DataGridView genresGrid = new();
    private readonly TextBox genreFilter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filtrar categorías…" };
    private readonly DataGridView candidatesGrid = new();
    private readonly DataGridView systemsGrid = new();
    private readonly RadioButton showSelected = new() { Text = "Candidatas de la fila seleccionada", AutoSize = true, Checked = true, Margin = new Padding(3, 3, 14, 3) };
    private readonly RadioButton showChecked = new() { Text = "De todo lo marcado (sin repetir)", AutoSize = true };
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), AutoEllipsis = true };
    private readonly Label note = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button editSupraButton = new() { Text = "Editar supracategorías…", AutoSize = true, MinimumSize = new Size(170, 28) };
    private readonly Button exportButton = new() { Text = "Exportar candidatas…", AutoSize = true, MinimumSize = new Size(150, 30) };
    private readonly Button closeButton = new() { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Cancel };

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "curation.json");

    public CurationDialog(IReadOnlyList<RomRow> allRoms, TraceLog trace)
    {
        roms = allRoms.Where(r => r.Status == RomStatus.Ok && r.Path.Length > 0).ToList();
        this.trace = trace;
        Text = "Reglas de curado — análisis";
        Width = 1280; Height = 780; MinimumSize = new Size(1040, 620);
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = closeButton;

        // Una regla por cada género que existe en la colección, y las supracategorías por encima.
        foreach (string genre in roms.Select(CurationRule.EffectiveGenre).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
            genreRules.Add(CurationRule.ForGenre(genre));
        supraDefs = SupraStore.Load() ?? CurationRuleSet.Defaults(genreRules.Select(r => r.Title));
        BuildRules();

        var state = LoadState();
        if (state is null) foreach (var def in supraDefs.Where(d => d.DefaultOn)) checkedIds.Add(def.Id);
        else
        {
            foreach (string id in state.Checked.Where(ruleById.ContainsKey)) checkedIds.Add(id);
            foreach (string key in state.Excluded) excluded.Add(key);
        }

        BuildLayout();
        FillSupra();
        FillGenres();

        foreach (var grid in new[] { supraGrid, genresGrid })
        {
            var current = grid;
            current.CurrentCellDirtyStateChanged += (_, _) => { if (current.IsCurrentCellDirty) current.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            current.CellValueChanged += (_, e) => OnCheckChanged(current, e.RowIndex, e.ColumnIndex);
            current.SelectionChanged += (_, _) => OnRuleSelected(current);
        }
        genreFilter.TextChanged += (_, _) => FillGenres();
        showSelected.CheckedChanged += (_, _) => { if (!updating) RefreshCandidates(); };
        exportButton.Click += (_, _) => Export();
        editSupraButton.Click += (_, _) => EditSupra();
        candidatesGrid.CellDoubleClick += (_, e) => ToggleExcluded(e.RowIndex);
        FormClosed += (_, _) => strikeFont?.Dispose();
        Shown += (_, _) => RefreshAll();
    }

    private void BuildLayout()
    {
        Configure(supraGrid);
        supraGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 30 });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rule", HeaderText = "Supracategoría", Width = 190, ReadOnly = true });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "ROMs", Width = 55, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Qué incluye", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        var supraLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        supraLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        supraLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        supraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        supraLayout.Controls.Add(supraGrid, 0, 0);
        editSupraButton.Anchor = AnchorStyles.None;
        supraLayout.Controls.Add(editSupraButton, 0, 1);
        var supraGroup = new GroupBox { Text = "Supracategorías", Dock = DockStyle.Fill };
        supraGroup.Controls.Add(supraLayout);

        Configure(genresGrid);
        genresGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 30 });
        genresGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rule", HeaderText = "Categoría (género)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        genresGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "ROMs", Width = 60, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        var genresLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        genresLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        genresLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        genresLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        genresLayout.Controls.Add(genreFilter, 0, 0);
        genresLayout.Controls.Add(genresGrid, 0, 1);
        var genresGroup = new GroupBox { Text = "Categorías existentes en la colección", Dock = DockStyle.Fill };
        genresGroup.Controls.Add(genresLayout);

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        left.Controls.Add(supraGroup, 0, 0);
        left.Controls.Add(genresGroup, 0, 1);

        Configure(candidatesGrid);
        foreach (var (name, header, width) in new[] { ("state", "Estado", 66), ("system", "Sistema", 90), ("rom", "ROM", 220), ("game", "Nombre", 190), ("genre", "Género", 140), ("size", "Tamaño", 70), ("rules", "Regla(s)", 150), ("reason", "Motivo", 0) })
            candidatesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width > 0 ? width : 100, ReadOnly = true });
        candidatesGrid.Columns["reason"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        candidatesGrid.Columns["size"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        modeRow.Controls.Add(showSelected);
        modeRow.Controls.Add(showChecked);
        modeRow.Controls.Add(new Label { Text = "Doble clic en una ROM para excluirla de la selección (o volver a incluirla).", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(16, 6, 3, 0) });
        var candidatesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        candidatesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        candidatesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        candidatesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        candidatesLayout.Controls.Add(modeRow, 0, 0);
        candidatesLayout.Controls.Add(candidatesGrid, 0, 1);

        Configure(systemsGrid);
        systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "system", HeaderText = "Sistema", Width = 160, ReadOnly = true });
        foreach (var (name, header) in new[] { ("total", "ROMs"), ("cand", "Candidatas"), ("pct", "%"), ("size", "Espacio candidatas") })
            systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = 110, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var candidatesTab = new TabPage("Candidatas"); candidatesTab.Controls.Add(candidatesLayout);
        var systemsTab = new TabPage("Resumen por sistema"); systemsTab.Controls.Add(systemsGrid);
        tabs.TabPages.Add(candidatesTab);
        tabs.TabPages.Add(systemsTab);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(tabs, 1, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(exportButton);
        buttons.Controls.Add(closeButton);
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonBar.Controls.Add(buttons, 0, 0);

        var intro = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
            Text = "Análisis de solo lectura sobre todos los sistemas: no se oculta, copia ni borra nada. Marca categorías y supracategorías para ver cuántas ROMs señalarían."
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 4, 8, 4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(split, 0, 1);
        layout.Controls.Add(summary, 0, 2);
        layout.Controls.Add(note, 0, 3);
        layout.Controls.Add(buttonBar, 0, 4);
        Controls.Add(layout);
    }

    private static void Configure(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.BackgroundColor = SystemColors.Window;
    }

    // Recalcula las reglas y qué ROMs cumple cada una (se repite al editar las supracategorías).
    private void BuildRules()
    {
        supra = supraDefs.Select(CurationRule.FromSupra).ToArray();
        ruleById.Clear();
        hits.Clear();
        counts.Clear();
        foreach (var rule in supra.Concat(genreRules)) ruleById[rule.Id] = rule;
        foreach (var row in roms)
            foreach (var rule in ruleById.Values)
                if (rule.Matches(row, out string reason)) hits.Add(new Hit(row, rule, reason));
        foreach (var group in hits.GroupBy(h => h.Rule.Id)) counts[group.Key] = group.Count();
    }

    private void EditSupra()
    {
        var genreCounts = genreRules.ToDictionary(r => r.Title, r => counts.GetValueOrDefault(r.Id), StringComparer.OrdinalIgnoreCase);
        using var editor = new SupraEditorDialog(roms, supraDefs, genreCounts, () => CurationRuleSet.Defaults(genreRules.Select(r => r.Title)));
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        supraDefs = editor.Result;
        SupraStore.Save(supraDefs);
        BuildRules();
        // Lo marcado se conserva para las supracategorías que siguen existiendo; las nuevas empiezan sin marcar.
        checkedIds.RemoveWhere(id => !ruleById.ContainsKey(id));
        if (activeRuleId is not null && !ruleById.ContainsKey(activeRuleId)) activeRuleId = null;
        SaveState();
        trace.Add("curation_supracategories_edited", $"count={supraDefs.Count}");
        FillSupra();
        FillGenres();
        RefreshAll();
    }

    private void FillSupra()
    {
        updating = true;
        try
        {
            supraGrid.Rows.Clear();
            foreach (var rule in supra)
            {
                int index = supraGrid.Rows.Add(checkedIds.Contains(rule.Id), rule.Title, counts.GetValueOrDefault(rule.Id), rule.Description);
                supraGrid.Rows[index].Tag = rule.Id;
                supraGrid.Rows[index].Cells["rule"].ToolTipText = rule.Criteria;
                supraGrid.Rows[index].Cells["desc"].ToolTipText = rule.Criteria;
            }
        }
        finally { updating = false; }
    }

    private void FillGenres()
    {
        string filter = genreFilter.Text.Trim();
        updating = true;
        try
        {
            genresGrid.Rows.Clear();
            foreach (var rule in genreRules.Where(r => filter.Length == 0 || r.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            {
                int index = genresGrid.Rows.Add(checkedIds.Contains(rule.Id), rule.Title, counts.GetValueOrDefault(rule.Id));
                genresGrid.Rows[index].Tag = rule.Id;
            }
        }
        finally { updating = false; }
    }

    private void OnCheckChanged(DataGridView grid, int rowIndex, int columnIndex)
    {
        if (updating || rowIndex < 0 || columnIndex != 0 || grid.Rows[rowIndex].Tag is not string id) return;
        if (grid.Rows[rowIndex].Cells["use"].Value is true) checkedIds.Add(id); else checkedIds.Remove(id);
        SaveState();
        RefreshAll();
    }

    private void OnRuleSelected(DataGridView grid)
    {
        if (updating || grid.CurrentRow?.Tag is not string id) return;
        activeRuleId = id;
        if (showSelected.Checked) RefreshCandidates();
    }

    // Candidatas de todo lo marcado, una sola vez por ROM aunque cumpla más de una regla.
    private static string KeyOf(RomRow row) =>
        $"{row.System}|{(row.GamelistPath.Length > 0 ? PathRules.Relative(row.GamelistPath) : row.RomName.ToLowerInvariant())}";

    private List<(RomRow Row, List<Hit> Hits)> Union(bool includeExcluded = false) =>
        hits.Where(h => checkedIds.Contains(h.Rule.Id) && (includeExcluded || !excluded.Contains(KeyOf(h.Row))))
            .GroupBy(h => $"{h.Row.System}|{h.Row.Path}")
            .Select(g => (g.First().Row, g.ToList()))
            .OrderBy(p => p.Item1.System, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item1.RomName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RefreshAll()
    {
        var union = Union();
        long bytes = union.Sum(p => p.Row.Size);
        double percent = roms.Count > 0 ? union.Count * 100.0 / roms.Count : 0;
        summary.Text = $"Con lo marcado ({checkedIds.Count} reglas): {union.Count:N0} ROMs candidatas de {roms.Count:N0} ({percent:0.0} %) · espacio: {ScreenScraperDialog.FormatBytes(bytes)}";
        int genericSports = roms.Count(r => r.Genre.Equals("Deportes", StringComparison.OrdinalIgnoreCase));
        note.Text = $"Excluidas a mano: {excluded.Count:N0}   ·   {genericSports:N0} ROMs tienen el género «Deportes» sin subgénero; solo las detectan las supracategorías de deportes por palabras del nombre.";
        FillSystems(union);
        RefreshCandidates();
    }

    private void FillSystems(List<(RomRow Row, List<Hit> Hits)> union)
    {
        var candidates = union.GroupBy(p => p.Row.System, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (Count: g.Count(), Bytes: g.Sum(p => p.Row.Size)), StringComparer.OrdinalIgnoreCase);
        systemsGrid.Rows.Clear();
        foreach (var group in roms.GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => candidates.GetValueOrDefault(g.Key).Count).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (count, size) = candidates.GetValueOrDefault(group.Key);
            int total = group.Count();
            systemsGrid.Rows.Add(group.Key, total.ToString("N0"), count.ToString("N0"), (total > 0 ? count * 100.0 / total : 0).ToString("0.0"), size > 0 ? ScreenScraperDialog.FormatBytes(size) : "—");
        }
    }

    private void RefreshCandidates()
    {
        List<(RomRow Row, List<Hit> Hits)> rows;
        if (showSelected.Checked)
            rows = activeRuleId is null ? new() : hits.Where(h => h.Rule.Id == activeRuleId)
                .Select(h => (h.Row, new List<Hit> { h }))
                .OrderBy(p => p.Row.System, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Row.RomName, StringComparer.OrdinalIgnoreCase).ToList();
        else rows = Union(includeExcluded: true);

        // Al refrescar (p. ej. tras excluir una ROM) se conserva la posición de la lista.
        int first = candidatesGrid.FirstDisplayedScrollingRowIndex;
        string? currentKey = candidatesGrid.CurrentRow?.Tag as string;
        strikeFont ??= new Font(candidatesGrid.Font, FontStyle.Strikeout);
        candidatesGrid.SuspendLayout();
        candidatesGrid.Rows.Clear();
        int currentIndex = -1;
        foreach (var (row, rowHits) in rows)
        {
            string key = KeyOf(row);
            bool isExcluded = excluded.Contains(key);
            int index = candidatesGrid.Rows.Add(isExcluded ? "Excluida" : "", row.System, row.RomName, row.GameName, CurationRule.EffectiveGenre(row), ScreenScraperDialog.FormatBytes(row.Size),
                string.Join(", ", rowHits.Select(h => h.Rule.Title)), string.Join(" · ", rowHits.Select(h => h.Reason).Distinct()));
            var gridRow = candidatesGrid.Rows[index];
            gridRow.Tag = key;
            if (isExcluded) { gridRow.DefaultCellStyle.ForeColor = SystemColors.GrayText; gridRow.DefaultCellStyle.Font = strikeFont; }
            if (key == currentKey) currentIndex = index;
        }
        candidatesGrid.ResumeLayout();
        if (currentIndex >= 0) candidatesGrid.CurrentCell = candidatesGrid.Rows[currentIndex].Cells["rom"];
        if (first >= 0 && first < candidatesGrid.Rows.Count) candidatesGrid.FirstDisplayedScrollingRowIndex = first;
    }

    // Doble clic en una candidata: la excluye de la selección o, si ya lo estaba, la vuelve a incluir.
    private void ToggleExcluded(int rowIndex)
    {
        if (rowIndex < 0 || candidatesGrid.Rows[rowIndex].Tag is not string key) return;
        if (!excluded.Remove(key)) excluded.Add(key);
        SaveState();
        RefreshAll();
    }

    // ---- Lo marcado se recuerda entre sesiones

    private sealed record CurationState(List<string> Checked, List<string> Excluded);

    // Admite también el formato anterior (solo la lista de reglas marcadas).
    private static CurationState? LoadState()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            string json = File.ReadAllText(StorePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return new CurationState(JsonSerializer.Deserialize<List<string>>(json) ?? new(), new());
            var state = JsonSerializer.Deserialize<CurationState>(json);
            return state is null ? null : new CurationState(state.Checked ?? new(), state.Excluded ?? new());
        }
        catch { return null; }
    }

    private void SaveState()
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var state = new CurationState(checkedIds.OrderBy(i => i, StringComparer.Ordinal).ToList(), excluded.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
            File.WriteAllText(temp, JsonSerializer.Serialize(state), new UTF8Encoding(false));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch { }
        finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    private void Export()
    {
        var union = Union();
        if (union.Count == 0) { QuietMessage.Show(this, "Exportar candidatas", "No hay candidatas con lo marcado."); return; }
        using var dialog = new SaveFileDialog { Title = "Exportar candidatas", Filter = "CSV (*.csv)|*.csv", DefaultExt = "csv", FileName = $"candidatas_curado_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Sistema;ROM;Nombre;Género;Tamaño (bytes);Reglas;Motivo" };
        foreach (var (row, rowHits) in union)
            lines.Add(string.Join(";", new[] { row.System, row.RomName, row.GameName, CurationRule.EffectiveGenre(row), row.Size.ToString(), string.Join(", ", rowHits.Select(h => h.Rule.Title)), string.Join(" · ", rowHits.Select(h => h.Reason).Distinct()) }.Select(Quote)));
        try
        {
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
            trace.Add("curation_exported", $"rows={union.Count}; file={dialog.FileName}");
        }
        catch (Exception ex) { QuietMessage.Show(this, "Exportar candidatas", ex.Message); }
    }
}
