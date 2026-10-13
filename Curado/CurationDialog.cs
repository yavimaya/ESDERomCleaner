using System.Text;
using System.Text.Json;

namespace ESDERomCleaner;

// Análisis de curado: aplica a toda la colección las categorías (géneros) y supracategorías marcadas y resume cuántas ROMs
// señalarían, para valorar si merece la pena. Es de solo lectura: no oculta, copia ni borra nada. La elección de sistemas se hace
// en la ventana principal.
internal sealed class CurationDialog : LocalizedForm
{
    private sealed record Hit(RomRow Row, CurationRule Rule, string Reason);

    private readonly List<RomRow> roms;
    private readonly bool gamelistBackups;
    // Es true si se han excluido ROMs de la colección (se han cambiado gamelists): la ventana principal debe volver a analizar.
    public bool CollectionChanged { get; private set; }
    // Las ROMs que se han excluido de la colección desde este formulario: la ventana principal las quita de su lista al cerrarlo.
    public List<RomRow> ExcludedRows { get; } = new();
    private readonly TraceLog trace;
    // ROMs que el usuario ha decidido eliminar al cerrar el formulario; las borra la ventana principal con su borrado seguro.
    public List<RomRow> RowsForDeletion { get; } = new();

    // Borrado completo (plan, aviso con las cifras exactas, ejecución y resultado) hecho por la ventana principal, pero mostrado sobre este formulario.
    // Devuelve las rutas de las ROMs borradas (null si se canceló). Sin él, las filas se entregan a la ventana principal al cerrar.
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<RomRow[], IWin32Window, Action<string>, Task<HashSet<string>?>>? DeleteHandler { get; set; }
    private bool deleting;
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
    // Columnas de la lista de candidatas que el usuario ha ocultado (por defecto, el nombre del archivo).
    private readonly HashSet<string> hiddenColumns = new();
    private readonly ContextMenuStrip columnsMenu = new() { ShowImageMargin = false, ShowCheckMargin = true };
    private readonly Button columnsButton = new() { Text = "Columnas ▾", AutoSize = true, Margin = new Padding(16, 1, 3, 0) };
    private static readonly (string Name, string Title)[] ColumnChoices =
        [("state", "Estado"), ("system", "Sistema"), ("rom", "Nombre del archivo"), ("game", "Nombre"), ("genre", "Categoría"), ("rules", "Regla(s)"), ("reason", "Motivo"), ("size", "Tamaño")];
    private List<CurationProfile> profiles = new();
    private string baseTitle = "";
    private bool updating;

    private readonly DataGridView supraGrid = new();
    private readonly DataGridView genresGrid = new();
    private readonly TextBox genreFilter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filtrar categorías…" };
    private readonly DataGridView candidatesGrid = new();
    private readonly DataGridView systemsGrid = new();
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), AutoEllipsis = true };
    private readonly Label note = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly RomLanguageIndex languageIndex = RomLanguageIndex.Load();
    private readonly RaIndex raIndex = RaIndex.Load();
    private CurationLanguageSettings languageSettings = CurationLanguageSettings.Load();
    private int languageKnown;
    private readonly Button languagesButton = new() { Text = "Idiomas…", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button deleteButton = new() { Text = "Eliminar candidatas…", AutoSize = true, MinimumSize = new Size(190, 30), ForeColor = Color.Firebrick, Enabled = false };
    private readonly Button editSupraButton = new() { Text = "Editar supracategorías…", AutoSize = true, MinimumSize = new Size(170, 28) };
    private readonly ComboBox profileBox = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, MaxDropDownItems = 14 };
    private readonly Button loadProfileButton = new() { Text = "Cargar", AutoSize = true, MinimumSize = new Size(80, 28), Enabled = false };
    private readonly Button saveProfileButton = new() { Text = "Guardar perfil…", AutoSize = true, MinimumSize = new Size(120, 28) };
    private readonly Button deleteProfileButton = new() { Text = "Eliminar", AutoSize = true, MinimumSize = new Size(80, 28), Enabled = false };
    private readonly Button excludeButton = new() { Text = "Excluir de la colección…", AutoSize = true, MinimumSize = new Size(170, 30), Enabled = false, ForeColor = Color.FromArgb(214, 100, 0) };
    private readonly Button exportButton = new() { Text = "Exportar candidatas…", AutoSize = true, MinimumSize = new Size(150, 30) };
    private readonly Button closeButton = new() { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(100, 30), DialogResult = DialogResult.Cancel };

    private static string StorePath => Path.Combine(AppPaths.Data, "curation.json");

    public CurationDialog(IReadOnlyList<RomRow> allRoms, TraceLog trace, bool gamelistBackups = false)
    {
        this.gamelistBackups = gamelistBackups;
        roms = allRoms.Where(r => r.Status == RomStatus.Ok && r.Path.Length > 0).ToList();
        this.trace = trace;
        Text = baseTitle = "Reglas de curado — análisis";
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

        if (state?.HiddenColumns is { } hiddenSaved) foreach (string column in hiddenSaved) hiddenColumns.Add(column);
        else hiddenColumns.Add("rom");

        BuildLayout();
        FillSupra();
        FillGenres();

        foreach (var grid in new[] { supraGrid, genresGrid })
        {
            var current = grid;
            current.CurrentCellDirtyStateChanged += (_, _) => { if (current.IsCurrentCellDirty) current.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            current.CellValueChanged += (_, e) => OnCheckChanged(current, e.RowIndex, e.ColumnIndex);
            // Doble clic en cualquier parte de la fila (menos en su casilla, que ya responde a cada clic) marca o desmarca la regla.
            current.CellDoubleClick += (_, e) => ToggleRow(current, e.RowIndex, e.ColumnIndex);
        }
        genreFilter.TextChanged += (_, _) => FillGenres();
        exportButton.Click += (_, _) => Export();
        editSupraButton.Click += (_, _) => EditSupra();
        languagesButton.Click += (_, _) => EditLanguages();
        deleteButton.Click += (_, _) => RequestDeletion();
        excludeButton.Click += (_, _) => ExcludeSelected();
        // El botón muestra cuántas candidatas hay seleccionadas y solo está activo si hay alguna.
        candidatesGrid.SelectionChanged += (_, _) =>
        {
            int count = candidatesGrid.SelectedRows.Count;
            excludeButton.Enabled = count > 0;
            excludeButton.Text = count > 0 ? $"Excluir de la colección ({count:N0})…" : "Excluir de la colección…";
        };
        // Menú contextual de las candidatas: excluirlas de la colección (contador y multiscrapeo de ES-DE) y sacarlas de esta lista.
        var rowMenu = new ContextMenuStrip { ShowImageMargin = false };
        var excludeItem = new ToolStripMenuItem("Excluir de la colección (contador y multiscrapeo)…");
        excludeItem.Click += (_, _) => ExcludeSelected();
        rowMenu.Items.Add(excludeItem);
        var deleteItem = new ToolStripMenuItem("Eliminar (ROM, accesorios y media)…") { ForeColor = Color.Firebrick };
        deleteItem.Click += (_, _) => DeleteSelected();
        rowMenu.Items.Add(deleteItem);
        rowMenu.Opening += (_, e) => { if (candidatesGrid.SelectedRows.Count == 0) e.Cancel = true; };
        candidatesGrid.ContextMenuStrip = rowMenu;
        candidatesGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !candidatesGrid.Rows[e.RowIndex].Selected)
            { candidatesGrid.ClearSelection(); candidatesGrid.Rows[e.RowIndex].Selected = true; }
        };
        saveProfileButton.Click += (_, _) => SaveProfile();
        loadProfileButton.Click += (_, _) => LoadProfile();
        deleteProfileButton.Click += (_, _) => DeleteProfile();
        profileBox.TextChanged += (_, _) => UpdateProfileButtons();
        profileBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && loadProfileButton.Enabled) { e.SuppressKeyPress = true; LoadProfile(); } };
        profiles = CurationProfileStore.Load();
        RefillProfiles();
        candidatesGrid.CellDoubleClick += (_, e) => ToggleExcluded(e.RowIndex);
        FormClosed += (_, _) => strikeFont?.Dispose();
        Shown += (_, _) => RefreshAll();
    }

    private void BuildLayout()
    {
        Configure(supraGrid);
        supraGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 30 });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rule", HeaderText = "Supracategoría", Width = 190, ReadOnly = true });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "ROMs", Width = 55, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });
        supraGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Qué incluye", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        var supraLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        supraLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        supraLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        supraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        supraLayout.Controls.Add(supraGrid, 0, 0);
        var supraButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false, Margin = Padding.Empty };
        supraButtons.Controls.Add(editSupraButton);
        supraButtons.Controls.Add(languagesButton);
        supraLayout.Controls.Add(supraButtons, 0, 1);
        var supraGroup = new GroupBox { Text = "Supracategorías", Dock = DockStyle.Fill };
        supraGroup.Controls.Add(supraLayout);

        Configure(genresGrid);
        genresGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 30 });
        genresGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rule", HeaderText = "Categoría", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        genresGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "ROMs", Width = 60, ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleCenter } } });
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
        candidatesGrid.MultiSelect = true; // se pueden seleccionar varias candidatas para excluirlas de una vez
        foreach (var (name, header, width) in new[] { ("state", "Estado", 66), ("system", "Sistema", 90), ("rom", "Nombre del archivo", 220), ("game", "Nombre", 190), ("genre", "Categoría", 140), ("rules", "Regla(s)", 150), ("reason", "Motivo", 0), ("size", "Tamaño", 80) })
            candidatesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width > 0 ? width : 100, ReadOnly = true });
        candidatesGrid.Columns["reason"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        candidatesGrid.Columns["size"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        BuildColumnsMenu();
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        modeRow.Controls.Add(new Label { Text = "Candidatas de todo lo marcado (sin repetir).", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 0) });
        modeRow.Controls.Add(new Label { Text = "Doble clic en una ROM para excluirla de la selección (o volver a incluirla).", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(16, 6, 3, 0) });
        modeRow.Controls.Add(columnsButton);
        columnsButton.Click += (_, _) => columnsMenu.Show(columnsButton, new Point(0, columnsButton.Height));
        candidatesGrid.ColumnHeaderMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) columnsMenu.Show(Cursor.Position); };
        var candidatesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        candidatesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        candidatesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        candidatesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        candidatesLayout.Controls.Add(modeRow, 0, 0);
        candidatesLayout.Controls.Add(candidatesGrid, 0, 1);

        Configure(systemsGrid);
        systemsGrid.Columns.Add(SystemIconColumn.Create(30));
        systemsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "system", HeaderText = "Sistema", Width = 160, ReadOnly = true });
        foreach (var (name, header) in new[] { ("total", "ROMs"), ("cand", "Candidatas"), ("pct", "%"), ("size", "Espacio candidatas") })
            systemsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = 110, ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = name == "total" ? DataGridViewContentAlignment.MiddleCenter : DataGridViewContentAlignment.MiddleRight },
                HeaderCell = { Style = { Alignment = name == "total" ? DataGridViewContentAlignment.MiddleCenter : DataGridViewContentAlignment.NotSet } }
            });

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
        buttons.Controls.Add(excludeButton);
        buttons.Controls.Add(deleteButton);
        buttons.Controls.Add(closeButton);
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonBar.Controls.Add(buttons, 0, 0);

        // Barra de perfiles: instantáneas con nombre de lo marcado, las exclusiones y las supracategorías, que se pueden buscar y recuperar.
        var intro = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        intro.Controls.Add(new Label { Text = "Perfil:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) });
        profileBox.Margin = new Padding(3, 5, 6, 0);
        intro.Controls.Add(profileBox);
        intro.Controls.Add(loadProfileButton);
        intro.Controls.Add(saveProfileButton);
        intro.Controls.Add(deleteProfileButton);
        intro.Controls.Add(new Label
        {
            Text = "Análisis de solo lectura sobre todos los sistemas: no se oculta, copia ni borra nada.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(18, 8, 3, 0)
        });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 4, 8, 4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
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
        supra = supraDefs.Select(CurationRule.FromSupra).Append(BuildLanguageRule()).Append(BuildAchievementsRule()).ToArray();
        languageKnown = roms.Count(r => LanguageResolver.Resolve(r, languageIndex, languageSettings.UseFilename).Languages.Count > 0);
        ruleById.Clear();
        hits.Clear();
        counts.Clear();
        foreach (var rule in supra.Concat(genreRules)) ruleById[rule.Id] = rule;
        foreach (var row in roms)
            foreach (var rule in ruleById.Values)
                if (rule.Matches(row, out string reason)) hits.Add(new Hit(row, rule, reason));
        foreach (var group in hits.GroupBy(h => h.Rule.Id)) counts[group.Key] = group.Count();
    }

    // ---- Perfiles

    private CurationProfile? FindProfile(string name) =>
        profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void RefillProfiles()
    {
        string text = profileBox.Text;
        profileBox.Items.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) profileBox.Items.Add(profile.Name);
        profileBox.Text = text;
        UpdateProfileButtons();
    }

    private void UpdateProfileButtons()
    {
        bool exists = FindProfile(profileBox.Text) is not null;
        loadProfileButton.Enabled = exists;
        deleteProfileButton.Enabled = exists;
    }

    private void SaveProfile()
    {
        string? name = QuietMessage.Prompt(this, "Guardar perfil de curado", "Nombre del perfil (si ya existe, se sustituye):", profileBox.Text.Trim(), "Guardar");
        if (name is null) return;
        var existing = FindProfile(name);
        if (existing is not null && !QuietMessage.Confirm(this, "Guardar perfil de curado", $"Ya existe un perfil llamado «{existing.Name}». ¿Quieres sustituirlo?", "Sustituir")) return;

        var profile = new CurationProfile
        {
            Name = existing?.Name ?? name, SavedAt = DateTime.Now,
            Checked = checkedIds.OrderBy(i => i, StringComparer.Ordinal).ToList(),
            Excluded = excluded.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            Supra = supraDefs.Select(d => d.Clone()).ToList()
        };
        if (existing is not null) profiles.Remove(existing);
        profiles.Add(profile);
        if (!CurationProfileStore.Save(profiles)) { QuietMessage.Show(this, "Guardar perfil de curado", "No se pudo guardar el perfil en disco."); return; }
        trace.Add("curation_profile_saved", $"name={profile.Name}; rules={profile.Checked.Count}; excluded={profile.Excluded.Count}");
        profileBox.Text = profile.Name;
        RefillProfiles();
        Text = $"{baseTitle} — perfil: {profile.Name}";
    }

    private void LoadProfile()
    {
        var profile = FindProfile(profileBox.Text);
        if (profile is null) { QuietMessage.Show(this, "Cargar perfil de curado", "No hay ningún perfil con ese nombre."); return; }
        if (!QuietMessage.Confirm(this, "Cargar perfil de curado",
                $"Se sustituirán las selecciones, las exclusiones y las supracategorías actuales por las del perfil «{profile.Name}» (guardado el {profile.SavedAt:dd/MM/yyyy HH:mm}).\r\n\r\n¿Continuar?", "Cargar")) return;

        if (profile.Supra.Count > 0)
        {
            supraDefs = profile.Supra.Select(d => d.Clone()).ToList();
            SupraStore.Save(supraDefs);
        }
        BuildRules();
        checkedIds.Clear();
        foreach (string id in profile.Checked.Where(ruleById.ContainsKey)) checkedIds.Add(id);
        excluded.Clear();
        foreach (string key in profile.Excluded) excluded.Add(key);
        SaveState();
        trace.Add("curation_profile_loaded", $"name={profile.Name}; rules={checkedIds.Count}; excluded={excluded.Count}");
        FillSupra();
        FillGenres();
        RefreshAll();
        Text = $"{baseTitle} — perfil: {profile.Name}";
    }

    private void DeleteProfile()
    {
        var profile = FindProfile(profileBox.Text);
        if (profile is null) return;
        if (!QuietMessage.Confirm(this, "Eliminar perfil de curado", $"¿Eliminar el perfil «{profile.Name}»?", "Eliminar")) return;
        profiles.Remove(profile);
        CurationProfileStore.Save(profiles);
        trace.Add("curation_profile_deleted", $"name={profile.Name}");
        profileBox.Text = "";
        RefillProfiles();
    }

    // Regla integrada de idiomas: ROMs cuyos idiomas conocidos no incluyen ninguno de los que entiende el usuario.
    private CurationRule BuildLanguageRule()
    {
        string understood = languageSettings.Understood.Count > 0 ? string.Join(", ", languageSettings.Understood.Select(LanguageCatalog.NameOf)) : "ninguno";
        string description = $"no están en ninguno de: {understood}" + (languageSettings.OnlyTextGenres ? " (solo categorías con texto)" : "");
        return new CurationRule("lang-unreadable", "Idiomas no entendibles", description, row =>
        {
            var (languages, source) = LanguageResolver.Resolve(row, languageIndex, languageSettings.UseFilename);
            if (languages.Count == 0) return null;
            if (languages.Any(l => languageSettings.Understood.Contains(l, StringComparer.OrdinalIgnoreCase))) return null;
            if (languageSettings.OnlyTextGenres && !CurationLanguageSettings.IsTextGenre(CurationRule.EffectiveGenre(row))) return null;
            return $"idiomas: {string.Join(", ", languages.Select(LanguageCatalog.NameOf))} ({source})";
        });
    }

    // Regla integrada de RetroAchievements: ROMs de sistemas ya cotejados que no tienen ningún juego con logros.
    private CurationRule BuildAchievementsRule()
    {
        int checkedSystems = roms.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).Count(s => raIndex.SystemChecked(s) && RaConsoles.Of(s) is not null);
        string description = checkedSystems > 0 ? $"sin logros en RetroAchievements (sistemas cotejados: {checkedSystems})"
            : "sin logros en RetroAchievements (antes hay que cotejar: Scrapeo › Buscar logros de RetroAchievements…)";
        return new CurationRule("ra-none", "Sin logros (RetroAchievements)", description, row =>
            RaConsoles.Of(row.System) is not null && raIndex.SystemChecked(row.System) && raIndex.Match(row) is null ? "sin logros en RetroAchievements" : null);
    }

    private void EditLanguages()
    {
        int fromScreenScraper = roms.Count(r => languageIndex.TryGet(RomKey.Of(r), out var info) && info.Languages.Count > 0);
        string coverage = $"Datos de idioma: {fromScreenScraper:N0} ROMs con idiomas de ScreenScraper; {languageKnown:N0} de {roms.Count:N0} con algún idioma conocido. " +
                          "Para traer más idiomas: Scrapeo › Descarga masiva de medios › «Idiomas (para el curado)».";
        using var dialog = new CurationLanguagesDialog(languageSettings, coverage);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        languageSettings = dialog.Result;
        languageSettings.Save();
        trace.Add("curation_languages_changed", $"understood={string.Join(",", languageSettings.Understood)}; text_only={languageSettings.OnlyTextGenres}; filename={languageSettings.UseFilename}");
        BuildRules();
        FillSupra();
        RefreshAll();
    }

    // Eliminar las candidatas: aquí solo se confirma el paso; el borrado real lo hace la ventana principal con su plan, su resumen y su
    // confirmación, que muestran qué se va a borrar antes de tocar nada.
    private void RequestDeletion() => RequestDeletionOf(Union().Select(p => p.Row).ToList());

    // Eliminar solo las candidatas seleccionadas en la lista (menú contextual). Las excluidas a mano de la selección están protegidas y no se tocan.
    private void DeleteSelected()
    {
        var keys = candidatesGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as string).Where(k => k is not null).Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0 && candidatesGrid.CurrentRow?.Tag is string current) keys.Add(current);
        if (keys.Count == 0)
        {
            QuietMessage.Show(this, "Eliminar candidatas", "Selecciona primero las ROMs candidatas que quieras eliminar.");
            return;
        }
        var rows = roms.Where(r => keys.Contains(KeyOf(r)) && !excluded.Contains(KeyOf(r))).ToList();
        if (rows.Count == 0)
        {
            QuietMessage.Show(this, "Eliminar candidatas", "Las ROMs seleccionadas están excluidas a mano de la selección (tachadas), así que no se tocan.");
            return;
        }
        RequestDeletionOf(rows);
    }

    private void RequestDeletionOf(IReadOnlyList<RomRow> rowsToDelete)
    {
        if (rowsToDelete.Count == 0) return;
        if (DeleteHandler is not null) { _ = DeleteWithHandlerAsync(rowsToDelete); return; }
        long bytes = rowsToDelete.Sum(r => r.Size);
        // Mismo aviso que el borrado de la ventana principal. Aquí solo se conocen las ROMs (los accesorios, la media y los gamelists se calculan en el plan
        // posterior, que vuelve a pedir confirmación con los números exactos), y lo que aún no se sabe se muestra como «—».
        var data = new DeleteConfirmData(rowsToDelete.Count, ScreenScraperDialog.FormatBytes(bytes), -1, "—", -1, "—", -1, ScreenScraperDialog.FormatBytes(bytes) + "+", gamelistBackups);
        using (var confirm = new DeleteConfirmDialog(data))
            if (confirm.ShowDialog(this) != DialogResult.OK) return;
        RowsForDeletion.Clear();
        RowsForDeletion.AddRange(rowsToDelete);
        trace.Add("curation_deletion_requested", $"roms={RowsForDeletion.Count}; bytes={bytes}");
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task DeleteWithHandlerAsync(IReadOnlyList<RomRow> rowsToDelete)
    {
        if (deleting || DeleteHandler is null) return;
        deleting = true;
        deleteButton.Enabled = false;
        string previousNote = note.Text;
        try
        {
            trace.Add("curation_deletion_requested", $"roms={rowsToDelete.Count}; bytes={rowsToDelete.Sum(r => r.Size)}");
            var deleted = await DeleteHandler(rowsToDelete.ToArray(), this, text => { if (!IsDisposed) note.Text = text; });
            if (IsDisposed) return;
            if (deleted is { Count: > 0 })
            {
                roms.RemoveAll(r => deleted.Contains(r.Path));
                foreach (var row in rowsToDelete) excluded.Remove(KeyOf(row));
                CollectionChanged = true;
                BuildRules();
                FillSupra();
                FillGenres();
                RefreshAll();
                SaveState();
            }
            else note.Text = previousNote;
        }
        finally
        {
            deleting = false;
            if (!IsDisposed) { deleteButton.Enabled = Union().Count > 0; }
        }
    }

    // Excluye de la colección las candidatas seleccionadas: nogamecount y nomultiscrape en true en su gamelist.xml (ES-DE no las cuenta ni las multiscrapea). No se
    // marca nada ni se borran archivos: solo desaparecen de esta lista, de las reglas y, al cerrar, de la ventana principal.
    private void ExcludeSelected()
    {
        var keys = candidatesGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as string).Where(k => k is not null).Select(k => k!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0 && candidatesGrid.CurrentRow?.Tag is string current) keys.Add(current);
        if (keys.Count == 0)
        {
            QuietMessage.Show(this, "Excluir de la colección", "Selecciona primero las ROMs candidatas que quieras excluir.");
            return;
        }
        var rows = roms.Where(r => keys.Contains(KeyOf(r)) && r.Gamelist.Length > 0 && r.GamelistPath.Length > 0).ToList();
        if (rows.Count == 0)
        {
            QuietMessage.Show(this, "Excluir de la colección", "Las ROMs seleccionadas no tienen entrada en ningún gamelist.xml, así que no se pueden excluir.");
            return;
        }
        string text = $"Se van a excluir {rows.Count:N0} ROMs del contador y del multiscrapeo de ES-DE (nogamecount y nomultiscrape en true en su gamelist.xml).\r\n\r\n"
            + "Desaparecerán de esta lista y de la ventana principal. No se toca ningún archivo de ROM ni de media y se puede revertir editando el gamelist.xml.";
        if (!QuietMessage.Confirm(this, "Excluir de la colección", text, "Excluir")) return;

        var flags = new Dictionary<string, string> { ["nogamecount"] = "true", ["nomultiscrape"] = "true" };
        var errors = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UseWaitCursor = true;
        try
        {
            foreach (var group in rows.GroupBy(r => r.Gamelist, StringComparer.OrdinalIgnoreCase))
            {
                var relatives = group.Select(r => PathRules.Relative(r.GamelistPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                try
                {
                    int updated = GamelistMetadataWriter.SetFields(group.Key, relatives, flags, gamelistBackups, errors);
                    if (updated == relatives.Count) foreach (var row in group) done.Add(KeyOf(row)); // las que no se pudieron marcar se quedan en la lista
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(Path.GetDirectoryName(group.Key) ?? "")}: {ex.Message}"); }
            }
        }
        finally { UseWaitCursor = false; }

        if (done.Count > 0)
        {
            ExcludedRows.AddRange(roms.Where(r => done.Contains(KeyOf(r))));
            roms.RemoveAll(r => done.Contains(KeyOf(r)));
            foreach (string key in done) excluded.Remove(key);
            CollectionChanged = true;
            BuildRules();
            FillSupra();
            FillGenres();
            RefreshAll();
            SaveState();
        }
        trace.Add("curation_excluded_from_collection", $"roms={done.Count}; errors={errors.Count}");
        if (errors.Count > 0) QuietMessage.Show(this, "Excluir de la colección", string.Join("\r\n", errors.Take(10)));
    }
    private void EditSupra()
    {
        var genreCounts = genreRules.ToDictionary(r => r.Title, r => counts.GetValueOrDefault(r.Id), StringComparer.OrdinalIgnoreCase);
        using var editor = new SupraEditorDialog(roms, supraDefs, genreCounts, () => CurationRuleSet.Defaults(genreRules.Select(r => r.Title)), OnSupraSaved);
        editor.ShowDialog(this);
    }

    // «Guardar cambios» del editor: se guardan, se recalculan las reglas y el análisis se actualiza detrás, con el editor aún abierto.
    private void OnSupraSaved(List<SupraDefinition> definitions)
    {
        supraDefs = definitions;
        SupraStore.Save(supraDefs);
        BuildRules();
        // Lo marcado se conserva para las supracategorías que siguen existiendo; las nuevas empiezan sin marcar.
        checkedIds.RemoveWhere(id => !ruleById.ContainsKey(id));
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

    // Menú para elegir qué columnas de las candidatas se ven; se queda abierto mientras se marcan varias.
    private void BuildColumnsMenu()
    {
        foreach (var (name, title) in ColumnChoices)
        {
            var item = new ToolStripMenuItem(title) { CheckOnClick = true, Checked = !hiddenColumns.Contains(name), Tag = name };
            item.CheckedChanged += (_, _) => OnColumnToggled(item);
            columnsMenu.Items.Add(item);
        }
        columnsMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        ApplyColumnVisibility();
    }

    private void OnColumnToggled(ToolStripMenuItem item)
    {
        string name = (string)item.Tag!;
        if (item.Checked) hiddenColumns.Remove(name); else hiddenColumns.Add(name);
        // Siempre queda al menos una columna visible.
        if (hiddenColumns.Count == ColumnChoices.Length) { item.Checked = true; return; }
        ApplyColumnVisibility();
        SaveState();
    }

    private void ApplyColumnVisibility()
    {
        foreach (var (name, _) in ColumnChoices) candidatesGrid.Columns[name]!.Visible = !hiddenColumns.Contains(name);
    }

    private void ToggleRow(DataGridView grid, int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex == 0) return;
        grid.Rows[rowIndex].Cells["use"].Value = grid.Rows[rowIndex].Cells["use"].Value is not true;
    }

    // Candidatas de todo lo marcado, una sola vez por ROM aunque cumpla más de una regla.
    private static string KeyOf(RomRow row) =>
        RomKey.Of(row);

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
        deleteButton.Text = $"Eliminar {union.Count:N0} candidatas…";
        deleteButton.Enabled = union.Count > 0;
        note.Text = $"Excluidas a mano: {excluded.Count:N0}   ·   Idioma conocido: {languageKnown:N0} de {roms.Count:N0}   ·   {genericSports:N0} ROMs tienen la categoría «Deportes» sin subgénero; solo las detectan las supracategorías de deportes por palabras del nombre.";
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
            systemsGrid.Rows.Add(SystemIconColumn.Icon(group.Key, 18), group.Key, total.ToString("N0"), count.ToString("N0"), (total > 0 ? count * 100.0 / total : 0).ToString("0.0"), size > 0 ? ScreenScraperDialog.FormatBytes(size) : "—");
        }
    }

    private void RefreshCandidates()
    {
        var rows = Union(includeExcluded: true);

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
            int index = candidatesGrid.Rows.Add(isExcluded ? "Excluida" : "", row.System, row.RomName, row.GameName, CurationRule.EffectiveGenre(row),
                string.Join(", ", rowHits.Select(h => h.Rule.Title)), string.Join(" · ", rowHits.Select(h => h.Reason).Distinct()), ScreenScraperDialog.FormatBytes(row.Size));
            var gridRow = candidatesGrid.Rows[index];
            gridRow.Tag = key;
            if (isExcluded) { gridRow.DefaultCellStyle.ForeColor = SystemColors.GrayText; gridRow.DefaultCellStyle.Font = strikeFont; }
            if (key == currentKey) currentIndex = index;
        }
        candidatesGrid.ResumeLayout();
        // La celda actual debe estar en una columna visible (la del nombre del archivo puede estar oculta).
        var firstVisible = candidatesGrid.Columns.GetFirstColumn(DataGridViewElementStates.Visible);
        if (currentIndex >= 0 && firstVisible is not null) candidatesGrid.CurrentCell = candidatesGrid.Rows[currentIndex].Cells[firstVisible.Index];
        else candidatesGrid.ClearSelection(); // la lista recién cargada no tiene nada seleccionado (la primera fila se marca sola): así el botón de excluir no cuenta una que nadie ha elegido
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

    private sealed record CurationState(List<string> Checked, List<string> Excluded, List<string>? HiddenColumns = null);

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
            return state is null ? null : new CurationState(state.Checked ?? new(), state.Excluded ?? new(), state.HiddenColumns);
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
            var state = new CurationState(checkedIds.OrderBy(i => i, StringComparer.Ordinal).ToList(), excluded.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(), hiddenColumns.OrderBy(c => c, StringComparer.Ordinal).ToList());
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
        using var dialog = new SaveFileDialog { Title = Loc.T("Exportar candidatas"), Filter = Loc.T("CSV (*.csv)|*.csv"), DefaultExt = "csv", FileName = $"candidatas_curado_{DateTime.Now:yyyyMMdd_HHmm}.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Sistema;ROM;Nombre;Categoría;Tamaño (bytes);Reglas;Motivo" };
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
