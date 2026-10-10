namespace ESDERomCleaner;

// Reglas para elegir, de entre los medios que ofrece ScreenScraper, los de una carpeta de ES-DE. Las comparten la pestaña de
// selección manual y la descarga masiva, para que ambas propongan siempre lo mismo.
internal static class SsMediaSelector
{
    // Regiones de reserva cuando ScreenScraper no tiene ese tipo de medio para la región de la ROM, por orden de prioridad.
    public static readonly string[] FallbackRegions = ["wor", "eu", "us", "jp"];

    public static List<string> RomRegions(SsGame game, string romName) =>
        game.RomRegions.Count > 0 ? game.RomRegions.ToList() : SsRegions.FromFileName(romName);

    // Orden de preferencia de los tipos de ScreenScraper de una carpeta. En vídeos manda la opción «normalizado»; las
    // miximages pueden fijarse a Recalbox v2.
    public static string[] TypeOrder(EsDeMediaFolder folder, bool preferNormalizedVideo, bool miximageV2Only)
    {
        if (folder.Folder == "videos") return preferNormalizedVideo ? ["video-normalized", "video"] : ["video", "video-normalized"];
        if (folder.Folder == "miximages" && miximageV2Only) return ["mixrbv2"];
        return folder.SsTypes;
    }

    // Candidatos ordenados de mejor a peor. Primero los de la región de la ROM (o sin región); si no hay ninguno, los de las
    // regiones de reserva y Fallback = true.
    public static (List<SsMedia> Candidates, bool Fallback) Candidates(SsGame game, IReadOnlyList<string> romRegions, EsDeMediaFolder folder, string[] typeOrder)
    {
        int TypeRank(SsMedia m) => Array.FindIndex(typeOrder, t => t.Equals(m.Type, StringComparison.OrdinalIgnoreCase));
        int RomRegionRank(string region)
        {
            for (int i = 0; i < romRegions.Count; i++)
                if (romRegions[i].Equals(region, StringComparison.OrdinalIgnoreCase)) return i;
            return romRegions.Count;
        }
        int FallbackRank(string region)
        {
            int index = Array.FindIndex(FallbackRegions, r => r.Equals(region, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : FallbackRegions.Length;
        }

        var usable = game.Medias.Where(m => EsDeMediaInstaller.Accepts(folder, m) && TypeRank(m) >= 0).ToList();
        var primary = usable
            .Where(m => m.Region.Length == 0 || romRegions.Count == 0 || romRegions.Contains(m.Region, StringComparer.OrdinalIgnoreCase))
            .OrderBy(TypeRank)
            .ThenBy(m => RomRegionRank(m.Region))
            .ToList();
        if (primary.Count > 0 || romRegions.Count == 0) return (primary, false);

        var fallback = usable
            .Where(m => FallbackRank(m.Region) < FallbackRegions.Length)
            .OrderBy(m => FallbackRank(m.Region))
            .ThenBy(TypeRank)
            .ToList();
        return (fallback, fallback.Count > 0);
    }
}

internal static class SsSystemResolver
{
    // Deduce el sistema de ScreenScraper a partir del nombre de la carpeta de ES-DE (nombres de RetroPie, Recalbox o comunes).
    public static SsSystem? Find(IEnumerable<SsSystem> systems, string folder)
    {
        string key = folder.Trim().ToLowerInvariant();
        var list = systems as IList<SsSystem> ?? systems.ToList();
        // Varios sistemas de ScreenScraper comparten el nombre de RetroPie (con «snes» también lo usan Satellaview, Sufami Turbo
        // y los hacks de Mario World). Gana el que coincide en RetroPie y en Recalbox y, en caso de empate, el de menor
        // identificador: el sistema principal se dio de alta antes que sus variantes.
        return PickLowestId(list.Where(s => s.Retropie == key && s.Recalbox == key))
            ?? PickLowestId(list.Where(s => s.Retropie == key))
            ?? PickLowestId(list.Where(s => s.Recalbox == key))
            ?? PickLowestId(list.Where(s => s.CommonNames.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase))));
    }

    private static SsSystem? PickLowestId(IEnumerable<SsSystem> candidates) => candidates.OrderBy(s => s.Id).FirstOrDefault();

}
