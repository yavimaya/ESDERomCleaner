using System.Text;

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

    public ScreenScraperDialog(RomRow row, TraceLog trace)
    {
        this.row = row;
        this.trace = trace;
        credentials = ScreenScraperCredentialStore.Load();
        Text = $"ScreenScraper — {row.RomName}";
        Width = 1100; Height = 720; MinimumSize = new Size(800, 520);
        StartPosition = FormStartPosition.CenterParent;
        BuildLayout();
        credentialsButton.Click += (_, _) => EditCredentials();
        searchButton.Click += async (_, _) => await SearchAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();
        Load += async (_, _) => await LoadSystemsAsync(false);
        FormClosing += (_, _) => cts?.Cancel();
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
        foreach (var (header, width) in new[] { ("Tipo", 130), ("Descripción", 200), ("Región", 70), ("Formato", 70), ("Tamaño", 90), ("Carpeta equivalente en ES-DE", 190), ("Variante / padre", 120) })
            mediaGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Automatic });
        mediaGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var mediaPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        mediaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        mediaPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mediaPanel.Controls.Add(mediaSummary, 0, 0);
        mediaPanel.Controls.Add(mediaGrid, 0, 1);

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
        mediaGrid.Rows.Clear();
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

        foreach (var m in game.Medias.OrderBy(m => m.Type, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Region, StringComparer.OrdinalIgnoreCase))
            mediaGrid.Rows.Add(m.Type, SsMediaInfo.Describe(m.Type), m.Region, m.Format, m.Size > 0 ? FormatBytes(m.Size) : "", SsMediaInfo.EsDeFolder(m.Type), m.Parent);

        int images = game.Medias.Count(m => SsMediaInfo.Kind(m.Format) == "image");
        int videos = game.Medias.Count(m => SsMediaInfo.Kind(m.Format) == "video");
        int pdfs = game.Medias.Count(m => SsMediaInfo.Kind(m.Format) == "pdf");
        int types = game.Medias.Select(m => m.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        mediaSummary.Text = $"  {game.Medias.Count} medios de {types} tipos: {images} imágenes, {videos} vídeos, {pdfs} manuales/PDF, {game.Medias.Count - images - videos - pdfs} otros.";
        statusLabel.Text = $"Encontrado: {game.BestName}  ·  hash de {hashes.Basis}  ·  {result.Quota}";
    }

    private static string FormatBytes(long n)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double x = n; int i = 0;
        while (x >= 1024 && i < units.Length - 1) { x /= 1024; i++; }
        return i == 0 ? $"{n} B" : $"{x:0.##} {units[i]}";
    }
}
