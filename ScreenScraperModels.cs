namespace ESDERomCleaner;

internal sealed record ScreenScraperCredentials(string DevId, string DevPassword, string SoftName, string UserId, string UserPassword)
{
    public static ScreenScraperCredentials Empty { get; } = new("", "", "ESDERomCleaner", "", "");
    public bool HasDeveloper => !string.IsNullOrWhiteSpace(DevId) && !string.IsNullOrWhiteSpace(DevPassword) && !string.IsNullOrWhiteSpace(SoftName);
}

internal sealed record SsSystem(int Id, string Name, string Retropie, string Recalbox, string[] CommonNames)
{
    public override string ToString() => $"{Name} (#{Id})";
}

internal sealed record SsText(string Key, string Text);

// Género de ScreenScraper: tiene un padre (ParentId «0» si es una categoría principal) y nombres por idioma.
internal sealed record SsGenre(string Id, string ParentId, bool Principal, List<SsText> Names);

// Url contiene las credenciales en claro: nunca se muestra ni se escribe en trazas.
internal sealed record SsMedia(string Type, string Parent, string Region, string Format, long Size, string Md5, string Url)
{
    public bool IsGameMedia => Parent.Length == 0 || Parent.Equals("jeu", StringComparison.OrdinalIgnoreCase);
    public string Label => Region.Length > 0 ? $"{Type} ({Region})" : Type;
}

internal sealed class SsGame
{
    public string Id { get; set; } = "";
    public string RomId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Developer { get; set; } = "";
    public string Players { get; set; } = "";
    public string Rating { get; set; } = "";
    public List<SsText> Names { get; } = new();
    public List<SsText> Synopsis { get; } = new();
    public List<SsText> Dates { get; } = new();
    public List<SsText> Classifications { get; } = new();
    public Dictionary<string, List<List<SsText>>> Groups { get; } = new();
    public List<SsMedia> Medias { get; } = new();
    public List<SsText> RomProperties { get; } = new();
    public List<SsGenre> Genres { get; } = new();
    // Regiones de la ROM identificada por hash (códigos cortos de ScreenScraper: us, eu, jp, wor...).
    public List<string> RomRegions { get; } = new();
    // Idiomas de la ROM identificada (códigos cortos: en, fr, ja…). Vacío si ScreenScraper no los indica.
    public List<string> RomLanguages { get; } = new();

    public string BestName
    {
        get
        {
            string regional = SsPick.Text(Names, "eu", "wor", "us", "ss", "jp");
            return regional.Length > 0 ? regional : Name;
        }
    }
}

internal static class SsRegions
{
    private static readonly Dictionary<string, string> FileNameTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["usa"] = "us", ["us"] = "us", ["europe"] = "eu", ["eu"] = "eu", ["japan"] = "jp", ["jp"] = "jp", ["world"] = "wor",
        ["spain"] = "sp", ["france"] = "fr", ["germany"] = "de", ["uk"] = "uk", ["united kingdom"] = "uk", ["italy"] = "it",
        ["brazil"] = "br", ["korea"] = "kr", ["australia"] = "au", ["netherlands"] = "nl", ["sweden"] = "se", ["china"] = "cn",
        ["taiwan"] = "tw", ["asia"] = "asi", ["canada"] = "ca"
    };

    // Regiones deducidas de las etiquetas No-Intro del nombre, p. ej. "Juego (USA, Europe).zip" -> us, eu.
    public static List<string> FromFileName(string fileName)
    {
        var result = new List<string>();
        foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(fileName, @"\(([^)]*)\)"))
            foreach (string part in tag.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (FileNameTags.TryGetValue(part, out var code) && !result.Contains(code)) result.Add(code);
        return result;
    }
}

// Resultado de una búsqueda por nombre: identificador del juego y todos los nombres que ScreenScraper conoce de él.
internal sealed record SsGameHit(string Id, List<string> Names);

internal sealed record SsLookupResult(List<SsGame> Games, string RawJson);

internal sealed class ScreenScraperException : Exception
{
    public int StatusCode { get; }
    public ScreenScraperException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
}

internal static class SsPick
{
    public static string Text(IEnumerable<SsText> items, params string[] preferredKeys)
    {
        var list = items as IList<SsText> ?? items.ToList();
        foreach (string key in preferredKeys)
        {
            var hit = list.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Text;
        }
        return list.FirstOrDefault()?.Text ?? "";
    }
}

internal static class SsMediaInfo
{
    // Carpeta de downloaded_media de ES-DE a la que corresponde cada tipo (propuesta; vacío = sin equivalencia).
    public static string EsDeFolder(string type) => type.ToLowerInvariant() switch
    {
        "box-2d" => "covers",
        "box-3d" => "3dboxes",
        "box-2d-back" => "backcovers",
        "ss" => "screenshots",
        "sstitle" => "titlescreens",
        "fanart" => "fanart",
        "marquee" or "wheel" or "wheel-hd" or "wheel-carbon" or "wheel-steel" or "screenmarquee" or "screenmarqueesmall" => "marquees",
        "mixrbv1" or "mixrbv2" => "miximages",
        "support-2d" => "physicalmedia",
        "video" or "video-normalized" => "videos",
        "manuel" => "manuals",
        _ => ""
    };

    public static string Describe(string type) => type.ToLowerInvariant() switch
    {
        "ss" => "Captura de pantalla",
        "sstitle" => "Pantalla de título",
        "fanart" => "Fan art",
        "video" => "Vídeo",
        "video-normalized" => "Vídeo (normalizado)",
        "overlay" => "Overlay",
        "steamgrid" => "Steam Grid",
        "wheel" => "Logo (wheel)",
        "wheel-hd" => "Logo HD (wheel)",
        "marquee" => "Marquee",
        "screenmarquee" => "Screen marquee",
        "box-2d" => "Caja frontal 2D",
        "box-2d-side" => "Caja: lomo",
        "box-2d-back" => "Caja: trasera",
        "box-texture" => "Caja: textura",
        "box-3d" => "Caja 3D",
        "support-2d" => "Soporte (cartucho/disco) 2D",
        "support-3d" => "Soporte 3D",
        "support-texture" => "Soporte: textura",
        "manuel" => "Manual",
        "flyer" => "Flyer",
        "maps" => "Mapas",
        "figurine" => "Figurita",
        "mixrbv1" => "Miximage (Recalbox v1)",
        "mixrbv2" => "Miximage (Recalbox v2)",
        "wheel-carbon" => "Logo (wheel, carbono)",
        "wheel-steel" => "Logo (wheel, acero)",
        "screenmarqueesmall" => "Screen marquee (pequeño)",
        "box-scan" => "Caja: escaneo",
        "support-scan" => "Soporte: escaneo",
        "background" => "Fondo",
        "pictoliste" or "pictomonochrome" or "pictocouleur" => "Pictograma",
        _ => type.StartsWith("bezel", StringComparison.OrdinalIgnoreCase) ? "Bezel" : ""
    };

    public static string Kind(string format) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" => "image",
        "mp4" or "mkv" or "avi" or "mov" or "webm" or "m4v" => "video",
        "pdf" => "pdf",
        _ => "other"
    };

    public static bool IsImage(string format) => Kind(format) == "image";
}

// Copias completas de los medios de ScreenScraper para compararlos antes de elegir; se reutilizan entre consultas.
internal static class SsMediaCache
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "media_cache");

    public static string PathFor(string gameId, SsMedia media)
    {
        string game = gameId.Length > 0 && gameId.All(char.IsAsciiDigit) ? gameId : "sin_id";
        string region = media.Region.Length > 0 ? media.Region : "todas";
        string hash = media.Md5.Length >= 12 ? media.Md5[..12].ToLowerInvariant() : "sinmd5";
        string format = media.Format.Length > 0 ? media.Format : "bin";
        return Path.Combine(Root, game, Safe($"{media.Type}_{region}_{hash}.{format}"));
    }

    // La descarga es atómica y verifica el MD5, así que basta con comprobar que existe con el tamaño esperado.
    public static bool IsCached(string gameId, SsMedia media)
    {
        var info = new FileInfo(PathFor(gameId, media));
        return info.Exists && (media.Size <= 0 || !media.IsGameMedia || info.Length == media.Size);
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
