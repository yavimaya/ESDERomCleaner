using System.Net;
using System.Text.Json;

namespace ESDERomCleaner;

internal sealed class ScreenScraperClient
{
    private const string BaseUrl = "https://api.screenscraper.fr/api2/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    // Los vídeos y manuales tardan minutos con la velocidad limitada de ScreenScraper: sin límite total, solo por inactividad.
    private static readonly HttpClient MediaHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private readonly ScreenScraperCredentials credentials;

    public ScreenScraperClient(ScreenScraperCredentials credentials) { this.credentials = credentials; }

    private static string SystemsCachePath => Path.Combine(AppPaths.Data, "screenscraper_systems.json");

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
            ScreenScraperQuota.RecordCall(response.IsSuccessStatusCode);
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

    // Lista oficial de géneros (genresListe.php): pares «nombre en español → nombre en inglés». Se lee de forma tolerante porque el formato
    // exacto no está comprobado: se buscan los objetos con «noms» (lista de {langue, text} o campos nom_xx) en cualquier parte de la respuesta.
    public async Task<Dictionary<string, string>> GetGenreNamesAsync(CancellationToken token)
    {
        string body = await GetStringAsync(BuildUrl("genresListe.php"), token);
        using var doc = ParseJson(body);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Visit(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("noms", out var names))
                {
                    string es = "", en = "";
                    if (names.ValueKind == JsonValueKind.Array)
                        foreach (var n in names.EnumerateArray())
                        {
                            string lang = Str(n, "langue"), text = Str(n, "text");
                            if (lang == "es") es = text; else if (lang == "en") en = text;
                        }
                    else if (names.ValueKind == JsonValueKind.Object)
                    {
                        es = Str(names, "nom_es"); en = Str(names, "nom_en");
                    }
                    if (es.Length > 0 && en.Length > 0 && !es.Equals(en, StringComparison.OrdinalIgnoreCase)) result[es] = en;
                }
                foreach (var p in e.EnumerateObject()) Visit(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) Visit(item);
        }
        Visit(doc.RootElement);
        return result;
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
            // ScreenScraper lo llama «Mame»; aquí es el sistema arcade genérico.
            if (id == 75) name = "Arcade (MAME)";
            result.Add(new SsSystem(id, name, retropie, recalbox, common));
        }
        return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Lee del servidor los datos de la cuenta (hilos, cuota, velocidad) sin consultar ningún juego.
    public async Task RefreshUserAsync(CancellationToken token)
    {
        string body = await GetStringAsync(BuildUrl("ssuserInfos.php"), token);
        using var doc = ParseJson(body);
        if (doc.RootElement.TryGetProperty("response", out var response) && response.TryGetProperty("ssuser", out var user) && user.ValueKind == JsonValueKind.Object)
            ScreenScraperQuota.Sync(user);
    }

    public async Task<SsLookupResult> LookupAsync(int systemId, string romFileName, RomHashes hashes, CancellationToken token)
    {
        string url = BuildUrl("jeuInfos.php",
            ("crc", hashes.Crc32), ("md5", hashes.Md5), ("sha1", hashes.Sha1),
            ("systemeid", systemId.ToString()), ("romtype", "rom"), ("romnom", romFileName),
            ("romtaille", hashes.Size.ToString()));
        return await LookupUrlAsync(url, token);
    }

    // Ficha completa de un juego ya identificado (p. ej. tras buscarlo por nombre), sin hash de ROM.
    public Task<SsLookupResult> LookupByGameIdAsync(int systemId, string gameId, CancellationToken token) =>
        LookupUrlAsync(BuildUrl("jeuInfos.php", ("systemeid", systemId.ToString()), ("gameid", gameId)), token);

    // Búsqueda por texto dentro de un sistema. Devuelve los juegos con todos sus nombres regionales para poder exigir
    // una coincidencia exacta. Si ScreenScraper no encuentra nada (404), la lista queda vacía.
    public async Task<List<SsGameHit>> SearchGamesAsync(int systemId, string name, CancellationToken token)
    {
        string body;
        try { body = await GetStringAsync(BuildUrl("jeuRecherche.php", ("systemeid", systemId.ToString()), ("recherche", name)), token); }
        catch (ScreenScraperException ex) when (ex.StatusCode == 404) { return new(); }
        using var doc = ParseJson(body);
        var hits = new List<SsGameHit>();
        if (!doc.RootElement.TryGetProperty("response", out var response)) return hits;
        if (response.TryGetProperty("ssuser", out var user) && user.ValueKind == JsonValueKind.Object) ScreenScraperQuota.Sync(user);
        if (!response.TryGetProperty("jeux", out var games) || games.ValueKind != JsonValueKind.Array) return hits;
        foreach (var game in games.EnumerateArray())
        {
            string id = Str(game, "id");
            if (id.Length == 0) continue;
            var names = new List<string>();
            var texts = new List<SsText>();
            FillTexts(texts, game, "noms", "region");
            names.AddRange(texts.Select(t => t.Text));
            string single = Str(game, "nom");
            if (single.Length > 0) names.Add(single);
            hits.Add(new SsGameHit(id, names));
        }
        return hits;
    }

    // Aproximaciones por nombre: los juegos que ScreenScraper devuelve para un texto (sin exigir coincidencia exacta), ya interpretados con sus
    // nombres, sinopsis y medios. Si no encuentra nada (404), la lista queda vacía.
    public async Task<List<SsGame>> SearchCandidatesAsync(int systemId, string name, CancellationToken token)
    {
        string body;
        try { body = await GetStringAsync(BuildUrl("jeuRecherche.php", ("systemeid", systemId.ToString()), ("recherche", name)), token); }
        catch (ScreenScraperException ex) when (ex.StatusCode == 404) { return new(); }
        using var doc = ParseJson(body);
        var games = new List<SsGame>();
        if (!doc.RootElement.TryGetProperty("response", out var response)) return games;
        if (response.TryGetProperty("ssuser", out var user) && user.ValueKind == JsonValueKind.Object) ScreenScraperQuota.Sync(user);
        if (!response.TryGetProperty("jeux", out var list) || list.ValueKind != JsonValueKind.Array) return games;
        foreach (var jeu in list.EnumerateArray())
        {
            if (jeu.ValueKind != JsonValueKind.Object || Str(jeu, "id").Length == 0) continue;
            games.Add(ParseGame(jeu));
        }
        return games;
    }

    private async Task<SsLookupResult> LookupUrlAsync(string url, CancellationToken token)
    {
        string body = await GetStringAsync(url, token);
        using var doc = ParseJson(body);
        string pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        pretty = pretty.Replace(Uri.EscapeDataString(credentials.DevPassword), "***");
        if (!doc.RootElement.TryGetProperty("response", out var response)) throw new ScreenScraperException(200, "La respuesta no contiene 'response'.");
        if (response.TryGetProperty("ssuser", out var user) && user.ValueKind == JsonValueKind.Object)
            ScreenScraperQuota.Sync(user);
        var games = new List<SsGame>();
        if (response.TryGetProperty("jeu", out var jeu) && jeu.ValueKind == JsonValueKind.Object) games.Add(ParseGame(jeu));
        return new SsLookupResult(games, MaskSecrets(pretty));
    }

    // Descarga un medio completo a 'target' (temporal + Move) y verifica el MD5 que publica ScreenScraper.
    // Las cuentas gratuitas solo admiten un hilo: el llamador debe descargar de uno en uno.
    public async Task DownloadMediaAsync(SsMedia media, string target, IProgress<long>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(media.Url)) throw new ScreenScraperException(0, "ScreenScraper no indica la dirección de descarga de este medio.");
        if (!Uri.TryCreate(media.Url, UriKind.Absolute, out var mediaUri) || mediaUri.Scheme != Uri.UriSchemeHttps
            || !(mediaUri.Host.Equals("screenscraper.fr", StringComparison.OrdinalIgnoreCase) || mediaUri.Host.EndsWith(".screenscraper.fr", StringComparison.OrdinalIgnoreCase)))
            throw new ScreenScraperException(0, "La dirección de descarga del medio no pertenece a screenscraper.fr.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
        for (int attempt = 1; ; attempt++)
        {
            // Se cancela si pasan StallTimeout sin recibir datos; cada bloque recibido reinicia el plazo.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
            stall.CancelAfter(StallTimeout);
            try
            {
                using var response = await MediaHttp.GetAsync(media.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                ScreenScraperQuota.RecordCall(response.IsSuccessStatusCode);
                if ((int)response.StatusCode == 429 && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), token);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ScreenScraperException((int)response.StatusCode, ExplainStatus(response.StatusCode, ""));
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    string body = (await response.Content.ReadAsStringAsync(stall.Token)).Trim();
                    throw new ScreenScraperException(200, "ScreenScraper no devolvió el archivo: " + MaskSecrets(body.Length > 200 ? body[..200] : body));
                }

                string md5;
                await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, stall.Token)) > 0)
                    {
                        stall.CancelAfter(StallTimeout);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        hash.AppendData(buffer, 0, read);
                        total += read;
                        progress?.Report(total);
                    }
                    md5 = Convert.ToHexString(hash.GetHashAndReset());
                }
                // Los pictogramas y fondos de género/editor publican un MD5 que no corresponde al archivo servido; solo se exige en los del juego.
                if (media.IsGameMedia && media.Md5.Length > 0 && !md5.Equals(media.Md5, StringComparison.OrdinalIgnoreCase))
                    throw new ScreenScraperException(200, "El archivo recibido no coincide con el MD5 que indica ScreenScraper (descarga incompleta o dañada).");
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new ScreenScraperException(0, $"La descarga se quedó parada más de {StallTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new ScreenScraperException(0, "No se pudo descargar de ScreenScraper: " + MaskSecrets(ex.Message.Split('?')[0]));
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                throw new ScreenScraperException(0, "Error de red o de disco durante la descarga: " + MaskSecrets(ex.Message.Split('?')[0]));
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
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
                if (texts.Count > 0 && group == "genres") game.Genres.Add(new SsGenre(Str(item, "id"), Str(item, "parentid"), Str(item, "principale") == "1", texts));
                if (texts.Count > 0) list.Add(texts);
            }
            if (list.Count > 0) game.Groups[group] = list;
        }
        if (jeu.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in medias.EnumerateArray())
            {
                long.TryParse(Str(m, "size"), out long size);
                game.Medias.Add(new SsMedia(Str(m, "type"), Str(m, "parent"), Str(m, "region"), Str(m, "format"), size, Str(m, "md5"), Str(m, "url")));
            }
        }
        if (jeu.TryGetProperty("rom", out var rom) && rom.ValueKind == JsonValueKind.Object)
            foreach (var p in rom.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    game.RomProperties.Add(new SsText(p.Name, Str(rom, p.Name)));
        if (jeu.TryGetProperty("rom", out var romRegions) && romRegions.ValueKind == JsonValueKind.Object)
        {
            if (romRegions.TryGetProperty("regions", out var regionGroup) && regionGroup.ValueKind == JsonValueKind.Object &&
                regionGroup.TryGetProperty("regions_shortname", out var shortNames) && shortNames.ValueKind == JsonValueKind.Array)
                game.RomRegions.AddRange(shortNames.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString() ?? "").Where(n => n.Length > 0));
            if (game.RomRegions.Count == 0)
                game.RomRegions.AddRange(Str(romRegions, "romregions").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (romRegions.TryGetProperty("langues", out var languageGroup) && languageGroup.ValueKind == JsonValueKind.Object &&
                languageGroup.TryGetProperty("langues_shortname", out var languageCodes) && languageCodes.ValueKind == JsonValueKind.Array)
                game.RomLanguages.AddRange(languageCodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => (n.GetString() ?? "").ToLowerInvariant()).Where(n => n.Length > 0));
            if (game.RomLanguages.Count == 0)
                game.RomLanguages.AddRange(Str(romRegions, "romlangues").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(l => l.ToLowerInvariant()));
        }
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
