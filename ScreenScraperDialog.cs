using System.Diagnostics;
using System.Text;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace ESDERomCleaner;

internal sealed class ScreenScraperDialog : Form
{
    private static readonly string[] PreferredLanguages = ["es", "en", "fr", "de", "it", "pt"];

    private readonly RomRow row;
    private readonly TraceLog trace;
    private ScreenScraperCredentials credentials;
    private List<SsSystem> allSystems = new();
    private CancellationTokenSource? cts;

    private readonly ComboBox systemCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly Button credentialsButton = new() { Text = "Credenciales…", AutoSize = true };
    private readonly Button searchButton = new() { Text = "Consultar ScreenScraper", AutoSize = true };
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
    private readonly PictureBox mediaPicture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 32) };
    private readonly VideoView mediaVideo = new() { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
    private readonly Label mediaPlaceholder = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(32, 32, 32), Visible = false };
    private readonly TextBox mediaInfo = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, ScrollBars = ScrollBars.Vertical };
    private readonly Button openMediaButton = new() { Text = "Abrir con la aplicación predeterminada", AutoSize = true, Enabled = false };
    private readonly CheckBox videoSoundCheck = new() { Text = "Sonido del vídeo", AutoSize = true, Margin = new Padding(12, 6, 3, 0) };
    private LibVLC? libVlc;
    private MediaPlayer? mediaPlayer;
    private Media? currentVideo;

    public ScreenScraperDialog(RomRow row, TraceLog trace)
    {
        this.row = row;
        this.trace = trace;
        credentials = ScreenScraperCredentialStore.Load();
        Text = $"ScreenScraper — {row.RomName}";
        Width = 1280; Height = 780; MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        credentialsButton.Click += (_, _) => EditCredentials();
        searchButton.Click += async (_, _) => await SearchAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();
        downloadAllButton.Click += async (_, _) => await DownloadMediaAsync(AllMediaRows().Where(r => !IsCached(r)).ToList());
        mediaGrid.SelectionChanged += (_, _) => ShowSelectedMedia();
        mediaGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || cancelButton.Enabled) return;
            var gridRow = mediaGrid.Rows[e.RowIndex];
            if (!IsCached(gridRow)) await DownloadMediaAsync([gridRow]);
        };
        openMediaButton.Click += (_, _) => OpenSelectedMedia();
        videoSoundCheck.CheckedChanged += (_, _) => { if (mediaPlayer is not null) mediaPlayer.Volume = videoSoundCheck.Checked ? 100 : 0; };
        Load += async (_, _) => await LoadSystemsAsync(false);
        FormClosing += (_, _) => cts?.Cancel();
        FormClosed += (_, _) => DisposeVideo();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "Sistema en ScreenScraper:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        top.Controls.Add(systemCombo);
        top.Controls.Add(searchButton);
        top.Controls.Add(cancelButton);
        top.Controls.Add(credentialsButton);

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
        viewer.Controls.Add(mediaVideo);
        viewer.Controls.Add(mediaPlaceholder);
        viewer.Controls.Add(mediaPicture);
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

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var metaTab = new TabPage("Metadatos"); metaTab.Controls.Add(metadataGrid);
        var mediaTab = new TabPage("Medios disponibles"); mediaTab.Controls.Add(mediaPanel);
        var rawTab = new TabPage("Respuesta JSON"); rawTab.Controls.Add(rawJson);
        tabs.TabPages.Add(metaTab); tabs.TabPages.Add(mediaTab); tabs.TabPages.Add(rawTab);

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
        credentialsButton.Enabled = !busy;
        systemCombo.Enabled = !busy;
        cancelButton.Enabled = busy;
        downloadAllButton.Enabled = !busy && currentGame is not null && AllMediaRows().Any(r => !IsCached(r));
        if (!busy) progressBar.Value = 0;
    }

    private void EditCredentials()
    {
        using var dialog = new ScreenScraperCredentialsDialog(credentials);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
        credentials = dialog.Result;
        try
        {
            ScreenScraperCredentialStore.Save(credentials);
            trace.Add("screenscraper_credentials_saved", $"user={(credentials.UserId.Length > 0 ? credentials.UserId : "(anónimo)")}");
            statusLabel.Text = "Credenciales guardadas.";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "No se pudieron guardar las credenciales: " + ex.Message;
        }
        _ = LoadSystemsAsync(true);
    }

    private async Task LoadSystemsAsync(bool refresh)
    {
        if (!credentials.HasDeveloper)
        {
            statusLabel.Text = "Configura tus credenciales de desarrollador (botón «Credenciales…») para empezar.";
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
            var match = FindSystem(row.System);
            if (match is not null) systemCombo.SelectedItem = match;
            statusLabel.Text = match is not null
                ? $"Sistema detectado: {match.Name}. Pulsa «Consultar ScreenScraper»."
                : $"No pude deducir el sistema a partir de «{row.System}»; elígelo en la lista.";
        }
        catch (Exception ex)
        {
            statusLabel.Text = "No se pudo cargar la lista de sistemas: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private SsSystem? FindSystem(string folder)
    {
        string key = folder.Trim().ToLowerInvariant();
        return allSystems.FirstOrDefault(s => s.Retropie == key)
            ?? allSystems.FirstOrDefault(s => s.Recalbox == key)
            ?? allSystems.FirstOrDefault(s => s.CommonNames.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task SearchAsync()
    {
        if (!credentials.HasDeveloper) { EditCredentials(); if (!credentials.HasDeveloper) return; }
        if (systemCombo.SelectedItem is not SsSystem system)
        {
            MessageBox.Show(this, "Elige primero el sistema de ScreenScraper.", "ScreenScraper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(row.Path))
        {
            MessageBox.Show(this, "Esta primera versión solo admite ROMs que sean un único archivo (no carpetas).", "ScreenScraper", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        statusLabel.Text = $"Encontrado: {game.BestName}  ·  hash de {hashes.Basis}  ·  {result.Quota}";
        ShowSelectedMedia();
    }

    private IEnumerable<DataGridViewRow> AllMediaRows() => mediaGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is SsMedia);

    private string CachePath(DataGridViewRow gridRow) => SsMediaCache.PathFor(currentGame?.Id ?? "", (SsMedia)gridRow.Tag!);

    private bool IsCached(DataGridViewRow gridRow) => currentGame is not null && gridRow.Tag is SsMedia media && SsMediaCache.IsCached(currentGame.Id, media);

    private void MarkDownloaded(DataGridViewRow gridRow, string state)
    {
        gridRow.Cells["state"].Value = state;
        gridRow.Cells["state"].Style.ForeColor = Color.DarkGreen;
        gridRow.Cells["resolution"].Value = ImageResolution(CachePath(gridRow), (SsMedia)gridRow.Tag!);
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
        if (kind == "image")
        {
            StopVideo();
            try
            {
                using var stream = File.OpenRead(path);
                using var image = Image.FromStream(stream);
                var old = mediaPicture.Image;
                mediaPicture.Image = new Bitmap(image);
                old?.Dispose();
                mediaPicture.Visible = true;
                mediaPlaceholder.Visible = false;
            }
            catch { ClearPreview("No se puede previsualizar esta imagen.\r\nPrueba con «Abrir con la aplicación predeterminada».", keepInfo: true); }
        }
        else if (kind == "video") PlayVideo(path);
        else ClearPreview(kind == "pdf" ? "Manual en PDF.\r\nPulsa «Abrir con la aplicación predeterminada»." : "Sin vista previa para este formato.", keepInfo: true);
    }

    private void ClearPreview(string message, bool keepInfo = false)
    {
        StopVideo();
        var old = mediaPicture.Image;
        mediaPicture.Image = null;
        old?.Dispose();
        mediaPicture.Visible = message.Length == 0;
        mediaPlaceholder.Text = message;
        mediaPlaceholder.Visible = message.Length > 0;
        if (!keepInfo) { mediaInfo.Text = ""; openMediaButton.Enabled = false; }
    }

    private void PlayVideo(string path)
    {
        StopVideo();
        try
        {
            if (mediaPlayer is null)
            {
                Core.Initialize();
                libVlc = new LibVLC("--no-video-title-show", "--no-osd");
                mediaPlayer = new MediaPlayer(libVlc);
                mediaVideo.MediaPlayer = mediaPlayer;
            }
            currentVideo = new Media(libVlc!, new Uri(Path.GetFullPath(path)));
            currentVideo.AddOption(":input-repeat=65535");
            mediaPlayer.Volume = videoSoundCheck.Checked ? 100 : 0;
            mediaPicture.Visible = false;
            mediaPlaceholder.Visible = false;
            mediaVideo.Visible = true;
            mediaVideo.BringToFront();
            if (!mediaPlayer.Play(currentVideo)) throw new InvalidOperationException();
        }
        catch { ClearPreview("No se pudo reproducir el vídeo.\r\nPrueba con «Abrir con la aplicación predeterminada».", keepInfo: true); }
    }

    private void StopVideo()
    {
        try { if (mediaPlayer?.IsPlaying == true) mediaPlayer.Stop(); } catch { }
        currentVideo?.Dispose();
        currentVideo = null;
        mediaVideo.Visible = false;
    }

    private void DisposeVideo()
    {
        StopVideo();
        mediaPlayer?.Dispose();
        mediaPlayer = null;
        libVlc?.Dispose();
        libVlc = null;
        mediaPicture.Image?.Dispose();
    }

    private void OpenSelectedMedia()
    {
        if (mediaGrid.SelectedRows.Count != 1 || !IsCached(mediaGrid.SelectedRows[0])) return;
        string path = CachePath(mediaGrid.SelectedRows[0]);
        StopVideo();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "No se pudo abrir el medio", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string FormatBytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double x = n; int i = 0;
        while (x >= 1024 && i < units.Length - 1) { x /= 1024; i++; }
        return i == 0 ? $"{n} B" : $"{x:0.##} {units[i]}";
    }
}
