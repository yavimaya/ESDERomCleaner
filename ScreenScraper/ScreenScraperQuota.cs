using System.Globalization;
using System.Text.Json;

namespace ESDERomCleaner;

internal sealed record SsQuotaSnapshot(DateTime SyncedAt, int RequestsToday, int MaxPerDay, int KoToday, int MaxKoPerDay, int MaxThreads, int MaxSpeedKBps);

// Contadores de uso de la API. ScreenScraper solo informa de ellos (bloque ssuser) en las consultas de juego;
// las descargas de medios no lo hacen, así que se cuentan aquí las llamadas hechas desde la última lectura.
internal static class ScreenScraperQuota
{
    private static readonly object Gate = new();
    private static SsQuotaSnapshot? snapshot = Load();
    private static int callsSinceSync, failedSinceSync;

    public static event Action? Changed;

    // Hilos simultáneos que permite la cuenta según la última lectura del servidor (0 si aún no se ha leído).
    public static int MaxThreads { get { lock (Gate) return snapshot?.MaxThreads ?? 0; } }

    private static string StorePath => Path.Combine(AppPaths.Data, "screenscraper_quota.json");

    public static void RecordCall(bool ok)
    {
        lock (Gate)
        {
            callsSinceSync++;
            if (!ok) failedSinceSync++;
        }
        Changed?.Invoke();
    }

    public static void Sync(JsonElement user)
    {
        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && int.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), out int n) ? n : 0;
        var fresh = new SsQuotaSnapshot(DateTime.Now, Int(user, "requeststoday"), Int(user, "maxrequestsperday"), Int(user, "requestskotoday"),
            Int(user, "maxrequestskoperday"), Int(user, "maxthreads"), Int(user, "maxdownloadspeed"));
        lock (Gate)
        {
            snapshot = fresh;
            callsSinceSync = 0;
            failedSinceSync = 0;
        }
        Save(fresh);
        Changed?.Invoke();
    }

    public static string Describe()
    {
        SsQuotaSnapshot? s;
        int calls, failed;
        lock (Gate) { s = snapshot; calls = callsSinceSync; failed = failedSinceSync; }
        var es = CultureInfo.GetCultureInfo("es-ES");
        if (s is null)
            return calls == 0 ? "API ScreenScraper: sin datos de uso todavía (se leen en la primera consulta)." : $"API ScreenScraper: {calls} llamadas en esta sesión ({failed} fallidas); el contador del servidor se lee en la próxima consulta.";
        string when = s.SyncedAt.Date == DateTime.Today ? s.SyncedAt.ToString("HH:mm", es) : s.SyncedAt.ToString("dd/MM HH:mm", es);
        string pending = calls > 0 ? $"  (+{calls} llamadas después{(failed > 0 ? $", {failed} fallidas" : "")})" : "";
        return string.Create(es, $"Peticiones hoy: {s.RequestsToday:N0} / {s.MaxPerDay:N0}{pending}   ·   Fallidas hoy: {s.KoToday:N0} / {s.MaxKoPerDay:N0}   ·   Hilos: {s.MaxThreads}   ·   Velocidad máx.: {s.MaxSpeedKBps} KB/s   ·   Leído del servidor: {when}");
    }

    // Solo las peticiones de hoy y el límite diario, contando también las llamadas hechas desde la última lectura del servidor
    // (así sube con cada petición, sin esperar a la siguiente consulta de juego).
    public static string TodayText()
    {
        SsQuotaSnapshot? s;
        int calls;
        lock (Gate) { s = snapshot; calls = callsSinceSync; }
        var es = CultureInfo.GetCultureInfo("es-ES");
        if (s is null) return calls == 0 ? "Peticiones hoy: sin datos" : $"Peticiones hoy: {calls.ToString("N0", es)}";
        return $"Peticiones hoy: {(s.RequestsToday + calls).ToString("N0", es)} / {s.MaxPerDay.ToString("N0", es)}";
    }

    // Los mismos datos que Describe(), uno por concepto, para mostrarlos en casillas de ancho fijo.
    public static QuotaParts Parts()
    {
        SsQuotaSnapshot? s;
        int calls, failed;
        lock (Gate) { s = snapshot; calls = callsSinceSync; failed = failedSinceSync; }
        var es = CultureInfo.GetCultureInfo("es-ES");
        string pending = calls > 0 ? $"+{calls.ToString("N0", es)} llamadas" + (failed > 0 ? $" ({failed.ToString("N0", es)} fallidas)" : "") : "";
        if (s is null) return new QuotaParts("Peticiones hoy: sin datos", pending, "", "", "", "");
        string when = s.SyncedAt.Date == DateTime.Today ? s.SyncedAt.ToString("HH:mm", es) : s.SyncedAt.ToString("dd/MM HH:mm", es);
        return new QuotaParts(
            $"Peticiones hoy: {s.RequestsToday.ToString("N0", es)} / {s.MaxPerDay.ToString("N0", es)}",
            pending,
            $"Fallidas hoy: {s.KoToday.ToString("N0", es)} / {s.MaxKoPerDay.ToString("N0", es)}",
            $"Hilos: {s.MaxThreads}",
            $"Velocidad máx.: {s.MaxSpeedKBps} KB/s",
            $"Leído del servidor: {when}");
    }

    private static SsQuotaSnapshot? Load()
    {
        try { return File.Exists(StorePath) ? JsonSerializer.Deserialize<SsQuotaSnapshot>(File.ReadAllText(StorePath)) : null; }
        catch { return null; }
    }

    private static void Save(SsQuotaSnapshot value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            string temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch { }
    }
}

internal sealed record QuotaParts(string Requests, string Pending, string Failed, string Threads, string Speed, string Synced);

// Barra inferior con los contadores de la API; se actualiza sola con cada llamada. Cada concepto tiene su propia casilla con un
// ancho fijo (el del mayor valor posible), así los textos no se desplazan al cambiar las cifras.
internal sealed class ScreenScraperQuotaStrip : StatusStrip
{
    private readonly ToolStripStatusLabel requests = NewLabel("Peticiones de hoy según ScreenScraper y límite diario de tu cuenta.");
    private readonly ToolStripStatusLabel pending = NewLabel("Llamadas hechas desde la última lectura del servidor. Las descargas de medios no actualizan el contador del servidor: hasta la siguiente consulta de juego se cuentan aquí.");
    private readonly ToolStripStatusLabel failed = NewLabel("Peticiones fallidas de hoy y límite diario de tu cuenta.");
    private readonly ToolStripStatusLabel threads = NewLabel("Peticiones simultáneas que permite tu membresía.");
    private readonly ToolStripStatusLabel speed = NewLabel("Velocidad máxima de descarga de tu membresía.");
    private readonly ToolStripStatusLabel synced = NewLabel("Hora de la última lectura del servidor.");
    private readonly ToolStripStatusLabel cpu = NewLabel("Uso de CPU del proceso (sobre el total de la máquina).");
    private readonly System.Windows.Forms.Timer cpuTimer = new() { Interval = 1000 };
    private TimeSpan lastCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
    private long lastTick = Environment.TickCount64;
    private readonly ToolStripStatusLabel filler = new() { Spring = true };
    private readonly ToolStripStatusLabel timing = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Visible = false };

    // Texto de la derecha de la barra (tiempo transcurrido y restante en la descarga masiva); vacío = oculto.
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string TimeText { set { timing.Text = value; timing.Visible = value.Length > 0; } }

    // showPending = false quita la casilla de llamadas pendientes de leer.
    public ScreenScraperQuotaStrip(bool showPending = true)
    {
        Dock = DockStyle.Bottom;
        SizingGrip = false;
        ShowItemToolTips = true;
        Items.AddRange(new ToolStripItem[] { requests, pending, failed, threads, speed, synced, cpu, filler, timing });
        cpuTimer.Tick += (_, _) => UpdateCpu();
        cpuTimer.Start();
        pending.Visible = showPending;
        ApplyWidths();
        UpdateText();
        ScreenScraperQuota.Changed += OnChanged;
    }

    private static ToolStripStatusLabel NewLabel(string tooltip) => new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = tooltip,
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched
    };

    // Ancho de cada casilla = el del texto más largo que puede llegar a tener.
    private void ApplyWidths()
    {
        void Fix(ToolStripStatusLabel label, string longest) => label.Width = TextRenderer.MeasureText(longest, Font).Width + 18;
        Fix(requests, "Peticiones hoy: 000.000 / 000.000");
        Fix(pending, "+00.000 llamadas (0.000 fallidas)");
        Fix(failed, "Fallidas hoy: 00.000 / 00.000");
        Fix(threads, "Hilos: 00");
        Fix(speed, "Velocidad máx.: 000000 KB/s");
        Fix(synced, "Leído del servidor: 00/00 00:00");
        Fix(cpu, "CPU: 100 %");
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyWidths();
    }

    // Porcentaje de CPU del proceso desde la última lectura, sobre todos los núcleos.
    private void UpdateCpu()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var total = process.TotalProcessorTime;
            long now = Environment.TickCount64;
            double elapsedMs = now - lastTick;
            if (elapsedMs <= 0) return;
            double percent = (total - lastCpu).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100;
            lastCpu = total; lastTick = now;
            cpu.Text = $"CPU: {Math.Clamp(percent, 0, 100):0} %";
        }
        catch { cpu.Text = ""; }
    }

    private void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(UpdateText); } catch (InvalidOperationException) { } return; }
        UpdateText();
    }

    private void UpdateText()
    {
        var parts = ScreenScraperQuota.Parts();
        requests.Text = parts.Requests;
        pending.Text = parts.Pending;
        failed.Text = parts.Failed;
        threads.Text = parts.Threads;
        speed.Text = parts.Speed;
        synced.Text = parts.Synced;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ScreenScraperQuota.Changed -= OnChanged; cpuTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
