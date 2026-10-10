using System.Net;
using System.Text.Json;

namespace ESDERomCleaner;

internal sealed class ScreenScraperClient
{
    private const string BaseUrl = "https://api.screenscraper.fr/api2/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly ScreenScraperCredentials credentials;

    public ScreenScraperClient(ScreenScraperCredentials credentials) { this.credentials = credentials; }

    private static string SystemsCachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDERomCleaner", "screenscraper_systems.json");

    private string BuildUrl(string endpoint, params (string Key, string? Value)[] extra)
    {
        var pairs = new List<(string, string?)>
        {
            ("devid", credentials.DevId), ("devpassword", credentials.DevPassword), ("softname", credentials.SoftName), ("output", "json")
        };
        if (!string.IsNullOrWhiteSpace(credentials.UserId))
        {
            pairs.Add(("ssid", credentials.UserId));
            pairs.Add(("sspassword", credentials.UserPassword));
        }
        pairs.AddRange(extra);
        return BaseUrl + endpoint + "?" + string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2!)));
    }

    private static string ExplainStatus(HttpStatusCode code, string body)
    {
        int status = (int)code;
        return status switch
        {
            400 => "Petición incorrecta (faltan campos o el nombre/hash de la ROM no es válido).",
            401 => "API cerrada para no miembros o miembros inactivos: el servidor está saturado.",
            403 => "Error de login: revisa el identificador y contraseña de desarrollador (y los de usuario).",
            404 => "ScreenScraper no reconoce esta ROM / no encuentra el juego.",
            423 => "API totalmente cerrada: el servidor tiene problemas graves.",
            426 => "El software ha sido bloqueado por ScreenScraper (nombre de software o versión no admitidos).",
            429 => "Demasiadas peticiones simultáneas o por minuto. Espera y reintenta.",
            430 => "Cuota diaria de peticiones agotada.",
            431 => "Demasiadas ROMs no reconocidas hoy. Reintenta mañana.",
            _ => $"Error HTTP {status}."
        };
    }

    private async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, token);
            string body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) throw new ScreenScraperException((int)response.StatusCode, ExplainStatus(response.StatusCode, body));
            return body;
        }
        catch (HttpRequestException ex) { throw new ScreenScraperException(0, "No se pudo conectar con ScreenScraper: " + ex.Message.Split('?')[0]); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new ScreenScraperException(0, "ScreenScraper tardó demasiado en responder."); }
    }

    private static JsonDocument ParseJson(string body)
    {
        string trimmed = body.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!trimmed.StartsWith('{')) throw new ScreenScraperException(200, "Respuesta inesperada de ScreenScraper: " + (trimmed.Length > 200 ? trimmed[..200] : trimmed));
        return JsonDocument.Parse(trimmed);
    }

    public async Task<List<SsSystem>> GetSystemsAsync(bool forceRefresh, CancellationToken token)
    {
        string body;
        if (!forceRefresh && File.Exists(SystemsCachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(SystemsCachePath) < TimeSpan.FromDays(30))
            body = await File.ReadAllTextAsync(SystemsCachePath, token);
        else
        {
            body = await GetStringAsync(BuildUrl("systemesListe.php"), token);
            try { Directory.CreateDirectory(Path.GetDirectoryName(SystemsCachePath)!); await File.WriteAllTextAsync(SystemsCachePath, body, token); } catch { }
        }
        using var doc = ParseJson(body);
        var result = new List<SsSystem>();
        if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("systemes", out var systems) || systems.ValueKind != JsonValueKind.Array) return result;
        foreach (var s in systems.EnumerateArray())
        {
            if (!int.TryParse(Str(s, "id"), out int id)) continue;
            string name = "", retropie = "", recalbox = "";
            string[] common = Array.Empty<string>();
            if (s.TryGetProperty("noms", out var names) && names.ValueKind == JsonValueKind.Object)
            {
                name = Str(names, "nom_eu"); if (name.Length == 0) name = Str(names, "nom_us"); if (name.Length == 0) name = Str(names, "nom_wor");
                retropie = Str(names, "nom_retropie").ToLowerInvariant();
                recalbox = Str(names, "nom_recalbox").ToLowerInvariant();
                common = Str(names, "noms_commun").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (name.Length == 0) name = common.FirstOrDefault() ?? "";
            }
            if (name.Length == 0) name = "Sistema " + id;
            result.Add(new SsSystem(id, name, retropie, recalbox, common));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SsLookupResult> LookupAsync(int systemId, string romFileName, RomHashes hashes, CancellationToken token)
    {
        string url = BuildUrl("jeuInfos.php",
            ("crc", hashes.Crc32), ("md5", hashes.Md5), ("sha1", hashes.Sha1),
            ("systemeid", systemId.ToString()), ("romtype", "rom"), ("romnom", romFileName),
            ("romtaille", hashes.Size.ToString()));
        string body = await GetStringAsync(url, token);
        using var doc = ParseJson(body);
        string pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        pretty = pretty.Replace(Uri.EscapeDataString(credentials.DevPassword), "***");
        if (!doc.RootElement.TryGetProperty("response", out var response)) throw new ScreenScraperException(200, "La respuesta no contiene 'response'.");
        string quota = "";
        if (response.TryGetProperty("ssuser", out var user) && user.ValueKind == JsonValueKind.Object)
            quota = $"Peticiones hoy: {Str(user, "requeststoday")}/{Str(user, "maxrequestsperday")} · Fallidas: {Str(user, "requestskotoday")}/{Str(user, "maxrequestskoperday")} · Hilos: {Str(user, "maxthreads")}";
        var games = new List<SsGame>();
        if (response.TryGetProperty("jeu", out var jeu) && jeu.ValueKind == JsonValueKind.Object) games.Add(ParseGame(jeu));
        return new SsLookupResult(games, MaskSecrets(pretty), quota);
    }

    private string MaskSecrets(string text)
    {
        foreach (string secret in new[] { credentials.DevPassword, credentials.UserPassword })
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***");
        return text;
    }

    private static SsGame ParseGame(JsonElement jeu)
    {
        var game = new SsGame
        {
            Id = Str(jeu, "id"), RomId = Str(jeu, "romid"), Name = Str(jeu, "nom"),
            SystemName = Str(jeu, "systeme", "text"), Publisher = Str(jeu, "editeur", "text"), Developer = Str(jeu, "developpeur", "text"),
            Players = Str(jeu, "joueurs", "text"), Rating = Str(jeu, "note", "text")
        };
        FillTexts(game.Names, jeu, "noms", "region");
        FillTexts(game.Synopsis, jeu, "synopsis", "langue");
        FillTexts(game.Dates, jeu, "dates", "region");
        if (jeu.TryGetProperty("classifications", out var cls) && cls.ValueKind == JsonValueKind.Array)
            foreach (var c in cls.EnumerateArray()) game.Classifications.Add(new SsText(Str(c, "type"), Str(c, "text")));
        foreach (string group in new[] { "genres", "modes", "familles", "numeros", "themes", "styles" })
        {
            if (!jeu.TryGetProperty(group, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var list = new List<List<SsText>>();
            foreach (var item in items.EnumerateArray())
            {
                var texts = new List<SsText>();
                FillTexts(texts, item, "noms", "langue");
                if (texts.Count > 0) list.Add(texts);
            }
            if (list.Count > 0) game.Groups[group] = list;
        }
        if (jeu.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in medias.EnumerateArray())
            {
                long.TryParse(Str(m, "size"), out long size);
                game.Medias.Add(new SsMedia(Str(m, "type"), Str(m, "parent"), Str(m, "region"), Str(m, "format"), size, Str(m, "url")));
            }
        }
        if (jeu.TryGetProperty("rom", out var rom) && rom.ValueKind == JsonValueKind.Object)
            foreach (var p in rom.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    game.RomProperties.Add(new SsText(p.Name, Str(rom, p.Name)));
        // La ROM trae regiones e idiomas como objetos anidados con listas (regions_en, langues_en...); se aplanan a texto.
        if (jeu.TryGetProperty("rom", out var romInfo) && romInfo.ValueKind == JsonValueKind.Object)
            foreach (var (group, key) in new[] { ("regions", "regions_en"), ("langues", "langues_en") })
                if (romInfo.TryGetProperty(group, out var nested) && nested.ValueKind == JsonValueKind.Object &&
                    nested.TryGetProperty(key, out var names) && names.ValueKind == JsonValueKind.Array)
                {
                    string joined = string.Join(", ", names.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "").Where(n => n.Length > 0));
                    if (joined.Length > 0) game.RomProperties.Add(new SsText(group, joined));
                }
        return game;
    }

    private static void FillTexts(List<SsText> target, JsonElement parent, string property, string keyName)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            string text = Str(item, "text");
            if (text.Length > 0) target.Add(new SsText(Str(item, keyName), text));
        }
    }

    private static string Str(JsonElement element, string name, string? inner = null)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return "";
        if (inner is not null) return Str(value, inner);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }
}
