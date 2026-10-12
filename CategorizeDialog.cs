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
    private readonly Label statusLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label summaryLabel = new() { AutoSize = false, TextAlign = ContentAlignment.TopLeft, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 100 };
    private readonly Button cancel = new() { Text = "Cancelar", Size = new Size(90, 28) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly CancellationTokenSource cts = new();
    private BulkMediaRunner? runner;
    private int plannedWork;

    public bool MetadataChanged { get; private set; }
    public string Result { get; private set; } = "";

    public CategorizeDialog(IReadOnlyList<RomRow> roms, IReadOnlyList<string> systems, string root, TraceLog trace, bool gamelistBackups)
    {
        this.roms = roms; this.systems = systems; this.root = root; this.trace = trace; this.gamelistBackups = gamelistBackups;
        Text = "Categorizar";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(520, 150);
        statusLabel.Location = new Point(12, 12); statusLabel.Size = new Size(496, 22);
        bar.Location = new Point(12, 40); bar.Size = new Size(496, 20);
        summaryLabel.Location = new Point(12, 68); summaryLabel.Size = new Size(496, 36);
        cancel.Location = new Point(418, 112);
        Controls.AddRange([statusLabel, bar, summaryLabel, cancel]);
        CancelButton = cancel;
        cancel.Click += (_, _) => { cts.Cancel(); cancel.Enabled = false; statusLabel.Text = "Cancelando…"; };
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
                Math.Max(1, ScreenScraperQuota.MaxThreads), true, gamelistBackups, true);

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
            runner = new BulkMediaRunner(options, credentials, trace, status, null);
            timer.Start();
            await runner.RunAsync(cts.Token);
            MetadataChanged = runner.MetadataChanged;
            var summary = runner.Summary;
            Result = summary.StoppedReason.Length > 0 ? summary.StoppedReason
                : $"Categorizadas {summary.MetadataUpdated:N0} ROM(s)" +
                  (summary.NotFound > 0 ? $"; {summary.NotFound:N0} no encontradas" : "") + (summary.MetadataFailed > 0 ? $"; {summary.MetadataFailed:N0} fallidas" : "") + ".";
        }
        catch (OperationCanceledException) { MetadataChanged |= runner?.MetadataChanged ?? false; Result = "Categorización cancelada."; }
        catch (Exception ex) { trace.Add("categorize_error", ex.ToString()); Result = "No se pudo categorizar: " + ex.Message; QuietMessage.Show(this, "Categorizar", Result); }
        finally
        {
            timer.Stop();
            if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}
