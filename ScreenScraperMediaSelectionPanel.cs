using System.Diagnostics;

namespace ESDERomCleaner;

// Pestaña que elige, por cada carpeta de downloaded_media de ES-DE, qué medio de ScreenScraper guardar en la colección.
// El estado, el progreso y el bloqueo de la ventana se comunican al diálogo anfitrión mediante eventos.
internal sealed class ScreenScraperMediaSelectionPanel : UserControl
{
    // Regiones de reserva cuando ScreenScraper no tiene ese tipo de medio para la región de la ROM.
    private static readonly string[] FallbackRegions = ["wor", "eu", "us", "jp"];

    private sealed class FolderState
    {
        public required EsDeMediaFolder Folder { get; init; }
        public required List<SsMedia> Candidates { get; init; }
        public required string TargetBase { get; init; }
        public SsMedia? Chosen { get; set; }
        public bool Save { get; set; }
        public bool Fallback { get; init; }
        public List<string> Existing { get; set; } = new();
    }

    private sealed record CandidateItem(SsMedia Media, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly SsGame game;
    private readonly RomRow row;
    private readonly string root;
    private readonly Func<ScreenScraperCredentials> credentials;
    private readonly TraceLog trace;
    private readonly bool preferNormalizedVideo;
    private readonly List<FolderState> states = new();
    private readonly List<string> romRegions;
    private readonly string romRegionsSource;
    private CancellationTokenSource? cts;
    private bool updating;

    private readonly DataGridView foldersGrid = new();
    private readonly ListBox candidatesList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly MediaPreviewPane currentPreview = new() { Dock = DockStyle.Fill };
    private readonly MediaPreviewPane candidatePreview = new() { Dock = DockStyle.Fill };
    private readonly CheckBox currentSound = new() { Text = "Sonido", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
    private readonly CheckBox candidateSound = new() { Text = "Sonido", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
    private readonly Label currentCaption = NewCaption();
    private readonly Label candidateCaption = NewCaption();
    private readonly Button openCurrentButton = new() { Text = "Abrir", AutoSize = true, Enabled = false };
    private readonly Button openCandidateButton = new() { Text = "Abrir", AutoSize = true, Enabled = false };
    private readonly Button saveButton = new() { Text = "Guardar cambios", AutoSize = true, MinimumSize = new Size(170, 30), Anchor = AnchorStyles.None };

    public bool CollectionChanged { get; private set; }
    public bool IsSaving { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<int>? ProgressChanged;
    public event Action<bool>? BusyChanged;
    // Se lanza al terminar un guardado, haya cambiado la colección o solo la caché de descargas.
    public event Action? SaveFinished;

    private void Report(string text) => StatusChanged?.Invoke(text);

    private void ReportProgress(int percent) => ProgressChanged?.Invoke(percent);

    public ScreenScraperMediaSelectionPanel(SsGame game, RomRow row, string root, Func<ScreenScraperCredentials> credentials, TraceLog trace, bool preferNormalizedVideo)
    {
        this.game = game;
        this.row = row;
        this.root = root;
        this.credentials = credentials;
        this.trace = trace;
        this.preferNormalizedVideo = preferNormalizedVideo;
        // Solo se ofrecen medios de la región de la ROM (o sin región). Fuente: ScreenScraper; si no la da, el nombre del archivo.
        romRegions = game.RomRegions.Count > 0 ? game.RomRegions.ToList() : SsRegions.FromFileName(row.RomName);
        romRegionsSource = game.RomRegions.Count > 0 ? "según ScreenScraper" : romRegions.Count > 0 ? "según el nombre del archivo" : "desconocida: se ofrecen todas";
        Dock = DockStyle.Fill;
        BuildLayout();
        BuildStates();

        foldersGrid.SelectionChanged += (_, _) => ShowSelectedFolder();
        foldersGrid.CurrentCellDirtyStateChanged += (_, _) => { if (foldersGrid.IsCurrentCellDirty) foldersGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        foldersGrid.CellValueChanged += (_, e) =>
        {
            if (updating || e.RowIndex < 0 || e.ColumnIndex != foldersGrid.Columns["save"]!.Index) return;
            var state = states[e.RowIndex];
            state.Save = state.Chosen is not null && foldersGrid.Rows[e.RowIndex].Cells["save"].Value is true;
            UpdateRow(e.RowIndex);
        };
        // ← → recorren los candidatos de la carpeta enfocada sin salir de la lista de carpetas.
        foldersGrid.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Left or Keys.Right) || candidatesList.Items.Count == 0) return;
            int next = candidatesList.SelectedIndex + (e.KeyCode == Keys.Right ? 1 : -1);
            candidatesList.SelectedIndex = Math.Clamp(next, 0, candidatesList.Items.Count - 1);
            e.Handled = true;
        };
        candidatesList.SelectedIndexChanged += (_, _) => ChooseCandidate();
        openCurrentButton.Click += (_, _) => { if (SelectedState()?.Existing.FirstOrDefault() is string path) OpenFile(path); };
        openCandidateButton.Click += (_, _) =>
        {
            if (SelectedState()?.Chosen is SsMedia media && SsMediaCache.IsCached(game.Id, media)) OpenFile(SsMediaCache.PathFor(game.Id, media));
        };
        saveButton.Click += async (_, _) => await SaveAsync();
        currentSound.CheckedChanged += (_, _) => currentPreview.Sound = currentSound.Checked;
        candidateSound.CheckedChanged += (_, _) => candidatePreview.Sound = candidateSound.Checked;
    }

    public void CancelSave() => cts?.Cancel();

    // La pestaña pasa a verse: se vuelve a leer la caché (el diálogo pudo descargar medios) y se retoma la vista previa.
    public void Activate()
    {
        if (foldersGrid.Rows.Count > 0 && foldersGrid.SelectedRows.Count == 0) foldersGrid.Rows[0].Selected = true;
        ShowSelectedFolder();
        foldersGrid.Focus();
        if (!IsSaving) Report("Marca las carpetas que quieres guardar (ninguna viene marcada). ↑ ↓ cambian de carpeta; ← → cambian de candidato.");
    }

    // La pestaña deja de verse: los vídeos se paran para que no sigan sonando.
    public void Deactivate()
    {
        currentPreview.StopVideo();
        candidatePreview.StopVideo();
    }

    private const int HeaderRow = 22;
    private const int CaptionRow = 56;

    private static Label NewCaption() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.TopLeft };

    private void BuildLayout()
    {
        foldersGrid.Dock = DockStyle.Fill;
        foldersGrid.AllowUserToAddRows = false;
        foldersGrid.AllowUserToDeleteRows = false;
        foldersGrid.AllowUserToResizeRows = false;
        foldersGrid.RowHeadersVisible = false;
        foldersGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        foldersGrid.MultiSelect = false;
        foldersGrid.BackgroundColor = SystemColors.Window;
        foldersGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "save", HeaderText = "Guardar", Width = 60 });
        foreach (var (name, header, width) in new[] { ("folder", "Carpeta ES-DE", 105), ("desc", "Qué es", 150), ("chosen", "Elegido de ScreenScraper", 160), ("current", "En la colección", 170), ("action", "Acción", 150) })
            foldersGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        foldersGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var candidatesGroup = new GroupBox { Text = "Candidatos para la carpeta enfocada (← → desde la lista de carpetas)", Dock = DockStyle.Fill };
        candidatesGroup.Controls.Add(candidatesList);

        // La lista de candidatos es corta (3-4 elementos): altura fija pequeña y el resto del espacio para el grid.
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        left.Controls.Add(foldersGrid, 0, 0);
        left.Controls.Add(candidatesGroup, 0, 1);

        var compare = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        compare.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compare.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderRow));
        compare.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        compare.RowStyles.Add(new RowStyle(SizeType.Absolute, CaptionRow));
        compare.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderRow));
        compare.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        compare.RowStyles.Add(new RowStyle(SizeType.Absolute, CaptionRow));
        compare.Controls.Add(Header("Actual en la colección", currentSound, openCurrentButton), 0, 0);
        compare.Controls.Add(currentPreview, 0, 1);
        compare.Controls.Add(currentCaption, 0, 2);
        compare.Controls.Add(Header("Candidato de ScreenScraper", candidateSound, openCandidateButton), 0, 3);
        compare.Controls.Add(candidatePreview, 0, 4);
        compare.Controls.Add(candidateCaption, 0, 5);

        // Columna de vistas previas: su ancho sale de la altura disponible para que cada medio quede en un recuadro 4:3 sin barras
        // vacías, y el resto del ancho es para el grid. Se recalcula con cada cambio de tamaño (maximizar, restaurar…).
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(compare, 1, 0);
        split.SizeChanged += (_, _) =>
        {
            int previewHeight = (split.Height - 2 * HeaderRow - 2 * CaptionRow) / 2;
            int width = Math.Clamp(previewHeight * 4 / 3 + 16, 380, Math.Max(380, split.Width - 520));
            if ((int)split.ColumnStyles[1].Width != width) split.ColumnStyles[1].Width = width;
        };

        var info = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            Text = $"Juego: {game.BestName}   ·   Región de la ROM: {(romRegions.Count > 0 ? string.Join(", ", romRegions) : "?")} ({romRegionsSource}); solo medios de esa región o sin región; si no hay, de wor, eu, us o jp, por ese orden   ·   Los medios se guardan con el nombre de la ROM en ES-DE/downloaded_media/{row.System}/<carpeta>/. Lo que se sustituye pasa a una copia de seguridad."
        };

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.Controls.Add(saveButton, 0, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(info, 0, 0);
        layout.Controls.Add(split, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
    }

    private static Control Header(string text, CheckBox sound, Button button)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        button.Margin = Padding.Empty;
        panel.Controls.Add(sound, 1, 0);
        panel.Controls.Add(button, 2, 0);
        return panel;
    }

    private void BuildStates()
    {
        updating = true;
        try
        {
            foreach (var folder in EsDeMediaInstaller.Folders)
            {
                var usable = game.Medias.Where(m => EsDeMediaInstaller.Accepts(folder, m)).ToList();
                string[] typeOrder = TypeOrder(folder);
                int TypeRank(SsMedia m) => Array.FindIndex(typeOrder, t => t.Equals(m.Type, StringComparison.OrdinalIgnoreCase));
                var candidates = usable
                    .Where(MatchesRomRegion)
                    .OrderBy(TypeRank)
                    .ThenBy(m => RomRegionRank(m.Region))
                    .ToList();
                // Sin medios de la región de la ROM: se ofrecen los de las regiones de reserva, en su orden de prioridad.
                bool fallback = candidates.Count == 0 && romRegions.Count > 0;
                if (fallback)
                    candidates = usable
                        .Where(m => FallbackRank(m.Region) < FallbackRegions.Length)
                        .OrderBy(m => FallbackRank(m.Region))
                        .ThenBy(TypeRank)
                        .ToList();
                var state = new FolderState { Folder = folder, Candidates = candidates, Fallback = fallback && candidates.Count > 0, TargetBase = EsDeMediaInstaller.TargetBase(root, row, folder.Folder) };
                state.Existing = EsDeMediaInstaller.Existing(state.TargetBase);
                state.Chosen = candidates.FirstOrDefault();
                // Nada se guarda sin marcarlo: ni siquiera las carpetas vacías de la colección.
                state.Save = false;
                states.Add(state);
                foldersGrid.Rows.Add(state.Save, folder.Folder, folder.Description, "", "", "");
                UpdateRow(states.Count - 1);
            }
        }
        catch (Exception ex)
        {
            Report(ex.Message);
            saveButton.Enabled = false;
        }
        finally { updating = false; }
    }

    private bool MatchesRomRegion(SsMedia media) =>
        media.Region.Length == 0 || romRegions.Count == 0 || romRegions.Contains(media.Region, StringComparer.OrdinalIgnoreCase);

    // Los medios sin región (válidos para todas) van detrás de los de la región exacta de la ROM.
    private int RomRegionRank(string region)
    {
        int index = romRegions.FindIndex(r => r.Equals(region, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : romRegions.Count;
    }

    private static int FallbackRank(string region)
    {
        int index = Array.FindIndex(FallbackRegions, r => r.Equals(region, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : FallbackRegions.Length;
    }

    private FolderState? SelectedState()
    {
        if (foldersGrid.SelectedRows.Count != 1) return null;
        int index = foldersGrid.SelectedRows[0].Index;
        return index >= 0 && index < states.Count ? states[index] : null;
    }

    private void UpdateRow(int index)
    {
        var state = states[index];
        var gridRow = foldersGrid.Rows[index];
        bool previous = updating;
        updating = true;
        try
        {
            var saveCell = gridRow.Cells["save"];
            saveCell.Value = state.Save;
            saveCell.ReadOnly = state.Chosen is null;
            gridRow.Cells["chosen"].Value = state.Chosen is null ? "(sin candidatos)" : $"{state.Chosen.Label} · {state.Chosen.Format}" + (state.Fallback ? " · otra región" : "");
            gridRow.Cells["chosen"].ToolTipText = state.Fallback ? "No hay medios de la región de la ROM; se ofrecen los de wor, eu, us y jp, por ese orden." : "";
            gridRow.Cells["current"].Value = state.Existing.Count == 0 ? "—" : Path.GetFileName(state.Existing[0]) + (state.Existing.Count > 1 ? $" (+{state.Existing.Count - 1})" : "");
            var action = gridRow.Cells["action"];
            (action.Value, action.Style.ForeColor) = (state.Chosen, state.Save, state.Existing.Count) switch
            {
                (null, _, _) => ("Sin candidatos", SystemColors.GrayText),
                (_, true, > 0) => ("Sustituir el actual", Color.DarkOrange),
                (_, true, _) => ("Añadir", Color.DarkGreen),
                (_, false, > 0) => ("Conservar el actual", SystemColors.ControlText),
                _ => ("No guardar", SystemColors.GrayText)
            };
            gridRow.DefaultCellStyle.ForeColor = state.Chosen is null ? SystemColors.GrayText : SystemColors.ControlText;
        }
        finally { updating = previous; }
    }

    private void ShowSelectedFolder()
    {
        var state = SelectedState();
        updating = true;
        try
        {
            candidatesList.BeginUpdate();
            candidatesList.Items.Clear();
            if (state is not null)
                foreach (var media in state.Candidates) candidatesList.Items.Add(new CandidateItem(media, CandidateText(media)));
            candidatesList.EndUpdate();
            if (state?.Chosen is not null) candidatesList.SelectedIndex = state.Candidates.IndexOf(state.Chosen);
        }
        finally { updating = false; }
        ShowCurrent(state);
        ShowCandidate(state);
    }

    private string CandidateText(SsMedia media)
    {
        bool cached = SsMediaCache.IsCached(game.Id, media);
        string size = media.Size > 0 ? ScreenScraperDialog.FormatBytes(media.Size) : "?";
        string extra = cached ? ImageResolution(SsMediaCache.PathFor(game.Id, media)) : "sin descargar";
        return $"{media.Label} · {media.Format} · {size}" + (extra.Length > 0 ? $" · {extra}" : "");
    }

    private void ChooseCandidate()
    {
        if (updating || SelectedState() is not FolderState state || candidatesList.SelectedItem is not CandidateItem item) return;
        state.Chosen = item.Media;
        UpdateRow(states.IndexOf(state));
        ShowCandidate(state);
    }

    private void ShowCurrent(FolderState? state)
    {
        string? path = state?.Existing.FirstOrDefault();
        openCurrentButton.Enabled = path is not null;
        if (state is null || path is null)
        {
            currentPreview.ShowMessage(state is null ? "" : "Vacío");
            currentCaption.Text = state is null ? "" : "No hay ningún medio de este tipo para la ROM.";
            return;
        }
        currentPreview.ShowFile(path);
        currentCaption.Tag = path;
        var info = new FileInfo(path);
        currentCaption.Text = $"{info.Name}\r\n{ScreenScraperDialog.FormatBytes(info.Length)}" +
            (state.Folder.Kind == "image" ? $" · {ImageResolution(path)}" : state.Folder.Kind == "video" ? VideoInfoPending : "") +
            (state.Existing.Count > 1 ? $"\r\nHay {state.Existing.Count} archivos con este nombre; se sustituirían todos." : "");
        if (state.Folder.Kind == "video") _ = FillVideoInfoAsync(currentCaption, path);
        _ = FillCurrentIdentityAsync(state, path);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> Md5Cache = new(StringComparer.OrdinalIgnoreCase);

    private static Task<string> Md5OfAsync(string path)
    {
        var info = new FileInfo(path);
        return Md5Cache.GetOrAdd($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => Task.Run(async () =>
        {
            try
            {
                await using var stream = File.OpenRead(path);
                return Convert.ToHexString(await System.Security.Cryptography.MD5.HashDataAsync(stream));
            }
            catch { return ""; }
        }));
    }

    // Dice a qué medio de ScreenScraper corresponde exactamente el archivo que hay en la colección (por MD5), para no confundir
    // p. ej. un vídeo normalizado con el original cuando ambos se llaman igual en disco.
    private async Task FillCurrentIdentityAsync(FolderState state, string path)
    {
        string md5 = await Md5OfAsync(path);
        if (IsDisposed || md5.Length == 0 || !Equals(currentCaption.Tag, path)) return;
        var match = game.Medias.FirstOrDefault(m => EsDeMediaInstaller.Accepts(state.Folder, m) && m.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase));
        currentCaption.Text += match is not null
            ? $"\r\nIdéntico al medio de ScreenScraper: {match.Label}"
            : "\r\nNo coincide con ningún medio de ScreenScraper (otro origen o editado).";
    }

    private void ShowCandidate(FolderState? state)
    {
        var media = state?.Chosen;
        if (state is null || media is null)
        {
            candidatePreview.ShowMessage(state is null ? "" : "Sin candidatos");
            candidateCaption.Text = state is null ? "" : "ScreenScraper no tiene medios de este tipo para la región de la ROM en un formato que ES-DE pueda usar.";
            openCandidateButton.Enabled = false;
            return;
        }
        bool cached = SsMediaCache.IsCached(game.Id, media);
        string path = SsMediaCache.PathFor(game.Id, media);
        openCandidateButton.Enabled = cached;
        if (cached) candidatePreview.ShowFile(path);
        else candidatePreview.ShowMessage("Sin descargar.\r\nSe descargará al guardar.");
        string description = SsMediaInfo.Describe(media.Type);
        candidateCaption.Text = $"{media.Label}{(description.Length > 0 ? " — " + description : "")}{(state.Fallback ? "  (no hay de la región de la ROM)" : "")}\r\n" +
            $"{media.Format} · {(media.Size > 0 ? ScreenScraperDialog.FormatBytes(media.Size) : "?")}" +
            (!cached ? " · sin descargar: se descargará al guardar" : state.Folder.Kind == "image" ? $" · {ImageResolution(path)}" : state.Folder.Kind == "video" ? VideoInfoPending : "");
        candidateCaption.Tag = path;
        if (cached && state.Folder.Kind == "video") _ = FillVideoInfoAsync(candidateCaption, path);
    }

    private const string VideoInfoPending = " · leyendo vídeo…";

    // Completa el pie con la resolución y duración del vídeo si el panel sigue mostrando ese archivo.
    private static async Task FillVideoInfoAsync(Label caption, string path)
    {
        string info = await VideoProbe.DescribeAsync(path);
        if (caption.IsDisposed || !Equals(caption.Tag, path)) return;
        caption.Text = caption.Text.Replace(VideoInfoPending, info.Length > 0 ? " · " + info : "");
    }

    // Orden de preferencia de tipos de la carpeta; en vídeos depende de la opción «Vídeo normalizado».
    private string[] TypeOrder(EsDeMediaFolder folder) =>
        folder.Folder == "videos" ? (preferNormalizedVideo ? ["video-normalized", "video"] : ["video", "video-normalized"]) : folder.SsTypes;

    private static string ImageResolution(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream, false, false);
            return $"{image.Width}×{image.Height}";
        }
        catch { return ""; }
    }

    private void OpenFile(string path)
    {
        // Se paran los vídeos de la comparación para que no suenen a la vez que la otra aplicación.
        currentPreview.StopVideo();
        candidatePreview.StopVideo();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { QuietMessage.Show(this, "No se pudo abrir el medio", ex.Message); }
    }

    private void SetBusy(bool busy)
    {
        foldersGrid.Enabled = !busy;
        candidatesList.Enabled = !busy;
        saveButton.Enabled = !busy;
        IsSaving = busy;
        BusyChanged?.Invoke(busy);
    }

    private async Task SaveAsync()
    {
        foldersGrid.EndEdit();
        var chosen = states.Where(s => s.Save && s.Chosen is not null).ToList();
        if (chosen.Count == 0)
        {
            QuietMessage.Show(this, "Guardar medios", "No hay ninguna carpeta marcada para guardar.");
            return;
        }
        // Se vuelve a mirar el disco justo antes de confirmar por si algo cambió mientras el diálogo estaba abierto.
        foreach (var state in chosen) state.Existing = EsDeMediaInstaller.Existing(state.TargetBase);
        var summary = chosen.Select(s => new MediaSaveRow(
            s.Folder.Folder,
            s.Chosen!.Label,
            $"{s.Chosen.Format} · {(s.Chosen.Size > 0 ? ScreenScraperDialog.FormatBytes(s.Chosen.Size) : "?")}",
            string.Join(", ", s.Existing.Select(Path.GetFileName)))).ToList();
        using (var confirm = new MediaSaveConfirmDialog(row.RomName, summary, EsDeMediaInstaller.BackupRoot))
            if (confirm.ShowDialog(this) != DialogResult.OK) return;

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetBusy(true);
        try
        {
            // 1. Descargar lo que falte en la caché. Si algo falla, no se toca la colección.
            var missing = chosen.Where(s => !SsMediaCache.IsCached(game.Id, s.Chosen!)).ToList();
            var client = new ScreenScraperClient(credentials());
            for (int i = 0; i < missing.Count; i++)
            {
                var media = missing[i].Chosen!;
                int position = i + 1;
                var progress = new Progress<long>(received =>
                {
                    Report($"Descargando {position}/{missing.Count}: {media.Label} · {ScreenScraperDialog.FormatBytes(received)}" +
                        (media.Size > 0 ? $" de {ScreenScraperDialog.FormatBytes(media.Size)}" : ""));
                    ReportProgress(media.Size > 0 ? Math.Clamp((int)(received * 100 / media.Size), 0, 100) : 0);
                });
                try { await client.DownloadMediaAsync(media, SsMediaCache.PathFor(game.Id, media), progress, token); }
                catch (ScreenScraperException ex)
                {
                    trace.Add("screenscraper_media_save_aborted", $"rom={row.RomName}; ss_game={game.Id}; media={media.Label}; {ex.Message}");
                    Report("No se guardó nada: " + ex.Message);
                    QuietMessage.Show(this, "Guardar medios", $"No se pudo descargar {media.Label}:\r\n{ex.Message}\r\n\r\nNo se ha modificado la colección.");
                    return;
                }
            }

            // 2. Copiar a downloaded_media.
            Report("Guardando en la colección…");
            ReportProgress(0);
            var requests = chosen.Select(s => new MediaInstallRequest(s.Folder.Folder, s.Chosen!, SsMediaCache.PathFor(game.Id, s.Chosen!))).ToList();
            var errors = new List<string>();
            var installProgress = new Progress<string>(Report);
            // Los resultados parciales se conservan aunque se cancele a mitad, para que la traza refleje lo que ya se guardó.
            var results = new List<MediaInstallResult>();
            try { await Task.Run(() => EsDeMediaInstaller.Install(root, row, requests, results, errors, installProgress, token), token); }
            finally
            {
                CollectionChanged |= results.Count > 0;
                if (results.Count > 0 || errors.Count > 0)
                    trace.Add("screenscraper_media_saved", $"system={row.System}; rom={row.RomName}; ss_game={game.Id}; " +
                        $"saved={string.Join(", ", results.Select(r => $"{r.Folder}:{r.Media.Label}" + (r.ReplacedFiles.Count > 0 ? $"[sustituye {r.ReplacedFiles.Count}]" : "")))}" +
                        (results.Any(r => r.BackupFiles.Count > 0) ? $"; backups={string.Join(" | ", results.SelectMany(r => r.BackupFiles))}" : "") +
                        (errors.Count > 0 ? $"; errors={string.Join(" | ", errors)}" : ""));
            }

            foreach (var state in states) state.Existing = EsDeMediaInstaller.Existing(state.TargetBase);
            foreach (var state in chosen.Where(s => results.Any(r => r.Folder == s.Folder.Folder))) state.Save = false;
            for (int i = 0; i < states.Count; i++) UpdateRow(i);
            ShowSelectedFolder();

            Report($"Guardados {results.Count} de {requests.Count} medios" + (errors.Count > 0 ? $"; {errors.Count} con error." : "."));
            // Si todo fue bien basta la barra de estado; solo se interrumpe al usuario cuando hubo errores.
            if (errors.Count > 0)
                QuietMessage.Show(this, "Guardar medios",
                    $"Guardados {results.Count} de {requests.Count} medios en ES-DE/downloaded_media/{row.System}.\r\n\r\nErrores:\r\n" + string.Join("\r\n", errors));
        }
        catch (OperationCanceledException)
        {
            foreach (var state in states) state.Existing = EsDeMediaInstaller.Existing(state.TargetBase);
            for (int i = 0; i < states.Count; i++) UpdateRow(i);
            Report("Cancelado. Lo que ya se guardó se mantiene; el resto no se tocó.");
        }
        catch (Exception ex)
        {
            trace.Add("screenscraper_media_save_error", $"rom={row.RomName}; {ex.Message}");
            Report("Error: " + ex.Message);
            QuietMessage.Show(this, "Guardar medios", ex.Message);
        }
        finally
        {
            SetBusy(false);
            SaveFinished?.Invoke();
        }
    }
}
