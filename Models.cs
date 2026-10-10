namespace ESDERomCleaner;

internal enum RomStatus { Ok, Ambiguous }
internal sealed record GameEntry(string Path, string Name, string Genre, string Description, string RelativeKey, string NameKey);
internal sealed record AccessoryFile(string System, string Path, long Size, string RelativePath, string MatchKey);
internal sealed record RomRow(string System, string Path, string RomName, long Size, string GameName, string Genre, string Description, RomStatus Status, string Gamelist, string GamelistPath)
{
    public string Display => Status == RomStatus.Ambiguous ? $"{GameName} [AMBIGUO]" : (string.IsNullOrWhiteSpace(GameName) ? RomName : GameName);
}
internal sealed record ScanProgress(string Message, int Current, int Total);
internal sealed record ScanResult(List<RomRow> Rows, List<AccessoryFile> Accessories, Dictionary<string,int> SystemFileCounts, int Gamelists, int SystemCount, int FileCount, Dictionary<string,int> ExtensionCounts, string CachePath);
internal sealed record TraceRecord(DateTime Utc, string Action, string Details);
