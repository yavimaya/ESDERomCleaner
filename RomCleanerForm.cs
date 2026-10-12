using System.Net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

public sealed partial class RomCleanerForm : LocalizedForm
{
    private readonly TextBox root = new() { Dock = DockStyle.Fill, PlaceholderText = "Carpeta que contiene Roms y ES-DE" };
    private readonly Button browse = new() { Text = "Examinar…" }, cloneButton = new() { Text = "Clonar" }, scan = new() { Text = "Analizar colección" }, cancel = new() { Text = "Cancelar", Enabled = false }, deleteSelected = new() { Text = "ELIMINAR", Enabled = false, ForeColor = Color.DarkRed };
    private readonly ComboBox systems = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox search = new() { Width = 190, PlaceholderText = "Buscar (3 caracteres mínimo)" };
    private readonly CheckBox regex = new() { Text = "Regex" };
    private readonly Button selectAll = new() { Text = "Seleccionar visibles", AutoSize = true };
    private readonly Button deselectAll = new() { Text = "Deseleccionar", AutoSize = true };
    private readonly Button clearSelection = new() { Text = "Vaciar selección", AutoSize = true, Enabled = false };
    private readonly ManualDoubleClickCheckedListBox categories = new() { Dock = DockStyle.Fill, CheckOnClick = false, SelectionMode = SelectionMode.One };
    private readonly ManualDoubleClickCheckedListBox roms = new() { Dock = DockStyle.Fill, CheckOnClick = false };
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.Control };
    private readonly VideoView videoView = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Control, Visible = false };
    private readonly ToolStripMenuItem showVideos = new("Mostrar vídeos") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem videoSound = new("Sonido en vídeos") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem showAccessory = new("Mostrar sistemas accesorios") { CheckOnClick = true, Checked = false, ToolTipText = "Muestra también las carpetas que no son sistemas de juegos, como androidapps, emulators o desktop." };
    private readonly ToolStripMenuItem screenScraperCredentials = new("Credenciales de ScreenScraper…");
    private readonly ToolStripMenuItem screenScraperSignUp = new("Alta en ScreenScraper…");
    private readonly ToolStripMenuItem curationRules = new("Reglas de curado (análisis)…");
    private readonly ToolStripMenuItem addRoms = new("Añadir ROMs…") { ToolTipText = "Arrastra ROMs para identificarlas y guardarlas en la carpeta de su sistema." };
    private readonly ToolStripMenuItem datManager = new("Bases de datos de ROMs…") { ToolTipText = "Descarga y actualiza los DAT de No-Intro y Redump con los que se identifican las ROMs." };
    private readonly ToolStripMenuItem createGamelists =new("Crear gamelists desde las ROMs…") { ToolTipText = "Crea los gamelist.xml que faltan y añade a los existentes las ROMs que no figuran, sin modificar lo que ya tienen." };
    private readonly ToolStripMenuItem bulkMedia =new("Descarga masiva de medios…");
    private readonly System.Windows.Forms.Timer videoPreviewTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer videoMonitorTimer = new() { Interval = 250 };
    private LibVLC? libVlc;
    private MediaPlayer? mediaPlayer;
    private Media? currentVideoMedia;
    private RomRow? pendingVideoRow;
    private int pendingVideoVersion;
    private readonly GroupBox miximageGroup = new() { Text = "Miximage de la ROM enfocada", Dock = DockStyle.Fill };
    private int previewVersion;
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9F) };
    private readonly TextBox description = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
    private readonly Label systemSummary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Selecciona un sistema tras analizar." };
    private readonly Label status = new() { AutoSize = true, Text = "Elige la carpeta raíz y analiza la colección." }, selectionLabel = new() { AutoSize = true, Text = "Seleccionadas: 0" };
    private readonly Label liveCounters = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Sistemas: 0 | ROMs: 0 | Ambiguas: 0 | Ficheros: 0" };
    private readonly NotebookButton extensionButton = new();
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly TraceLog trace = new();
    private List<RomRow> all = new(), current = new();
    private List<AccessoryFile> accessories = new();
    private Dictionary<string, int> systemFileCounts = new(StringComparer.OrdinalIgnoreCase);
    private int gamelistCount;
    private long mediaBytes;
    private bool sizesCalculating;
    private string cachePath = "—";
    private CancellationTokenSource? cts;
    private bool changing;
    private const string DefaultRoot = @"C:\Temp\NOVA";
    // Última ruta raíz con la que se analizó una colección; solo se actualiza cuando el análisis tiene éxito.
    private string lastRootPath = DefaultRoot;
    private readonly ToolStripMenuItem selectContext = new("Añadir a selección");
    private readonly ToolStripMenuItem backupOption = new("Crear copias de seguridad de gamelist.xml") { CheckOnClick = true };
    private bool createGamelistBackups = false;

    private sealed record AppOptions(bool CreateGamelistBackups, bool ShowVideos, bool VideoSound, bool ShowAccessorySystems, string RootPath);
    private static string OptionsPath => Path.Combine(AppPaths.Data, "settings.json");

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    private static AppOptions LoadOptions()
    {
        var defaults = new AppOptions(false, true, true, false, DefaultRoot);
        try
        {
            if (!File.Exists(OptionsPath)) return defaults;
            using var document = JsonDocument.Parse(File.ReadAllText(OptionsPath));
            var element = document.RootElement;
            return new AppOptions(
                ReadBool(element, nameof(AppOptions.CreateGamelistBackups), defaults.CreateGamelistBackups),
                ReadBool(element, nameof(AppOptions.ShowVideos), defaults.ShowVideos),
                ReadBool(element, nameof(AppOptions.VideoSound), defaults.VideoSound),
                ReadBool(element, nameof(AppOptions.ShowAccessorySystems), defaults.ShowAccessorySystems),
                element.TryGetProperty(nameof(AppOptions.RootPath), out var rootValue) && rootValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rootValue.GetString()) ? rootValue.GetString()! : defaults.RootPath);
        }
        catch { return defaults; }
    }

    private static void WriteOptions(AppOptions options)
    {
        string? temporary = null;
        try
        {
            string path = OptionsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
            var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) try { File.Delete(temporary); } catch { }
        }
    }

    private void SaveBackupPreference(bool enabled)
    {
        try
        {
            WriteOptions(new AppOptions(enabled, showVideos.Checked, videoSound.Checked, showAccessory.Checked, lastRootPath));
            createGamelistBackups = enabled;
            trace.Add("option_gamelist_backups_changed", enabled ? "enabled" : "disabled");
            status.Text = enabled ? "Copias de seguridad de gamelist.xml activadas." : "Copias de seguridad de gamelist.xml desactivadas.";
        }
        catch (Exception ex)
        {
            backupOption.Checked = createGamelistBackups;
            trace.Add("option_gamelist_backups_error", ex.ToString());
            MessageBox.Show(this, "No se pudo guardar la opción. Se mantiene la configuración anterior.\r\n\r\n" + ex.Message,
                "Opciones", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveOptions()
    {
        try
        {
            WriteOptions(new AppOptions(createGamelistBackups, showVideos.Checked, videoSound.Checked, showAccessory.Checked, lastRootPath));
            trace.Add("option_video_changed", $"show={showVideos.Checked}; sound={videoSound.Checked}");
        }
        catch (Exception ex)
        {
            trace.Add("option_video_error", ex.ToString());
            status.Text = "No se pudo guardar la opción de vídeo; solo se aplicará en esta sesión.";
        }
    }

    public RomCleanerForm()
    {
        Text = $"ES-DE ROM Cleaner {AppVersion()}"; Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; MinimumSize = new Size(1000, 680); Size = new Size(1060, 720); StartPosition = FormStartPosition.CenterScreen;
        var options = LoadOptions();
        createGamelistBackups = options.CreateGamelistBackups;
        backupOption.Checked = createGamelistBackups;
        showVideos.Checked = options.ShowVideos;
        videoSound.Checked = options.VideoSound;
        showAccessory.Checked = options.ShowAccessorySystems;
        lastRootPath = options.RootPath;
        KeyPreview = true;
        KeyDown += HandlePanelNavigation;
        FormClosing += (_, _) => StopVideoPlayback();
        FormClosed += (_, _) => DisposeVideoPlayer();
        BuildLayout();
        root.Text = lastRootPath;
        WireEvents();
    }

    private void HandlePanelNavigation(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Right && categories.ContainsFocus)
        {
            if (roms.SelectedIndex < 0 && roms.Items.Count > 0) roms.SelectedIndex = 0;
            roms.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Left && roms.ContainsFocus)
        {
            if (categories.SelectedIndex < 0 && categories.Items.Count > 0) categories.SelectedIndex = 0;
            categories.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }
    private void OpenCloneDialog()
    {
        ClearSearchForContextChange();
        if (!EnsureCollectionRoot()) return;
        trace.Add("mirror_dialog_opened", root.Text);
        try
        {
            using var dialog = new MirrorSyncDialog(root.Text, trace.Add);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            trace.Add("mirror_dialog_error", ex.ToString());
            MessageBox.Show(this, "No se pudo abrir el formulario de clonación.\r\n\r\n" + ex.Message,
                "Clonar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private void WireEvents()
    {
        backupOption.Click += (_, _) => SaveBackupPreference(backupOption.Checked);
        browse.Click += (_, _) => { ClearSearchForContextChange(); using var d = new FolderBrowserDialog { Description = Loc.T("Selecciona la carpeta con Roms y ES-DE") }; if (d.ShowDialog(this) == DialogResult.OK) root.Text = d.SelectedPath; };
        cloneButton.Click += (_, _) => OpenCloneDialog();
        root.Enter += (_, _) => ClearSearchForContextChange();
        systems.Enter += (_, _) => ClearSearchForContextChange();
        systems.DropDown += (_, _) => ClearSearchForContextChange();
        categories.Enter += (_, _) => ClearSearchForContextChange();
        categories.MouseDown += (_, _) => ClearSearchForContextChange();
        scan.Click += async (_, _) => await ScanAsync(); cancel.Click += (_, _) => cts?.Cancel();
        systems.SelectedIndexChanged += (_, _) => { if (!changing) { ClearSearchForContextChange(); ChangeSystem(); } }; categories.SelectedIndexChanged += (_, _) => { if (!changing) { ClearSearchForContextChange(); RefreshList(); } };
        selectAll.Click += (_, _) => SetVisibleSelection(true);
        deselectAll.Click += (_, _) => SetVisibleSelection(false);
        clearSelection.Click += (_, _) => ClearSelection();
        deleteSelected.Click += async (_, _) => await DeleteSelectedAsync();
        extensionButton.Click += (_, _) => ShowExtensions();
        Loc.SetToolTip(new ToolTip(), extensionButton, "Ver listado de extensiones y cantidad de ROMs");
        categories.ItemCheck += (_, e) =>
        {
            // Programmatic category synchronization also raises ItemCheck; do not queue it as a user action.
            if (changing) return;
            int index = e.Index;
            bool check = e.NewValue == CheckState.Checked;
            BeginInvoke(new Action(() => CategoryChanged(index, check)));
        };
        roms.ItemCheck += (_, e) =>
        {
            if (changing || e.Index < 0 || e.Index >= roms.Items.Count) return;
            string path = (roms.Items[e.Index] as RomItem)?.Row.Path ?? "";
            bool isChecked = e.NewValue == CheckState.Checked;
            if (path.Length == 0) return;
            BeginInvoke(new Action(() => ApplyRomCheck(path, isChecked)));
        };
        roms.DoubleClick += (_, _) => ToggleFocused(); roms.SelectedIndexChanged += (_, _) => { if (!changing) ShowDetails(); };
        search.TextChanged += (_, _) => RefreshList(); regex.CheckedChanged += (_, _) => RefreshList();
        roms.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { int i = roms.IndexFromPoint(e.Location); if (i >= 0) roms.SelectedIndex = i; } };
        roms.ContextMenuStrip = new ContextMenuStrip(); roms.ContextMenuStrip.Items.Add(selectContext);
        roms.ContextMenuStrip.Items.Add("Editar metadatos…", null, (_, _) => EditMetadata());
        roms.ContextMenuStrip.Items.Add("Scrapear con ScreenScraper…", null, (_, _) => OpenScraperDialog());
        roms.ContextMenuStrip.Opening += (_, e) => { if (roms.SelectedItem is not RomItem item) e.Cancel = true; else selectContext.Text = selected.Contains(item.Row.Path) ? "Quitar de selección" : "Añadir a selección"; };
        selectContext.Click += (_, _) => ToggleFocused();        videoPreviewTimer.Tick += async (_, _) => await StartFocusedVideoAsync();
        videoMonitorTimer.Tick += (_, _) => CheckVideoPlayback();
        showVideos.CheckedChanged += (_, _) =>
        {
            SaveOptions();
            if (!showVideos.Checked)
            {
                videoPreviewTimer.Stop();
                StopVideoPlayback();
                miximageGroup.Text = pendingVideoRow is null ? "Miximage de la ROM enfocada" : $"Miximage de {pendingVideoRow.GameName}";
            }
            else if (pendingVideoRow is not null) ScheduleVideoPreview(pendingVideoRow, pendingVideoVersion);
        };
        bulkMedia.Click += async (_, _) => await OpenBulkMediaDialogAsync();
        WireFolderMenus();
        WireLanguageMenu();
        addRoms.Click += async (_, _) => await OpenAddRomsAsync();
        datManager.Click += (_, _) => { using var d = new DatManagerDialog(trace, root.Text.Trim().Trim('"')); d.ShowDialog(this); if (RomDatabase.InstalledCount() > 0) _ = RomDatabase.GetIndexAsync(); };
        createGamelists.Click +=async (_, _) => await CreateGamelistsAsync(scanAfter: true, quiet: false);
        curationRules.Click +=async (_, _) => await OpenCurationDialogAsync();
        screenScraperCredentials.Click += (_, _) =>
        {
            if (ScreenScraperAccount.Edit(this, trace, ScreenScraperCredentialStore.Load()) is not null) status.Text = "Credenciales de ScreenScraper guardadas.";
        };
        screenScraperSignUp.Click += (_, _) => ScreenScraperAccount.OpenSignUp(this);
        showAccessory.CheckedChanged += (_, _) =>
        {
            SaveOptions();
            trace.Add("option_accessory_systems_changed", showAccessory.Checked ? "shown" : "hidden");
            RefreshSystemsList();
        };
        videoSound.CheckedChanged += (_, _) => { if (mediaPlayer is not null) mediaPlayer.Volume = videoSound.Checked ? 100 : 0; SaveOptions(); };
    }

    private void ShowExtensions()
    {
        var counts = all
            .Where(row => row.Status == RomStatus.Ok)
            .GroupBy(row =>
            {
                string extension = Path.GetExtension(row.Path).ToUpperInvariant();
                return extension.Length == 0 ? "(sin extensión)" : extension;
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Extension = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var dialog = new Form
        {
            Text = "Extensiones y datos de colección",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 500),
            MinimumSize = new Size(380, 340),
            ShowInTaskbar = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var summary = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = counts.Length == 0 ? "No hay ROMs identificadas." : $"Extensiones: {counts.Length:N0}    |    ROMs: {counts.Sum(item => item.Count):N0}"
        };
        var gamelists = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Gamelists encontrados: {gamelistCount:N0}"
        };
        var cacheInfo = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Caché: {cachePath}"
        };
        Loc.SetToolTip(new ToolTip(), cacheInfo, $"Caché: {cachePath}");
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
        list.Columns.Add("Extensión", 250);
        list.Columns.Add("ROMs", 120, HorizontalAlignment.Center);
        foreach (var item in counts)
            list.Items.Add(new ListViewItem(new[] { item.Extension, item.Count.ToString("N0") }));
        var close = new Button { Text = "Cerrar", DialogResult = DialogResult.OK, Width = 84, Height = 28 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        buttons.Controls.Add(close);
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(gamelists, 0, 1);
        layout.Controls.Add(cacheInfo, 0, 2);
        layout.Controls.Add(list, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = close;
        trace.Add("extensions_dialog_opened", $"extensions={counts.Length}; roms={counts.Sum(item => item.Count)}; gamelists={gamelistCount}; cache={cachePath}");
        dialog.ShowDialog(this);
    }
    private void ShowTrace()
    {
        using var dialog = new TraceViewerDialog(trace);
        dialog.ShowDialog(this);
    }

    private sealed record CategoryItem(string Genre, int RomCount) { public override string ToString() => $"{Genre} [{RomCount:N0}]"; }
    private sealed record RomItem(RomRow Row) { public override string ToString() => Row.Display; }
}
