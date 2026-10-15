namespace ESDERomCleaner;

// Categorización temporal de las ROMs que se van a añadir: pregunta a ScreenScraper (por hash del archivo) la categoría de cada una, solo para poder
// curarlas antes de añadirlas. No escribe nada en ningún gamelist: el resultado se queda en memoria (y se reutiliza si se vuelve a abrir el curado).
internal static class TemporaryCategorizer
{
    public sealed record Item(string Path, string Folder, string FileName);

    // Devuelve false si se cancela o no se puede (sin credenciales, error de conexión o de cuota). Lo ya categorizado queda en «genres».
    public static async Task<bool> RunAsync(IWin32Window owner, TraceLog trace, IReadOnlyList<Item> roms, Dictionary<string, string> genres)
    {
        var pending = roms.Where(r => !genres.ContainsKey(r.Path)).ToList();
        if (pending.Count == 0) return true;
        var credentials = ScreenScraperAccount.Ensure(owner, trace, ScreenScraperCredentialStore.Load());
        if (!credentials.HasDeveloper) { QuietMessage.Show(owner, "Curar y añadir", "Para curar hay que categorizar antes las ROMs con ScreenScraper, y faltan sus credenciales."); return false; }

        using var dialog = new ProgressDialog(pending.Count);
        using var cts = new CancellationTokenSource();
        dialog.CancelRequested += () => cts.Cancel();
        string? failure = null;
        int done = 0, known = 0, consecutiveErrors = 0;
        var gate = new object();

        var work = Task.Run(async () =>
        {
            try
            {
                var client = new ScreenScraperClient(credentials);
                var systems = await client.GetSystemsAsync(false, cts.Token);
                int degree = Math.Clamp(ScreenScraperQuota.MaxThreads, 1, 4);
                await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cts.Token }, async (item, token) =>
                {
                    string genre = "";
                    try
                    {
                        if (SsSystemResolver.Find(systems, item.Folder) is { } system)
                        {
                            var game = await FindAsync(client, system, item, token);
                            if (game is not null) genre = GamelistMetadata.From(game, []).Genre;
                        }
                        lock (gate) { consecutiveErrors = 0; if (genre.Length > 0) known++; }
                    }
                    catch (ScreenScraperException ex) when (ex.StatusCode == 404) { lock (gate) consecutiveErrors = 0; }
                    catch (ScreenScraperException ex)
                    {
                        // Cuota, bloqueo o credenciales: no tiene sentido seguir. Otros errores: se sigue, pero si no para de fallar se corta.
                        bool fatal = ex.StatusCode is 401 or 403 or 423 or 426 or 429 or 430 or 431;
                        lock (gate) { consecutiveErrors++; if (fatal || consecutiveErrors >= 5) failure ??= ex.Message; }
                        if (failure is not null) cts.Cancel();
                        return;
                    }
                    lock (gate) genres[item.Path] = genre;
                    int n = Interlocked.Increment(ref done);
                    dialog.Report(n, $"{item.FileName}");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure ??= ex.Message; }
            finally { dialog.Finish(); }
        });
        dialog.ShowDialog(owner);
        cts.Cancel();
        await work;
        trace.Add("temporary_categorization", $"roms={pending.Count}; hechas={done}; con_categoria={known}; error={failure ?? ""}");
        if (failure is not null) QuietMessage.Show(owner, "Curar y añadir", "No se pudo completar la categorización temporal: " + failure);
        return failure is null && done >= pending.Count;
    }

    // Por hash del archivo; si es un zip y no se reconoce, se prueba con el hash del zip entero (como en la descarga masiva).
    private static async Task<SsGame?> FindAsync(ScreenScraperClient client, SsSystem system, Item item, CancellationToken token)
    {
        string extension = Path.GetExtension(item.Path).ToLowerInvariant();
        bool packed = extension is ".zip" or ".7z" or ".rar";
        async Task<SsLookupResult> Lookup(RomHashes hashes)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return await client.LookupAsync(system.Id, item.FileName, hashes, token); }
                catch (ScreenScraperException ex) when (ex.StatusCode == 429 && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(3 * attempt), token); }
            }
        }
        var hashes = await Task.Run(() => RomHasher.Compute(item.Path, packed, null, token), token);
        try { return (await Lookup(hashes)).Games.FirstOrDefault(); }
        catch (ScreenScraperException ex) when (ex.StatusCode == 404 && extension == ".zip")
        {
            var whole = await Task.Run(() => RomHasher.Compute(item.Path, false, null, token), token);
            return (await Lookup(whole)).Games.FirstOrDefault();
        }
    }

    // Aviso de progreso con barra y botón «Cancelar».
    private sealed class ProgressDialog : LocalizedForm
    {
        private readonly ProgressBar bar = new() { Minimum = 0 };
        private readonly Label status = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, Tag = "noloc" };
        private readonly int total;
        private bool finished;
        public event Action? CancelRequested;

        public ProgressDialog(int total)
        {
            this.total = total;
            Text = "Categorización temporal";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            BackColor = RaUi.Body;
            ClientSize = new Size(560, 236);
            bar.Maximum = Math.Max(1, total);
            var banner = new RaHeader("Categorización temporal", "Consultando categorías en ScreenScraper.", 68, 48, logo: false);
            RaUi.AnimateIcon(banner.Icon, RaUi.ScrollWritingFrames());
            status.Location = new Point(24, 84); status.Size = new Size(512, 22); status.Text = $"0 / {total:N0}";
            bar.Location = new Point(24, 112); bar.Size = new Size(512, 14);
            var footer = RaUi.Footer();
            var cancel = RaUi.MakeButton("Cancelar", false); cancel.Location = new Point(436, 10);
            cancel.Click += (_, _) => { CancelRequested?.Invoke(); Close(); };
            footer.Controls.Add(cancel);
            // Abajo del todo: peticiones de hoy a ScreenScraper a la izquierda y CPU a la derecha.
            Controls.AddRange([banner, status, bar, footer, new CpuUsageStrip(withQuota: true)]);
            FormClosing += (_, _) => { if (!finished) CancelRequested?.Invoke(); };
        }

        public void Report(int done, string current)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { bar.Value = Math.Min(bar.Maximum, done); status.Text = $"{done:N0} / {total:N0} · {current}"; }); }
            catch (InvalidOperationException) { }
        }

        public void Finish()
        {
            finished = true;
            if (IsDisposed) return;
            try { if (IsHandleCreated) BeginInvoke(Close); else Shown += (_, _) => Close(); }
            catch (InvalidOperationException) { }
        }
    }
}
