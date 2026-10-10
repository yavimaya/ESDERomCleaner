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

    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "screenscraper_quota.json");

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

// Barra inferior con los contadores de la API; se actualiza sola con cada llamada.
internal sealed class ScreenScraperQuotaStrip : StatusStrip
{
    private readonly ToolStripStatusLabel label = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public ScreenScraperQuotaStrip()
    {
        Dock = DockStyle.Bottom;
        SizingGrip = false;
        ShowItemToolTips = true;
        label.ToolTipText = "El contador del servidor se actualiza en cada consulta de juego. Las descargas de medios no lo informan: se muestran aparte como llamadas posteriores.";
        Items.Add(label);
        UpdateText();
        ScreenScraperQuota.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(UpdateText); } catch (InvalidOperationException) { } return; }
        UpdateText();
    }

    private void UpdateText() => label.Text = ScreenScraperQuota.Describe();

    protected override void Dispose(bool disposing)
    {
        if (disposing) ScreenScraperQuota.Changed -= OnChanged;
        base.Dispose(disposing);
    }
}
