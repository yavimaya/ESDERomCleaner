using System.Diagnostics;
using System.Text;

namespace ESDERomCleaner;

internal sealed class ScreenScraperDialog : Form
{
    private static readonly string[] PreferredLanguages = ["es", "en", "fr", "de", "it", "pt"];

    private readonly RomRow row;
    private readonly string root;
    private readonly bool preferNormalizedVideo;
    private readonly TraceLog trace;
    private ScreenScraperCredentials credentials;
    private List<SsSystem> allSystems = new();
    private CancellationTokenSource? cts;

    // El sistema se deduce de la carpeta; el desplegable solo aparece si no se puede deducir o si ScreenScraper no reconoce la ROM.
    private readonly ComboBox systemCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, Visible = false };
    private readonly Label systemPrompt = new() { Text = "Sistema en ScreenScraper:", AutoSize = true, Margin = new Padding(3, 7, 3, 0), Visible = false };
    private readonly Label systemInfo = new() { Text = "Sistema: detectando…", AutoSize = true, Margin = new Padding(3, 7, 12, 0) };
    private readonly Button searchButton = new() { Text = "Refrescar consulta", AutoSize = true };
    private readonly Button cancelButton = new() { Text = "Cancelar", AutoSize = true, Enabled = false };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly DataGridView metadataGrid = new();
    private readonly DataGridView mediaGrid = new();
    private readonly Label mediaSummary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox rawJson = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F) };

    // Vista previa de los medios descargados a la caché local.
    private SsGame? currentGame;
    private readonly Button downloadAllButton = new() { Text = "Descargar todos para comparar", AutoSize = true, Enabled = false };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage mediaTab = new("Medios disponibles");
    private readonly TabPage selectionTab = new("Elegir y guardar en la colección");
    private readonly Label selectionPlaceholder = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Consulta un juego en ScreenScraper para poder elegir qué medios guardar en la colección." };
    private ScreenScraperMediaSelectionPanel? selectionPanel;
    private readonly MediaPreviewPane mediaPreview = new() { Dock = DockStyle.Fill };
    private readonly TextBox mediaInfo = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, ScrollBars = ScrollBars.Vertical };
    private readonly Button openMediaButton = new() { Text = "Abrir con la aplicación predeterminada", AutoSize = true, Enabled = false };
    private readonly CheckBox videoSoundCheck = new() { Text = "Sonido del vídeo", AutoSize = true, Margin = new Padding(12, 6, 3, 0) };

    public bool CollectionMediaChanged { get; private set; }

    public ScreenScraperDialog(RomRow row, string root, TraceLog trace, bool preferNormalizedVideo)
    {
        this.row = row;
        this.root = root;
        this.preferNormalizedVideo = preferNormalizedVideo;
        this.trace = trace;
        credentials = ScreenScraperCredentialStore.Load();
        Text = $"ScreenScraper — {row.RomName}";
        Width = 1280; Height = 780; MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        searchButton.Click += async (_, _) => await SearchAsync();
        cancelButton.Click += (_, _) => { cts?.Cancel(); selectionPanel?.CancelSave(); };
        downloadAllButton.Click += async (_, _) => await DownloadMediaAsync(AllMediaRows().Where(r => !IsCached(r)).ToList());
        mediaGrid.SelectionChanged += (_, _) => ShowSelectedMedia();
        mediaGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || cancelButton.Enabled) return;
            var gridRow = mediaGrid.Rows[e.RowIndex];
            if (!IsCached(gridRow)) await DownloadMediaAsync([gridRow]);
        };
        openMediaButton.Click += (_, _) => OpenSelectedMedia();
        videoSoundCheck.CheckedChanged += (_, _) => mediaPreview.Sound = videoSoundCheck.Checked;
        Shown += async (_, _) =>
        {
            // Sin credenciales se ofrece registrarlas (o darse de alta) antes de seguir.
            credentials = ScreenScraperAccount.Ensure(this, trace, credentials);
            await LoadSystemsAsync(false);
            // La consulta se lanza sola al abrir; el botón «Refrescar consulta» la repite.
            if (!IsDisposed && credentials.HasDeveloper && systemCombo.SelectedItem is SsSystem) await SearchAsync(automatic: true);
        };
        tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        FormClosing += (_, e) =>
        {
            cts?.Cancel();
            // Mientras se copian medios a la colección no se cierra: se cancela y se espera a que termine el elemento en curso.
            if (selectionPanel?.IsSaving == true)
            {
                selectionPanel.CancelSave();
                statusLabel.Text = "Terminando el guardado en curso; cierra de nuevo cuando acabe.";
                e.Cancel = true;
            }
        };
        FormClosed += (_, _) => mediaPreview.StopVideo();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(systemInfo);
        top.Controls.Add(systemPrompt);
        top.Controls.Add(systemCombo);
        top.Controls.Add(searchButton);
        top.Controls.Add(cancelButton);

        var info = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft,
            Text = $"ROM: {row.RomName}   ·   Sistema local: {row.System}   ·   {row.Path}"
        };

        ConfigureGrid(metadataGrid);
        metadataGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Campo", Width = 220 });
        metadataGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Valor", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
        metadataGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        ConfigureGrid(mediaGrid);
        mediaGrid.AutoGenerateColumns = false;
        foreach (var (name, header, width) in new[] { ("type", "Tipo", 120), ("desc", "Descripción", 170), ("region", "Región", 55), ("format", "Formato", 55), ("size", "Tamaño", 75), ("resolution", "Resolución", 80), ("state", "Estado", 110), ("folder", "Carpeta ES-DE", 100), ("parent", "Variante / padre", 90) })
            mediaGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
        mediaGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        mediaGrid.MultiSelect = false;

        var mediaToolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        mediaToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mediaToolbar.Controls.Add(mediaSummary, 0, 0);
        mediaToolbar.Controls.Add(downloadAllButton, 1, 0);

        var viewer = new Panel { Dock = DockStyle.Fill };
        viewer.Controls.Add(mediaPreview);
        var viewerButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        viewerButtons.Controls.Add(openMediaButton);
        viewerButtons.Controls.Add(videoSoundCheck);
        var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        previewPanel.Controls.Add(viewer, 0, 0);
        previewPanel.Controls.Add(mediaInfo, 0, 1);
        previewPanel.Controls.Add(viewerButtons, 0, 2);

        var mediaSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
        mediaSplit.Panel1.Controls.Add(mediaGrid);
        mediaSplit.Panel2.Controls.Add(previewPanel);
        // La anchura del panel de vista previa se fija la primera vez que el control tiene tamaño real.
        bool splitSized = false;
        mediaSplit.SizeChanged += (_, _) =>
        {
            if (splitSized || mediaSplit.Width < 760) return;
            splitSized = true;
            mediaSplit.SplitterDistance = mediaSplit.Width - 440;
        };

        var mediaPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mediaPanel.Controls.Add(mediaToolbar, 0, 0);
        mediaPanel.Controls.Add(mediaSplit, 0, 1);

        var metaTab = new TabPage("Metadatos"); metaTab.Controls.Add(metadataGrid);
        mediaTab.Controls.Add(mediaPanel);
        selectionTab.Controls.Add(selectionPlaceholder);
        var rawTab = new TabPage("Respuesta JSON"); rawTab.Controls.Add(rawJson);
        tabs.TabPages.Add(metaTab); tabs.TabPages.Add(mediaTab); tabs.TabPages.Add(selectionTab); tabs.TabPages.Add(rawTab);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(info, 0, 1);
        layout.Controls.Add(tabs, 0, 2);
        layout.Controls.Add(progressBar, 0, 3);
        layout.Controls.Add(statusLabel, 0, 4);
        Controls.Add(layout);
        Controls.Add(new ScreenScraperQuotaStrip());
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.BackgroundColor = SystemColors.Window;
    }

    private void SetBusy(bool busy)
    {
        searchButton.Enabled = !busy;
        systemCombo.Enabled = !busy;
        cancelButton.Enabled = busy;
        downloadAllButton.Enabled = !busy && currentGame is not null && AllMediaRows().Any(r => !IsCached(r));
        if (!busy) progressBar.Value = 0;
    }

    private async Task LoadSystemsAsync(bool refresh)
    {
        if (!credentials.HasDeveloper)
        {
            statusLabel.Text = "Faltan las credenciales de ScreenScraper: se configuran en Opciones › Credenciales de ScreenScraper.";
            return;
        }
        try
        {
            SetBusy(true);
            statusLabel.Text = "Cargando lista de sistemas de ScreenScraper…";
            allSystems = await new ScreenScraperClient(credentials).GetSystemsAsync(refresh, CancellationToken.None);
            systemCombo.BeginUpdate();
            systemCombo.Items.Clear();
            foreach (var system in allSystems) systemCombo.Items.Add(system);
            systemCombo.EndUpdate();
            var match = SsSystemResolver.Find(allSystems, row.System);
            if (match is not null)
            {
                systemCombo.SelectedItem = match;
                systemInfo.Text = $"Sistema: {match.Name} (#{match.Id}), deducido de la carpeta «{row.System}»";
                statusLabel.Text = "Consultando ScreenScraper…";
            }
            else ShowSystemChooser($"No pude deducir el sistema de ScreenScraper a partir de la carpeta «{row.System}»; elígelo en la lista y pulsa «Refrescar consulta».");
        }
        catch (Exception ex)
        {
            statusLabel.Text = "No se pudo cargar la lista de sistemas: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void ShowSystemChooser(string message)
    {
        systemInfo.Visible = false;
        systemPrompt.Visible = true;
        systemCombo.Visible = true;
        statusLabel.Text = message;
    }

    private async Task SearchAsync(bool automatic = false)
    {
        if (!credentials.HasDeveloper) { statusLabel.Text = "Faltan las credenciales de ScreenScraper: se configuran en Opciones › Credenciales de ScreenScraper."; return; }
        if (systemCombo.SelectedItem is not SsSystem system)
        {
            MessageBox.Show(this, "Elige primero el sistema de ScreenScraper.", "ScreenScraper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(row.Path))
        {
            const string folderMessage = "Esta primera versión solo admite ROMs que sean un único archivo (no carpetas).";
            // Al abrir no se interrumpe con un cuadro de diálogo: basta con avisar en la barra de estado.
            if (automatic) statusLabel.Text = folderMessage;
            else MessageBox.Show(this, folderMessage, "ScreenScraper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetBusy(true);
        try
        {
            bool isZip = Path.GetExtension(row.Path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
            var progress = new Progress<double>(p => progressBar.Value = Math.Clamp((int)(p * 100), 0, 100));
            statusLabel.Text = "Calculando CRC32, MD5 y SHA1…";
            var hashes = await Task.Run(() => RomHasher.Compute(row.Path, isZip, progress, token), token);
            var client = new ScreenScraperClient(credentials);
            statusLabel.Text = $"Consultando ScreenScraper (hash de {hashes.Basis})…";
            SsLookupResult result;
            try { result = await client.LookupAsync(system.Id, row.RomName, hashes, token); }
            catch (ScreenScraperException ex) when (ex.StatusCode == 404 && isZip)
            {
                statusLabel.Text = "Sin coincidencia con el contenido del zip; reintentando con el archivo completo…";
                var whole = await Task.Run(() => RomHasher.Compute(row.Path, false, progress, token), token);
                result = await client.LookupAsync(system.Id, row.RomName, whole, token);
                hashes = whole;
            }
            trace.Add("screenscraper_lookup", $"system={row.System}; ss_system={system.Id}; rom={row.RomName}; hash={hashes.Basis}; found={result.Games.Count}");
            Render(result, hashes);
        }
        catch (OperationCanceledException) { statusLabel.Text = "Cancelado."; }
        catch (ScreenScraperException ex)
        {
            trace.Add("screenscraper_error", $"rom={row.RomName}; status={ex.StatusCode}; {ex.Message}");
            statusLabel.Text = ex.Message;
            // Sin coincidencia por hash, ScreenScraper busca por nombre dentro del sistema indicado: ahí sí importa elegir bien.
            if (ex.StatusCode == 404)
                ShowSystemChooser("ScreenScraper no reconoce esta ROM por su hash (hack, traducción, volcado raro…). Si la carpeta mezcla sistemas " +
                    "(p. ej. MSX/MSX2, PC Engine/PC Engine CD), elige el sistema correcto y vuelve a consultar: buscará por nombre en él.");
        }
        catch (Exception ex)
        {
            trace.Add("screenscraper_error", $"rom={row.RomName}; {ex}");
            statusLabel.Text = "Error: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void Render(SsLookupResult result, RomHashes hashes)
    {
        rawJson.Text = result.RawJson.Replace("\r\n", "\n").Replace("\n", "\r\n");
        metadataGrid.Rows.Clear();
        currentGame = null;
        mediaGrid.Rows.Clear();
        ClearPreview("");
        ResetSelectionTab();
        if (result.Games.Count == 0)
        {
            statusLabel.Text = "ScreenScraper respondió sin ningún juego asociado a esta ROM.";
            mediaSummary.Text = "";
            return;
        }
        var game = result.Games[0];
        void Add(string field, string value) { if (!string.IsNullOrWhiteSpace(value)) metadataGrid.Rows.Add(field, value); }

        Add("ID del juego", game.Id);
        Add("ID de la ROM", game.RomId);
        Add("Nombre", game.BestName);
        foreach (var n in game.Names) Add($"Nombre ({n.Key})", n.Text);
        Add("Sistema", game.SystemName);
        Add("Editor", game.Publisher);
        Add("Desarrollador", game.Developer);
        Add("Jugadores", game.Players);
        Add("Nota (sobre 20)", game.Rating);
        foreach (var d in game.Dates) Add($"Fecha ({d.Key})", d.Text);
        foreach (var (group, label) in new[] { ("genres", "Géneros"), ("modes", "Modos"), ("familles", "Familias"), ("numeros", "Serie / número"), ("themes", "Temas"), ("styles", "Estilos") })
        {
            if (!game.Groups.TryGetValue(group, out var items)) continue;
            Add(label, string.Join(" · ", items.Select(texts => SsPick.Text(texts, PreferredLanguages))));
        }
        foreach (var c in game.Classifications) Add($"Clasificación ({c.Key})", c.Text);
        foreach (var s in game.Synopsis) Add($"Sinopsis ({s.Key})", s.Text);
        foreach (var p in game.RomProperties) Add("ROM · " + p.Key, p.Text);

        currentGame = game;
        BuildSelectionTab(game);
        // Primero los medios del juego y al final los pictogramas de género, editor, etc.
        foreach (var m in game.Medias.OrderBy(m => m.IsGameMedia ? 0 : 1).ThenBy(m => m.Type, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Region, StringComparer.OrdinalIgnoreCase))
        {
            int index = mediaGrid.Rows.Add(m.Type, SsMediaInfo.Describe(m.Type), m.Region, m.Format, m.Size > 0 ? FormatBytes(m.Size) : "", "", "", SsMediaInfo.EsDeFolder(m.Type), m.Parent);
            var gridRow = mediaGrid.Rows[index];
            gridRow.Tag = m;
            if (IsCached(gridRow)) MarkDownloaded(gridRow, "En caché");
            else gridRow.Cells["state"].Value = "Pendiente";
        }

        // Los medios con parent distinto de "jeu" son pictogramas de género/editor/etc., no medios del juego.
        var gameMedias = game.Medias.Where(m => m.IsGameMedia).ToList();
        int images = gameMedias.Count(m => SsMediaInfo.Kind(m.Format) == "image");
        int videos = gameMedias.Count(m => SsMediaInfo.Kind(m.Format) == "video");
        int pdfs = gameMedias.Count(m => SsMediaInfo.Kind(m.Format) == "pdf");
        int types = gameMedias.Select(m => m.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int extra = game.Medias.Count - gameMedias.Count;
        mediaSummary.Text = $"  {gameMedias.Count} medios del juego de {types} tipos: {images} imágenes, {videos} vídeos, {pdfs} manuales/PDF, {gameMedias.Count - images - videos - pdfs} otros." + (extra > 0 ? $" (+{extra} pictogramas de género, editor, etc.)" : "");
        statusLabel.Text = $"Encontrado: {game.BestName}  ·  hash de {hashes.Basis}";
        ShowSelectedMedia();
    }

    private IEnumerable<DataGridViewRow> AllMediaRows() => mediaGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is SsMedia);

    private string CachePath(DataGridViewRow gridRow) => SsMediaCache.PathFor(currentGame?.Id ?? "", (SsMedia)gridRow.Tag!);

    private bool IsCached(DataGridViewRow gridRow) => currentGame is not null && gridRow.Tag is SsMedia media && SsMediaCache.IsCached(currentGame.Id, media);

    private void MarkDownloaded(DataGridViewRow gridRow, string state)
    {
        gridRow.Cells["state"].Value = state;
        gridRow.Cells["state"].Style.ForeColor = Color.DarkGreen;
        var media = (SsMedia)gridRow.Tag!;
        gridRow.Cells["resolution"].Value = ImageResolution(CachePath(gridRow), media);
        if (SsMediaInfo.Kind(media.Format) == "video") _ = FillVideoInfoAsync(gridRow, CachePath(gridRow));
    }

    private static async Task FillVideoInfoAsync(DataGridViewRow gridRow, string path)
    {
        string info = await VideoProbe.DescribeAsync(path);
        if (gridRow.DataGridView is not null) gridRow.Cells["resolution"].Value = info;
    }

    private static string ImageResolution(string path, SsMedia media)
    {
        if (!SsMediaInfo.IsImage(media.Format)) return "";
        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream, false, false);
            return $"{image.Width}×{image.Height}";
        }
        catch { return "?"; }
    }

    private async Task DownloadMediaAsync(IReadOnlyList<DataGridViewRow> rows)
    {
        if (currentGame is null || rows.Count == 0) return;
        var game = currentGame;
        long totalBytes = rows.Sum(r => ((SsMedia)r.Tag!).Size);
        if (rows.Count > 1 && MessageBox.Show(this,
                $"Se descargarán {rows.Count} medios ({FormatBytes(totalBytes)}) de uno en uno, que es lo que permite tu cuenta de ScreenScraper.\r\n\r\n" +
                $"Se guardan en una caché local para compararlos; todavía no se copia nada a la colección.\r\n\r\n{SsMediaCache.Root}\r\n\r\n¿Continuar?",
                "Descargar medios", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetBusy(true);
        var client = new ScreenScraperClient(credentials);
        int ok = 0, failed = 0;
        long doneBytes = 0;
        var clock = Stopwatch.StartNew();
        string stopReason = "";
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var gridRow = rows[i];
                var media = (SsMedia)gridRow.Tag!;
                if (IsCached(gridRow)) { MarkDownloaded(gridRow, "En caché"); ok++; continue; }
                gridRow.Cells["state"].Value = "Descargando…";
                gridRow.Cells["state"].Style.ForeColor = SystemColors.ControlText;
                long before = doneBytes;
                int position = i + 1;
                var progress = new Progress<long>(received =>
                {
                    double speed = (before + received) / Math.Max(clock.Elapsed.TotalSeconds, 0.5);
                    statusLabel.Text = $"Descargando {position}/{rows.Count}: {media.Label} · {FormatBytes(received)}" +
                        (media.Size > 0 ? $" de {FormatBytes(media.Size)}" : "") + $" · {FormatBytes((long)speed)}/s";
                    progressBar.Value = totalBytes > 0 ? Math.Clamp((int)((before + received) * 100 / totalBytes), 0, 100) : 0;
                });
                try
                {
                    await client.DownloadMediaAsync(media, CachePath(gridRow), progress, token);
                    MarkDownloaded(gridRow, "Descargado");
                    ok++;
                    if (gridRow.Selected) ShowSelectedMedia();
                }
                catch (ScreenScraperException ex)
                {
                    failed++;
                    gridRow.Cells["state"].Value = "Error";
                    gridRow.Cells["state"].Style.ForeColor = Color.Firebrick;
                    gridRow.Cells["state"].ToolTipText = ex.Message;
                    // Cuota, bloqueo o credenciales: no tiene sentido seguir pidiendo medios.
                    if (ex.StatusCode is 401 or 403 or 423 or 426 or 429 or 430 or 431) { stopReason = ex.Message; break; }
                }
                doneBytes += media.Size;
            }
            statusLabel.Text = stopReason.Length > 0
                ? $"Descarga detenida: {stopReason}"
                : $"Descarga terminada: {ok} correctos, {failed} con error" + (failed > 0 ? " (pasa el ratón sobre «Error» para ver el motivo)." : ".");
        }
        catch (OperationCanceledException)
        {
            foreach (var gridRow in rows.Where(r => !IsCached(r))) gridRow.Cells["state"].Value = "Pendiente";
            statusLabel.Text = $"Descarga cancelada: {ok} medios descargados.";
        }
        finally
        {
            trace.Add("screenscraper_media_preview", $"rom={row.RomName}; ss_game={game.Id}; requested={rows.Count}; ok={ok}; failed={failed}; seconds={clock.Elapsed.TotalSeconds:0}" + (stopReason.Length > 0 ? $"; stopped={stopReason}" : ""));
            SetBusy(false);
            ShowSelectedMedia();
        }
    }

    private void ShowSelectedMedia()
    {
        var gridRow = mediaGrid.SelectedRows.Count == 1 ? mediaGrid.SelectedRows[0] : null;
        if (gridRow?.Tag is not SsMedia media || currentGame is null) { ClearPreview(""); return; }

        string path = CachePath(gridRow);
        bool cached = IsCached(gridRow);
        string kind = SsMediaInfo.Kind(media.Format);
        string description = SsMediaInfo.Describe(media.Type);
        string folder = SsMediaInfo.EsDeFolder(media.Type);
        mediaInfo.Text =
            $"Tipo: {media.Type}{(description.Length > 0 ? " — " + description : "")}\r\n" +
            $"Región: {(media.Region.Length > 0 ? media.Region : "(todas)")}   ·   Formato: {media.Format}   ·   Tamaño: {(media.Size > 0 ? FormatBytes(media.Size) : "?")}\r\n" +
            (cached && kind == "image" ? $"Resolución: {ImageResolution(path, media)}\r\n" : "") +
            (cached && kind == "video" ? VideoInfoPending + "\r\n" : "") +
            $"Carpeta en ES-DE: {(folder.Length > 0 ? folder : "(sin equivalencia)")}\r\n" +
            $"Variante / padre: {media.Parent}\r\n" +
            (media.Md5.Length > 0 ? $"MD5: {media.Md5.ToLowerInvariant()}{(cached ? " (verificado)" : "")}\r\n" : "") +
            (cached ? $"Archivo en caché: {path}" : "");
        openMediaButton.Enabled = cached;

        if (!cached)
        {
            ClearPreview(cancelButton.Enabled ? "Aún no descargado." : "Aún no descargado.\r\nDoble clic en la fila para descargar solo este medio,\r\no «Descargar todos para comparar».", keepInfo: true);
            return;
        }
        mediaPreview.ShowFile(path);
        if (kind == "video") _ = ShowVideoInfoAsync(gridRow, path);
    }

    private const string VideoInfoPending = "Vídeo: leyendo resolución y duración…";

    private async Task ShowVideoInfoAsync(DataGridViewRow gridRow, string path)
    {
        string info = await VideoProbe.DescribeAsync(path);
        // Solo si el usuario sigue en la misma fila.
        if (mediaGrid.SelectedRows.Count != 1 || mediaGrid.SelectedRows[0] != gridRow) return;
        mediaInfo.Text = mediaInfo.Text.Replace(VideoInfoPending, "Vídeo: " + (info.Length > 0 ? info : "no se pudo leer"));
    }

    private void ClearPreview(string message, bool keepInfo = false)
    {
        mediaPreview.ShowMessage(message);
        if (!keepInfo) { mediaInfo.Text = ""; openMediaButton.Enabled = false; }
    }

    private void ResetSelectionTab()
    {
        if (selectionPanel is not null)
        {
            selectionTab.Controls.Remove(selectionPanel);
            selectionPanel.Dispose();
            selectionPanel = null;
        }
        selectionTab.Controls.Add(selectionPlaceholder);
    }

    private void BuildSelectionTab(SsGame game)
    {
        selectionTab.Controls.Remove(selectionPlaceholder);
        try
        {
            var panel = new ScreenScraperMediaSelectionPanel(game, row, root, () => credentials, trace, preferNormalizedVideo);
            panel.StatusChanged += text => statusLabel.Text = text;
            panel.ProgressChanged += percent => progressBar.Value = Math.Clamp(percent, 0, 100);
            panel.BusyChanged += SetBusy;
            panel.SaveFinished += () =>
            {
                CollectionMediaChanged |= panel.CollectionChanged;
                // El guardado puede haber descargado a la caché medios que aquí figuraban como pendientes.
                RefreshCacheStates();
            };
            selectionPanel = panel;
            selectionTab.Controls.Add(panel);
        }
        catch (Exception ex)
        {
            selectionPlaceholder.Text = "No se pudo preparar la selección de medios: " + ex.Message;
            selectionTab.Controls.Add(selectionPlaceholder);
        }
    }

    private void RefreshCacheStates()
    {
        foreach (var gridRow in AllMediaRows().Where(IsCached).Where(r => r.Cells["state"].Value as string == "Pendiente"))
            MarkDownloaded(gridRow, "Descargado");
    }

    // Al cambiar de pestaña se paran los vídeos de la que se abandona y se retoma la vista previa de la que se muestra.
    private void OnTabChanged()
    {
        if (tabs.SelectedTab != mediaTab) mediaPreview.StopVideo();
        if (tabs.SelectedTab != selectionTab) selectionPanel?.Deactivate();
        if (tabs.SelectedTab == mediaTab) { RefreshCacheStates(); ShowSelectedMedia(); }
        if (tabs.SelectedTab == selectionTab) selectionPanel?.Activate();
    }

    private void OpenSelectedMedia()
    {
        if (mediaGrid.SelectedRows.Count != 1 || !IsCached(mediaGrid.SelectedRows[0])) return;
        string path = CachePath(mediaGrid.SelectedRows[0]);
        mediaPreview.StopVideo();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo abrir el medio", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    internal static string FormatBytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double x = n; int i = 0;
        while (x >= 1024 && i < units.Length - 1) { x /= 1024; i++; }
        return i == 0 ? $"{n} B" : $"{x:0.##} {units[i]}";
    }
}
