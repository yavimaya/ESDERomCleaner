namespace ESDERomCleaner;

// Editor de supracategorías: nombre, descripción, géneros que incluye y palabras del nombre. Trabaja sobre una copia; «Guardar cambios»
// se la entrega al llamador sin cerrar la ventana.
internal sealed class SupraEditorDialog : Form
{
    private sealed record GenreItem(string Name, int Count)
    {
        public override string ToString() => $"{Name}  ({Count:N0})";
    }

    private readonly IReadOnlyList<RomRow> roms;
    private readonly List<GenreItem> genres;
    private readonly Func<List<SupraDefinition>> defaults;
    private readonly List<SupraDefinition> defs;
    private readonly Action<List<SupraDefinition>> save;
    private bool loading;
    private bool dirty;

    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button addButton = new() { Text = "Añadir", AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly Button removeButton = new() { Text = "Eliminar", AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly TextBox titleBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox descriptionBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox genreFilter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filtrar géneros…" };
    private readonly CheckedListBox genresList = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox keywordsBox = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = new Font("Consolas", 9F) };
    private readonly Label matchLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), AutoEllipsis = true };
    private readonly Button resetButton = new() { Text = "Restaurar las de partida", AutoSize = true, MinimumSize = new Size(170, 30) };
    private readonly Button saveButton = new() { Text = "Guardar cambios", AutoSize = true, MinimumSize = new Size(140, 30), Enabled = false };
    private readonly Button closeButton = new() { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(100, 30) };
    private readonly GroupBox genresGroup = new() { Text = "Géneros que incluye", Dock = DockStyle.Fill };
    private readonly Panel rightPanel = new() { Dock = DockStyle.Fill };

    public SupraEditorDialog(IReadOnlyList<RomRow> roms, IEnumerable<SupraDefinition> current, IReadOnlyDictionary<string, int> genreCounts, Func<List<SupraDefinition>> defaults, Action<List<SupraDefinition>> save)
    {
        this.roms = roms;
        this.defaults = defaults;
        this.save = save;
        defs = current.Select(d => d.Clone()).ToList();
        genres = genreCounts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new GenreItem(p.Key, p.Value)).ToList();

        Text = "Editar supracategorías de curado";
        Width = 1000; Height = 660; MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();

        foreach (var def in defs) list.Items.Add(def.Title);
        list.SelectedIndexChanged += (_, _) => LoadSelected();
        addButton.Click += (_, _) => AddNew();
        removeButton.Click += (_, _) => RemoveSelected();
        resetButton.Click += (_, _) => Reset();
        titleBox.TextChanged += (_, _) =>
        {
            if (loading || Current is not { } def) return;
            def.Title = titleBox.Text;
            MarkDirty();
            loading = true;
            try { list.Items[list.SelectedIndex] = def.Title.Length > 0 ? def.Title : "(sin nombre)"; }
            finally { loading = false; }
        };
        descriptionBox.TextChanged += (_, _) => { if (!loading && Current is { } def) { def.Description = descriptionBox.Text; MarkDirty(); } };
        keywordsBox.TextChanged += (_, _) =>
        {
            if (loading || Current is not { } def) return;
            def.Keywords = keywordsBox.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            MarkDirty();
            UpdateMatch();
        };
        genreFilter.TextChanged += (_, _) => RefillGenres();
        genresList.ItemCheck += (_, e) =>
        {
            if (loading || Current is not { } def) return;
            string name = ((GenreItem)genresList.Items[e.Index]).Name;
            if (e.NewValue == CheckState.Checked) { if (!def.Genres.Contains(name, StringComparer.OrdinalIgnoreCase)) def.Genres.Add(name); }
            else def.Genres.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
            MarkDirty();
            // El recuento se actualiza cuando WinForms ya ha aplicado el cambio.
            BeginInvoke(UpdateMatch);
        };
        saveButton.Click += (_, _) => SaveChanges();
        closeButton.Click += (_, _) => Close();
        // Si quedan cambios sin guardar, se pide confirmación antes de cerrar.
        FormClosing += (_, e) =>
        {
            if (dirty && !QuietMessage.Confirm(this, "Editar supracategorías de curado", "Hay cambios sin guardar. ¿Cerrar y descartarlos?", "Descartar"))
                e.Cancel = true;
        };
        if (list.Items.Count > 0) list.SelectedIndex = 0; else LoadSelected();
    }

    private SupraDefinition? Current => list.SelectedIndex >= 0 && list.SelectedIndex < defs.Count ? defs[list.SelectedIndex] : null;

    private void BuildLayout()
    {
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        listButtons.Controls.Add(addButton);
        listButtons.Controls.Add(removeButton);
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        listLayout.Controls.Add(list, 0, 0);
        listLayout.Controls.Add(listButtons, 0, 1);
        var listGroup = new GroupBox { Text = "Supracategorías", Dock = DockStyle.Fill };
        listGroup.Controls.Add(listLayout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.Controls.Add(new Label { Text = "Nombre:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        header.Controls.Add(titleBox, 1, 0);
        header.Controls.Add(new Label { Text = "Descripción:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        header.Controls.Add(descriptionBox, 1, 1);

        var genresLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        genresLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        genresLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        genresLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        genresLayout.Controls.Add(genreFilter, 0, 0);
        genresLayout.Controls.Add(genresList, 0, 1);
        genresGroup.Controls.Add(genresLayout);

        var hint = new Label
        {
            Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText,
            Text = "Una palabra por línea. Se busca dentro del nombre del juego, sin distinguir mayúsculas. Puedes usar expresiones regulares " +
                   "(por ejemplo \\bnfl\\b para la palabra completa); si una línea no es una expresión válida se toma como texto literal."
        };
        var keywordsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        keywordsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keywordsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        keywordsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        keywordsLayout.Controls.Add(keywordsBox, 0, 0);
        keywordsLayout.Controls.Add(hint, 0, 1);
        var keywordsGroup = new GroupBox { Text = "Palabras del nombre", Dock = DockStyle.Fill };
        keywordsGroup.Controls.Add(keywordsLayout);

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        middle.Controls.Add(genresGroup, 0, 0);
        middle.Controls.Add(keywordsGroup, 1, 0);

        var rightLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        rightLayout.Controls.Add(header, 0, 0);
        rightLayout.Controls.Add(middle, 0, 1);
        rightLayout.Controls.Add(matchLabel, 0, 2);
        rightPanel.Controls.Add(rightLayout);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.Controls.Add(listGroup, 0, 0);
        split.Controls.Add(rightPanel, 1, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(resetButton);
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonBar.Controls.Add(buttons, 0, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8, 6, 8, 4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(split, 0, 0);
        layout.Controls.Add(buttonBar, 0, 1);
        Controls.Add(layout);
    }

    private void LoadSelected()
    {
        if (loading) return;
        loading = true;
        try
        {
            var def = Current;
            rightPanel.Enabled = def is not null;
            removeButton.Enabled = def is not null;
            titleBox.Text = def?.Title ?? "";
            descriptionBox.Text = def?.Description ?? "";
            keywordsBox.Text = def is null ? "" : string.Join(Environment.NewLine, def.Keywords);
        }
        finally { loading = false; }
        RefillGenres();
        UpdateMatch();
    }

    private void RefillGenres()
    {
        string filter = genreFilter.Text.Trim();
        var def = Current;
        loading = true;
        try
        {
            genresList.BeginUpdate();
            genresList.Items.Clear();
            foreach (var genre in genres.Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
                genresList.Items.Add(genre, def is not null && def.Genres.Contains(genre.Name, StringComparer.OrdinalIgnoreCase));
            genresList.EndUpdate();
        }
        finally { loading = false; }
    }

    // ROMs que cumpliría la supracategoría con lo que hay escrito ahora, y aviso si alguna línea no es una expresión válida.
    private void UpdateMatch()
    {
        // El título del grupo indica cuántas categorías (géneros) tiene marcadas la supracategoría seleccionada.
        genresGroup.Text = Current is { } selected
            ? $"Géneros que incluye ({selected.Genres.Count} {(selected.Genres.Count == 1 ? "seleccionado" : "seleccionados")})"
            : "Géneros que incluye";
        if (Current is not { } def) { matchLabel.Text = ""; return; }
        var rule = CurationRule.FromSupra(def);
        int count = roms.Count(r => rule.Matches(r, out _));
        var invalid = def.Keywords.Where(k => !CurationRule.IsValidRegex(k)).ToList();
        matchLabel.Text = $"ROMs que coinciden con esta supracategoría: {count:N0} de {roms.Count:N0}" +
                          (invalid.Count > 0 ? $"   ·   {invalid.Count} línea(s) no son expresiones válidas y se toman como texto" : "");
    }

    private void MarkDirty()
    {
        dirty = true;
        saveButton.Enabled = true;
    }

    // Entrega los cambios al llamador y deja la ventana abierta; «Guardar cambios» vuelve a desactivarse hasta la próxima edición.
    private void SaveChanges()
    {
        if (defs.Any(d => string.IsNullOrWhiteSpace(d.Title)))
        {
            QuietMessage.Show(this, "Editar supracategorías de curado", "Todas las supracategorías necesitan un nombre.");
            return;
        }
        save(defs.Select(d => d.Clone()).ToList());
        dirty = false;
        saveButton.Enabled = false;
    }

    private void AddNew()
    {
        var def = new SupraDefinition { Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Title = "Nueva supracategoría" };
        defs.Add(def);
        MarkDirty();
        list.Items.Add(def.Title);
        list.SelectedIndex = list.Items.Count - 1;
        titleBox.Focus();
        titleBox.SelectAll();
    }

    private void RemoveSelected()
    {
        int index = list.SelectedIndex;
        if (index < 0) return;
        if (!QuietMessage.Confirm(this, "Eliminar supracategoría", $"¿Eliminar «{defs[index].Title}»?", "Eliminar")) return;
        defs.RemoveAt(index);
        MarkDirty();
        loading = true;
        try { list.Items.RemoveAt(index); }
        finally { loading = false; }
        if (list.Items.Count > 0) list.SelectedIndex = Math.Min(index, list.Items.Count - 1); else LoadSelected();
    }

    private void Reset()
    {
        if (!QuietMessage.Confirm(this, "Restaurar supracategorías", "Se perderán tus cambios y se volverá a las supracategorías de partida. ¿Continuar?", "Restaurar")) return;
        defs.Clear();
        defs.AddRange(defaults());
        MarkDirty();
        loading = true;
        try
        {
            list.Items.Clear();
            foreach (var def in defs) list.Items.Add(def.Title);
        }
        finally { loading = false; }
        if (list.Items.Count > 0) list.SelectedIndex = 0; else LoadSelected();
    }
}
