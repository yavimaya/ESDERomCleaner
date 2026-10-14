using System.Text.RegularExpressions;

namespace ESDERomCleaner;

// Pestaña «Aproximaciones por nombre»: cuando ScreenScraper no reconoce la ROM ni por hash ni por el nombre exacto, se buscan juegos
// parecidos por texto y se muestran con su miximage, nombre y descripción. Al elegir uno se puede descargar todo (imágenes, vídeo y
// metadatos) o solo lo que se marque; el trabajo lo hace el mismo motor de la descarga masiva con ese juego forzado.
internal sealed partial class ScreenScraperDialog
{
    private const int MaxCandidates = 20;
    private const int CandidateRowHeight = 120;

    private readonly bool gamelistBackups;
    private readonly TabPage candidatesTab = new("Aproximaciones por nombre");
    private readonly TextBox queryBox = new() { Width = 420 };
    private readonly Button searchCandidatesButton = new() { Text = "Buscar aproximaciones", AutoSize = true };
    private readonly DataGridView candidatesGrid = new();
    private readonly Button useAllButton = new() { Text = "Descargar todo (imágenes, vídeo y metadatos)", AutoSize = true, Enabled = false };
    private readonly Button usePickButton = new() { Text = "Elegir qué descargar…", AutoSize = true, Enabled = false };
    private readonly Label candidatesHint = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 0), ForeColor = SystemColors.GrayText };
    private CancellationTokenSource? candidatesCts;
    private bool candidatesBusy;

    public bool MetadataChanged { get; private set; }

    private void InitCandidates()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(6, 4, 6, 0) };
        bar.Controls.Add(new Label { Text = "Nombre a buscar:", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        bar.Controls.Add(queryBox);
        bar.Controls.Add(searchCandidatesButton);

        candidatesGrid.Dock = DockStyle.Fill;
        candidatesGrid.ReadOnly = true;
        candidatesGrid.AllowUserToAddRows = false;
        candidatesGrid.AllowUserToDeleteRows = false;
        candidatesGrid.AllowUserToResizeRows = false;
        candidatesGrid.RowHeadersVisible = false;
        candidatesGrid.MultiSelect = false;
        candidatesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        RaUi.StyleGrid(candidatesGrid, CandidateRowHeight, 28);
        candidatesGrid.RowTemplate.Height = CandidateRowHeight;
        candidatesGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        candidatesGrid.Columns.Add(new DataGridViewImageColumn { Name = "mix", HeaderText = "Miximage", Width = 170, ImageLayout = DataGridViewImageCellLayout.Zoom });
        candidatesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name", HeaderText = "Juego", Width = 260,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Font = new Font(Font, FontStyle.Bold), Alignment = DataGridViewContentAlignment.TopLeft }
        });
        candidatesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "desc", HeaderText = "Descripción", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, Alignment = DataGridViewContentAlignment.TopLeft }
        });
        candidatesGrid.SelectionChanged += (_, _) => UpdateCandidateButtons();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(6, 6, 6, 0) };
        buttons.Controls.Add(useAllButton);
        buttons.Controls.Add(usePickButton);
        buttons.Controls.Add(candidatesHint);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(candidatesGrid, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        candidatesTab.Controls.Add(layout);

        searchCandidatesButton.Click += async (_, _) => await SearchCandidatesAsync();
        queryBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchCandidatesAsync(); } };
        useAllButton.Click += async (_, _) => await UseCandidateAsync(pick: false);
        usePickButton.Click += async (_, _) => await UseCandidateAsync(pick: true);
        FormClosed += (_, _) => candidatesCts?.Cancel();
    }

    private void SetCandidatesBusy(bool busy)
    {
        candidatesBusy = busy;
        queryBox.Enabled = searchCandidatesButton.Enabled = !busy;
        UpdateCandidateButtons();
    }

    private void UpdateCandidateButtons() =>
        useAllButton.Enabled = usePickButton.Enabled = !candidatesBusy && SelectedCandidate() is not null;

    private SsGame? SelectedCandidate() =>
        candidatesGrid.SelectedRows.Count == 1 ? candidatesGrid.SelectedRows[0].Tag as SsGame : null;

    // La pestaña aparece (y se lanza la primera búsqueda) cuando ScreenScraper no reconoce la ROM.
    private void ShowCandidates()
    {
        if (!tabs.TabPages.Contains(candidatesTab)) tabs.TabPages.Insert(1, candidatesTab);
        tabs.SelectedTab = candidatesTab;
        if (queryBox.Text.Length == 0) queryBox.Text = DefaultQuery();
        if (candidatesGrid.Rows.Count == 0 && !candidatesBusy && systemCombo.SelectedItem is SsSystem) _ = SearchCandidatesAsync();
    }

    // Nombre del juego sin etiquetas entre paréntesis o corchetes ni guiones bajos.
    private string DefaultQuery()
    {
        string name = !string.IsNullOrWhiteSpace(row.GameName) ? row.GameName : Path.GetFileNameWithoutExtension(row.RomName);
        name = Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Replace('_', ' ');
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    // Variantes que se prueban por orden hasta que alguna devuelve juegos: el texto, con «&» por «and» y sin las últimas palabras.
    private static IEnumerable<string> QueryVariants(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> All()
        {
            yield return text;
            if (text.Contains('&')) yield return text.Replace("&", "and");
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int n = words.Length - 1; n >= 1; n--)
            {
                string shorter = string.Join(' ', words.Take(n));
                if (shorter.Length >= 4) yield return shorter;
            }
        }
        foreach (string variant in All())
            if (variant.Trim().Length > 0 && seen.Add(variant.Trim())) yield return variant.Trim();
    }

    private async Task SearchCandidatesAsync()
    {
        if (candidatesBusy) return;
        if (!credentials.HasDeveloper) { statusLabel.Text = "Faltan las credenciales de ScreenScraper: se configuran en Opciones › Credenciales de ScreenScraper."; return; }
        if (systemCombo.SelectedItem is not SsSystem system) { statusLabel.Text = "Elige primero el sistema de ScreenScraper."; return; }
        string text = queryBox.Text.Trim();
        if (text.Length < 2) { statusLabel.Text = "Escribe al menos dos letras del nombre del juego."; return; }

        candidatesCts?.Cancel();
        candidatesCts = new CancellationTokenSource();
        var token = candidatesCts.Token;
        SetBusy(true);
        candidatesGrid.Rows.Clear();
        candidatesHint.Text = "";
        var client = new ScreenScraperClient(credentials);
        try
        {
            List<SsGame> found = [];
            string used = text;
            foreach (string query in QueryVariants(text))
            {
                token.ThrowIfCancellationRequested();
                statusLabel.Text = $"Buscando «{query}» en {system.Name}…";
                found = await client.SearchCandidatesAsync(system.Id, query, token);
                used = query;
                if (found.Count > 0) break;
            }
            trace.Add("screenscraper_candidates", $"system={row.System}; ss_system={system.Id}; rom={row.RomName}; query={used}; found={found.Count}");
            if (found.Count == 0)
            {
                statusLabel.Text = $"ScreenScraper no devuelve ningún juego parecido a «{text}» en {system.Name}. Prueba con otro nombre, o con otro sistema.";
                return;
            }
            foreach (var game in found.Take(MaxCandidates))
            {
                string description = SsPick.Text(game.Synopsis, PreferredLanguages);
                if (description.Length > 700) description = description[..700] + "…";
                int i = candidatesGrid.Rows.Add((object)null!, game.BestName + (game.SystemName.Length > 0 ? "\r\n(" + game.SystemName + ")" : ""), description);
                candidatesGrid.Rows[i].Tag = game;
            }
            statusLabel.Text = $"{Math.Min(found.Count, MaxCandidates)} juegos parecidos a «{used}»: elige el que corresponde a esta ROM.";
            candidatesHint.Text = used.Equals(text, StringComparison.OrdinalIgnoreCase) ? "" : $"(la búsqueda de «{text}» no dio resultados; se usó «{used}»)";
            if (candidatesGrid.Rows.Count > 0) candidatesGrid.Rows[0].Selected = true;
            SetBusy(false);
            await LoadThumbnailsAsync(client, token);
        }
        catch (OperationCanceledException) { statusLabel.Text = "Cancelado."; }
        catch (ScreenScraperException ex) { statusLabel.Text = ex.Message; trace.Add("screenscraper_error", $"candidates; rom={row.RomName}; status={ex.StatusCode}; {ex.Message}"); }
        catch (Exception ex) { statusLabel.Text = "Error: " + ex.Message; trace.Add("screenscraper_error", $"candidates; rom={row.RomName}; {ex}"); }
        finally { if (!IsDisposed && candidatesBusy) SetBusy(false); }
    }

    private static readonly string[] ThumbnailTypes = ["mixrbv2", "mixrbv1", "ss", "box-2D", "sstitle"];

    // La miximage de cada candidato; si no tiene, la captura o la carátula. Se baja a la caché de medios, de una en una.
    private async Task LoadThumbnailsAsync(ScreenScraperClient client, CancellationToken token)
    {
        foreach (DataGridViewRow gridRow in candidatesGrid.Rows.Cast<DataGridViewRow>().ToList())
        {
            if (token.IsCancellationRequested || IsDisposed) return;
            if (gridRow.Tag is not SsGame game) continue;
            var media = PickThumbnail(game);
            if (media is null) continue;
            try
            {
                string path = SsMediaCache.PathFor(game.Id, media);
                if (!SsMediaCache.IsCached(game.Id, media)) await client.DownloadMediaAsync(media, path, null, token);
                if (IsDisposed || gridRow.DataGridView is null) return;
                using var image = Image.FromFile(path);
                gridRow.Cells["mix"].Value = new Bitmap(image);
            }
            catch (OperationCanceledException) { return; }
            catch { /* sin imagen para este candidato: se queda en blanco */ }
        }
    }

    private SsMedia? PickThumbnail(SsGame game)
    {
        var regions = SsMediaSelector.RomRegions(game, row.RomName).Concat(["wor", "eu", "us", "jp"]).ToList();
        foreach (string type in ThumbnailTypes)
        {
            var ofType = game.Medias.Where(m => m.IsGameMedia && m.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && SsMediaInfo.IsImage(m.Format)).ToList();
            if (ofType.Count == 0) continue;
            foreach (string region in regions)
                if (ofType.FirstOrDefault(m => m.Region.Equals(region, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            return ofType.FirstOrDefault(m => m.Region.Length == 0) ?? ofType[0];
        }
        return null;
    }

    // Descarga para esta ROM el juego elegido: todo (imágenes, vídeo, metadatos e idiomas) o lo que se marque en la ventana de selección.
    private async Task UseCandidateAsync(bool pick)
    {
        if (SelectedCandidate() is not { } candidate) return;
        if (systemCombo.SelectedItem is not SsSystem system) return;
        DownloadSelection selection = DownloadSelection.Everything(preferNormalizedVideo);
        if (pick)
        {
            using var dialog = new SsPickDownloadsDialog(preferNormalizedVideo);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;
            selection = dialog.Result;
        }
        if (selection.Folders.Count == 0 && !selection.Metadata && !selection.Languages) { statusLabel.Text = "No se marcó nada que descargar."; return; }

        string what = string.Join(", ", EsDeMediaInstaller.Folders.Where(f => selection.Folders.Contains(f.Folder)).Select(f => f.Description)
            .Concat(selection.Metadata ? ["metadatos"] : []).Concat(selection.Languages ? ["idiomas"] : []));
        if (!QuietMessage.Confirm(this, "Descargar el juego elegido",
                $"ROM: {row.RomName}\r\nJuego elegido: {candidate.BestName}\r\n\r\nSe descargará: {what}.\r\n\r\n" +
                "Si la ROM ya tiene esos medios se sustituyen (con copia de seguridad en la carpeta media_backups) y los metadatos se reescriben en su gamelist.xml.",
                "Descargar")) return;

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetBusy(true);
        var client = new ScreenScraperClient(credentials);
        try
        {
            statusLabel.Text = $"Leyendo «{candidate.BestName}» completo en ScreenScraper…";
            var lookup = await client.LookupByGameIdAsync(system.Id, candidate.Id, token);
            if (lookup.Games.Count == 0) { statusLabel.Text = "ScreenScraper no devolvió los datos de ese juego."; return; }
            var full = lookup.Games[0];
            Render(lookup, new RomHashes("", "", "", 0, ChosenByHand));
            var folders = EsDeMediaInstaller.Folders.Where(f => selection.Folders.Contains(f.Folder)).ToList();
            var options = new BulkMediaOptions(root, [row], allSystems, [row.System], folders, selection.NormalizedVideo, BulkOverwrite.Always, true, false, 1,
                selection.Metadata, gamelistBackups, selection.Languages) { ForcedGame = _ => full };
            var status = new Progress<BulkStatus>(s => statusLabel.Text = s.Text);
            var runner = new BulkMediaRunner(options, credentials, trace, status, null);
            trace.Add("screenscraper_candidate_chosen", $"system={row.System}; rom={row.RomName}; ss_game={full.Id}; folders={string.Join(",", selection.Folders)}; metadata={selection.Metadata}; languages={selection.Languages}");
            await runner.RunAsync(token);
            CollectionMediaChanged |= runner.CollectionChanged;
            MetadataChanged |= runner.MetadataChanged;
            var s2 = runner.Summary;
            statusLabel.Text = s2.StoppedReason.Length > 0 ? s2.StoppedReason
                : $"Hecho con «{full.BestName}»: {s2.FilesSaved} archivo(s) guardados" + (s2.FilesReplaced > 0 ? $" ({s2.FilesReplaced} sustituyen a otros)" : "")
                  + (selection.Metadata ? $", metadatos {(s2.MetadataUpdated > 0 ? "escritos" : "no escritos")}" : "")
                  + (s2.FoldersUnavailable > 0 ? $"; {s2.FoldersUnavailable} medio(s) que ScreenScraper no tiene" : "")
                  + (s2.WithErrors > 0 ? $"; {s2.WithErrors} error(es)" : "") + ".";
        }
        catch (OperationCanceledException) { statusLabel.Text = "Cancelado."; }
        catch (ScreenScraperException ex) { statusLabel.Text = ex.Message; trace.Add("screenscraper_error", $"candidate; rom={row.RomName}; status={ex.StatusCode}; {ex.Message}"); }
        catch (Exception ex) { statusLabel.Text = "Error: " + ex.Message; trace.Add("screenscraper_error", $"candidate; rom={row.RomName}; {ex}"); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private const string ChosenByHand = "elegido a mano";
}

// Qué descargar del juego elegido.
internal sealed record DownloadSelection(HashSet<string> Folders, bool Metadata, bool Languages, bool NormalizedVideo)
{
    public static DownloadSelection Everything(bool normalizedVideo) =>
        new(EsDeMediaInstaller.Folders.Select(f => f.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase), true, true, normalizedVideo);
}

// Casillas de lo que se quiere descargar: metadatos, idiomas y cada carpeta de medios de ES-DE.
internal sealed class SsPickDownloadsDialog : LocalizedForm
{
    private readonly CheckBox metadata = new() { Text = "Metadatos (nombre, descripción, categoría… en el gamelist.xml)", Checked = true, AutoSize = true };
    private readonly CheckBox languages = new() { Text = "Idiomas (para el curado)", Checked = true, AutoSize = true };
    private readonly Dictionary<string, CheckBox> folders = new();
    public DownloadSelection? Result { get; private set; }

    private readonly RadioButton normalizedRadio = new() { Text = "Normalizado (menor tamaño)", AutoSize = true };
    private readonly RadioButton originalRadio = new() { Text = "Original", AutoSize = true };

    public SsPickDownloadsDialog(bool normalizedVideo)
    {
        Text = "Elegir qué descargar";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        var list = new FlowLayoutPanel { Location = new Point(14, 12), Size = new Size(400, 24 * (EsDeMediaInstaller.Folders.Length + 3) + 8), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        list.Controls.Add(metadata);
        list.Controls.Add(languages);
        foreach (var folder in EsDeMediaInstaller.Folders)
        {
            // El fan art y los manuales no se descargan salvo que se marquen.
            var box = new CheckBox { Text = $"{folder.Description} ({folder.Folder})", Checked = folder.Folder is not ("fanart" or "manuals"), AutoSize = true };
            folders[folder.Folder] = box;
            list.Controls.Add(box);
            if (folder.Folder == "videos")
            {
                // Tipo de vídeo, debajo de la casilla del vídeo y sangrado; solo se puede elegir si el vídeo está marcado.
                var kind = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 0, 0, 0) };
                normalizedRadio.Checked = normalizedVideo; originalRadio.Checked = !normalizedVideo;
                kind.Controls.Add(normalizedRadio); kind.Controls.Add(originalRadio);
                box.CheckedChanged += (_, _) => normalizedRadio.Enabled = originalRadio.Enabled = box.Checked;
                list.Controls.Add(kind);
            }
        }
        int y = list.Bottom + 6;
        var all = new Button { Text = "Todo", Location = new Point(14, y), Size = new Size(70, 26) };
        var none = new Button { Text = "Nada", Location = new Point(90, y), Size = new Size(70, 26) };
        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);
        var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, Location = new Point(250, y + 36), Size = new Size(80, 28) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(336, y + 36), Size = new Size(80, 28) };
        ok.Click += (_, _) => Result = new DownloadSelection(folders.Where(p => p.Value.Checked).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), metadata.Checked, languages.Checked, normalizedRadio.Checked);
        ClientSize = new Size(430, y + 76);
        Controls.AddRange([list, all, none, ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void SetAll(bool value)
    {
        metadata.Checked = languages.Checked = value;
        foreach (var box in folders.Values) box.Checked = value;
    }
}
