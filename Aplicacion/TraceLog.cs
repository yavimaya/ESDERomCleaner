using System.Text.Json;

namespace ESDERomCleaner;

internal sealed class TraceLog
{
    private static string path => Path.Combine(AppPaths.Data, "traceability.jsonl");
    private static readonly object Gate = new();
    public void Add(string action, string details)
    {
        // La descarga masiva escribe desde varios hilos a la vez.
        lock (Gate)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, JsonSerializer.Serialize(new TraceRecord(DateTime.UtcNow, action, details)) + Environment.NewLine); }
            catch { }
        }
    }
    public List<TraceRecord> ReadRecords(int max)
    {
        var result = new List<TraceRecord>();
        try
        {
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path).TakeLast(max))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<TraceRecord>(line);
                    if (record is not null) result.Add(record with { Action = record.Action ?? "", Details = record.Details ?? "" });
                }
                catch (JsonException) { }
            }
        }
        catch { }
        return result;
    }
    public string Read()
    {
        try { return File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(500)) : "Aún no hay trazas."; }
        catch (Exception e) { return $"No se pudo leer: {e.Message}"; }
    }
}
