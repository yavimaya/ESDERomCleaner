namespace ESDERomCleaner;

// Categorizar: descarga solo los metadatos (que traen la categoría) y los idiomas de las ROMs de los sistemas indicados.
// Es una ventana sencilla, solo con la barra de progreso y lo que se está haciendo, que se cierra sola al terminar.
internal sealed class CategorizeDialog : LocalizedForm
{
    private readonly IReadOnlyList<RomRow> roms;
    private readonly IReadOnlyList<string> systems;
    private readonly string root;
    private readonly TraceLog trace;
    private readonly bool gamelistBackups;
    private readonly Label statusLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Label summaryLabel = new() { AutoSize = false, TextAlign = ContentAlignment.TopLeft, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100 };
    private readonly Button cancel = new() { Text = "Cancelar", Size = new Size(110, 32) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly CancellationTokenSource cts = new();
    private BulkMediaRunner? runner;
    private int plannedWork;
    private readonly List<BulkIssue> issueList = [];

    public bool MetadataChanged { get; private set; }
    public string Result { get; private set; } = "";

    public CategorizeDialog(IReadOnlyList<RomRow> roms, IReadOnlyList<string> systems, string root, TraceLog trace, bool gamelistBackups)
    {
        this.roms = roms; this.systems = systems; this.root = root; this.trace = trace; this.gamelistBackups = gamelistBackups;
        Text = "Categorizar";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(600, 272);
        BackColor = RaUi.Body;
        // Mismo aspecto que el resto de formularios nuevos: cabecera oscura con el papiro (la pluma escribe mientras se categoriza), pie con el botón y barra de CPU.
        var banner = new RaHeader("Categorizar", "Descarga de ScreenScraper la categoría (y los idiomas) de las ROMs de los sistemas elegidos.", 84, 48, logo: false); // más alta: el subtítulo ocupa dos líneas en algunos idiomas
        RaUi.AnimateIcon(banner.Icon, RaUi.ScrollWritingFrames());
        statusLabel.Location = new Point(24, 100); statusLabel.Size = new Size(552, 22);
        bar.Location = new Point(24, 128); bar.Size = new Size(552, 14);
        summaryLabel.Location = new Point(24, 152); summaryLabel.Size = new Size(552, 38);
        var footer = RaUi.Footer();
        RaUi.StyleButton(cancel, false);
        cancel.Location = new Point(600 - 24 - cancel.Width, 10);
        footer.Controls.Add(cancel);
        // Abajo del todo: peticiones de hoy a ScreenScraper a la izquierda y CPU a la derecha.
        Controls.AddRange([banner, statusLabel, bar, summaryLabel, footer, new CpuUsageStrip(withQuota: true)]);
        CancelButton = cancel;
        cancel.Click += (_, _) => { if (!CancelConfirm.Ask(this, "la categorización de las ROMs")) return; cts.Cancel(); cancel.Enabled = false; statusLabel.Text = "Cancelando…"; };
        timer.Tick += (_, _) => RefreshProgress();
        Shown += async (_, _) => await RunAsync();
    }

    private int Percent(BulkSummary s) => plannedWork > 0 ? Math.Clamp(Math.Max(0, s.RomsFinished - s.AlreadyComplete) * 100 / plannedWork, 0, 100) : 0;

    private void RefreshProgress()
    {
        if (runner is null) return;
        var s = runner.Summary;
        bar.Value = Percent(s);
        Text = $"Categorizar — {bar.Value} %";
        summaryLabel.Text = $"ROMs {Math.Max(0, s.RomsFinished - s.AlreadyComplete):N0}/{plannedWork:N0} · categorizadas {Math.Max(s.MetadataFetched, s.MetadataUpdated):N0}" +
            (s.MetadataFailed > 0 ? $" ({s.MetadataFailed:N0} fallidas)" : "") + (s.LanguagesStored > 0 ? $" · idiomas {s.LanguagesStored:N0}" : "") +
            (s.NotFound > 0 ? $" · no encontradas {s.NotFound:N0}" : "") + (s.MetadataUpdated == 0 ? "\r\n(las categorías se escriben en el gamelist.xml de cada sistema al terminarlo)" : "");
    }

    private async Task RunAsync()
    {
        try
        {
            var credentials = ScreenScraperCredentialStore.Load();
            if (!credentials.HasDeveloper)
            {
                Result = "Faltan las credenciales de ScreenScraper (Scrapeo › Credenciales de ScreenScraper…).";
                QuietMessage.Show(this, "Categorizar", Result);
                return;
            }
            statusLabel.Text = "Conectando con ScreenScraper…";
            bar.Style = ProgressBarStyle.Marquee;
            var client = new ScreenScraperClient(credentials);
            var ssSystems = await client.GetSystemsAsync(false, cts.Token);
            try { await client.RefreshUserAsync(cts.Token); } catch (OperationCanceledException) { throw; } catch { /* se sigue con lo último que se leyó */ }

            // Metadatos (con la categoría) e idiomas, sin carpetas de medios. Los hilos los marca la membresía.
            var settings = BulkMediaSettings.Load(true);
            var options = new BulkMediaOptions(root, roms, ssSystems, systems.ToList(), [], true, BulkOverwrite.No, false, settings.SearchByName,
                Math.Max(1, ScreenScraperQuota.MaxThreads), true, gamelistBackups, true) { CategoryOnly = true };

            statusLabel.Text = "Calculando el trabajo pendiente…";
            var plan = await Task.Run(() => BulkMediaRunner.Plan(options), cts.Token);
            plannedWork = plan.RomsWithWork;
            if (plannedWork == 0)
            {
                Result = "No hay ROMs que categorizar en esos sistemas.";
                return;
            }

            bar.Style = ProgressBarStyle.Blocks;
            var status = new Progress<BulkStatus>(s => statusLabel.Text = s.Text);
            runner = new BulkMediaRunner(options, credentials, trace, status, new Progress<BulkIssue>(i => { lock (issueList) issueList.Add(i); }));
            timer.Start();
            await runner.RunAsync(cts.Token);
            MetadataChanged = runner.MetadataChanged;
            var summary = runner.Summary;
            Result = summary.StoppedReason.Length > 0 ? "⚠ Detenida antes de terminar: los resultados son parciales. " + summary.StoppedReason
                : $"Categorizadas {summary.MetadataUpdated:N0} ROM(s)" +
                  (summary.NotFound > 0 ? $"; {summary.NotFound:N0} no encontradas" : "") + (summary.MetadataFailed > 0 ? $"; {summary.MetadataFailed:N0} fallidas" : "") + ".";
            // Si alguna ROM no se ha encontrado en ScreenScraper, se muestra el resumen de lo hecho; si se han encontrado todas, no.
            if (summary.StoppedReason.Length == 0 && (summary.NotFound > 0 || !summary.NoGenre.IsEmpty) && !cts.IsCancellationRequested)
            {
                await Task.Delay(300); // deja que lleguen las últimas incidencias
                string[] missing;
                lock (issueList) missing = issueList.Select(i => $"{i.System}: {i.Rom}").Concat(summary.NoGenre).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                using var report = new CategorizeSummaryDialog(Math.Max(0, summary.MetadataUpdated - summary.NoGenre.Count), summary.NoGenre.Count, summary.NotFound, summary.MetadataFailed, missing);
                report.ShowDialog(this);
            }
        }
        catch (OperationCanceledException) { MetadataChanged |= runner?.MetadataChanged ?? false; Result = "⚠ Detenida antes de terminar: los resultados son parciales."; }
        catch (Exception ex) { trace.Add("categorize_error", ex.ToString()); Result = "No se pudo categorizar: " + ex.Message; QuietMessage.Show(this, "Categorizar", Result); }
        finally
        {
            timer.Stop();
            if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}

// Resumen de la categorización cuando a alguna ROM no se le encontró categoría en ScreenScraper: qué se hizo y cuáles siguen sin ella. Con el formato de los
// formularios nuevos: cabecera oscura, tarjetas con las cifras y una tabla con las ROMs (icono del sistema, sistema y nombre).
internal sealed class CategorizeSummaryDialog : LocalizedForm
{
    private static readonly Color Ok = Color.FromArgb(34, 130, 70), Warn = Color.FromArgb(214, 100, 0), Bad = Color.FromArgb(170, 40, 40);

    public CategorizeSummaryDialog(int categorized, int noGenre, int notFound, int failed, string[] missing)
    {
        Text = "Categorizar";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        const int width = 660, headerHeight = 68, footerHeight = 52, tileHeight = 88;
        ClientSize = new Size(width, 520);

        var banner = new RaHeader("Categorizar", "Resultado de la categorización", headerHeight, 48, logo: false);
        banner.Icon.Image = RaUi.ScrollIcon();

        // Tarjetas con las cifras (solo las que no son cero, salvo las categorizadas).
        var tiles = new List<(string Number, string Name, Color Color)> { (categorized.ToString("N0"), "Categorizadas", Ok) };
        if (noGenre > 0) tiles.Add((noGenre.ToString("N0"), "Sin categoría en ScreenScraper", Warn));
        if (notFound > 0) tiles.Add((notFound.ToString("N0"), "No encontradas en ScreenScraper", Warn));
        if (failed > 0) tiles.Add((failed.ToString("N0"), "Fallidas", Bad));
        int innerWidth = width - 48, gap = 10, tileWidth = (innerWidth - gap * (tiles.Count - 1)) / tiles.Count;
        var tilePanel = new Panel { Location = new Point(24, headerHeight + 16), Size = new Size(innerWidth, tileHeight), BackColor = RaUi.Body };
        for (int i = 0; i < tiles.Count; i++)
        {
            var card = RaUi.Card(new Rectangle(i * (tileWidth + gap), 0, tileWidth, tileHeight));
            card.Controls.Add(new Label { Text = tiles[i].Number, Tag = "noloc", Location = new Point(0, 8), Size = new Size(tileWidth, 38), TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), ForeColor = tiles[i].Color });
            card.Controls.Add(new Label { Text = tiles[i].Name, Location = new Point(4, 50), Size = new Size(tileWidth - 8, 34), TextAlign = ContentAlignment.TopCenter, Font = new Font(Font, FontStyle.Bold) });
            tilePanel.Controls.Add(card);
        }

        var caption = new Label { Text = "ROMs que siguen sin categoría:", Location = new Point(24, headerHeight + 16 + tileHeight + 14), Size = new Size(innerWidth, 22), Font = new Font(Font, FontStyle.Bold) };
        int gridTop = caption.Bottom + 4;
        int gridHeight = ClientSize.Height - footerHeight - gridTop - 14;

        // Los nombres de las ROMs no se traducen (Tag «noloc»); cada línea llega como «sistema: ROM».
        var grid = new DataGridView
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, Tag = "noloc", AutoGenerateColumns = false
        };
        RaUi.StyleGrid(grid, 24, 26, selectionVisible: false);
        grid.Columns.Add(new DataGridViewImageColumn { Width = 30, SortMode = DataGridViewColumnSortMode.NotSortable, ImageLayout = DataGridViewImageCellLayout.Zoom, DefaultCellStyle = new DataGridViewCellStyle { NullValue = null } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sistema", Width = 130, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ROM", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (string entry in missing.Take(200))
        {
            int split = entry.IndexOf(": ", StringComparison.Ordinal);
            string system = split > 0 ? entry[..split] : "", rom = split > 0 ? entry[(split + 2)..] : entry;
            grid.Rows.Add(system.Length > 0 ? PixelSystemIcons.Get(system, 16) : null, system, rom);
        }
        if (missing.Length > 200) grid.Rows.Add(null, "", $"… (+{missing.Length - 200:N0})");
        var frame = RaUi.Frame(grid, new Rectangle(24, gridTop, innerWidth, gridHeight));

        var footer = RaUi.Footer(footerHeight);
        var ok = RaUi.MakeButton("Aceptar", true, 110);
        ok.DialogResult = DialogResult.OK; ok.Location = new Point(width - 20 - ok.Width, 10);
        footer.Controls.Add(ok);
        AcceptButton = ok; CancelButton = ok;
        Controls.Add(tilePanel); Controls.Add(caption); Controls.Add(frame); Controls.Add(footer); Controls.Add(banner);
    }
}
