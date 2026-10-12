namespace ESDERomCleaner;

// Huecos de la colección: cuántos juegos no tienen metadatos o les falta el miximage o el vídeo (por sistema y en total),
// con un menú para scrapear justo los que faltan en el formulario de descarga masiva.
public sealed partial class RomCleanerForm
{
    private void WireGaps()
    {
        systemGaps.Click += (_, _) => ShowGapMenu(systemGaps, systemOnly: true);
        collectionGaps.Click += (_, _) => ShowGapMenu(collectionGaps, systemOnly: false);
        root.TextChanged += (_, _) => UpdateScanButton();
        UpdateGapLabels();
    }

    // Carpeta raíz del último análisis correcto: mientras siga siendo la de la caja, el botón ofrece «Re-analizar».
    private string? loadedRoot;
    private int gapsVersion;

    private void UpdateScanButton()
    {
        bool loaded = all.Count > 0 && loadedRoot is not null && root.Text.Trim().Trim('"').Equals(loadedRoot, StringComparison.OrdinalIgnoreCase);
        scan.Text = loaded ? "Re-analizar colección" : "Analizar colección";
    }

    // Vuelve a calcular los huecos (lista cada carpeta de medios una vez) sin bloquear la ventana.
    private async Task RefreshGapsAsync()
    {
        var rows = all.Where(r => IsVisibleSystem(r.System)).ToList();
        string rootPath = root.Text;
        int version = ++gapsVersion; // si empieza otro cálculo mientras, solo cuenta el último
        gaps = null;
        UpdateGapLabels();
        Dictionary<string, GapCount>? result = null;
        try { result = await Task.Run(() => MediaGaps.Compute(rootPath, rows)); }
        catch (Exception ex) { trace.Add("gaps_error", ex.Message); }
        if (IsDisposed || version != gapsVersion) return;
        gaps = result ?? [];
        UpdateGapLabels();
    }

    // Icono del sistema elegido, a la izquierda del desplegable de sistemas.
    private readonly PictureBox systemIcon = new() { SizeMode = PictureBoxSizeMode.StretchImage, TabStop = false };

    private void UpdateGapLabels()
    {
        systemIcon.Image = systems.SelectedItem is string selectedSystem ? PixelSystemIcons.Get(selectedSystem, 54) : PixelSystemIcons.Placeholder(54);
        searchInSystem.Text = systems.SelectedItem is string scopeName ? scopeName : Loc.T("Este sistema"); // el botón de la búsqueda lleva el nombre del sistema
        if (gaps is null)
        {
            bool any = all.Count > 0;
            systemGaps.SetPlain(any ? "Calculando huecos…" : "");
            collectionGaps.SetPlain(any ? "Toda la colección: calculando huecos…" : "");
            return;
        }
        var total = new GapCount();
        foreach (var pair in gaps) if (IsVisibleSystem(pair.Key)) total += pair.Value;
        collectionGaps.Set("Toda la colección —", total);
        if (systems.SelectedItem is string s && gaps.TryGetValue(s, out var g)) systemGaps.Set("", g); else systemGaps.SetPlain("");
    }

    private void ShowGapMenu(Control anchor, bool systemOnly)
    {
        if (gaps is null || cts != null) return;
        string? system = systemOnly ? systems.SelectedItem as string : null;
        if (systemOnly && system is null) return;
        var scope = gaps.Where(p => IsVisibleSystem(p.Key) && (system is null || p.Key.Equals(system, StringComparison.OrdinalIgnoreCase))).Select(p => p.Value)
            .Aggregate(new GapCount(), (a, b) => a + b);
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var meta = new ToolStripMenuItem($"Scrapear metadatos de las ROMs sin categoría ({scope.NoMetadata:N0})") { Enabled = scope.NoMetadata > 0 };
        var media = new ToolStripMenuItem($"Scrapear imagen y vídeo de los que faltan ({scope.NoMedia:N0})") { Enabled = scope.NoMedia > 0 };
        var mix = new ToolStripMenuItem($"Scrapear imagen de los que faltan ({scope.NoImage:N0})") { Enabled = scope.NoImage > 0 };
        var video = new ToolStripMenuItem($"Scrapear vídeo de los que faltan ({scope.NoVideo:N0})") { Enabled = scope.NoVideo > 0 };
        meta.Click += async (_, _) => await ScrapeMissingAsync(system, GapKind.Metadata);
        media.Click += async (_, _) => await ScrapeMissingAsync(system, GapKind.Media);
        mix.Click += async (_, _) => await ScrapeMissingAsync(system, GapKind.Image);
        video.Click += async (_, _) => await ScrapeMissingAsync(system, GapKind.Video);
        menu.Items.AddRange([meta, media, mix, video]);
        Loc.Attach(menu);
        menu.Show(Cursor.Position); // justo donde se ha hecho clic
    }

    // Abre la descarga masiva solo con las ROMs a las que les falta lo pedido (todas las de la colección visible o las del sistema actual).
    private enum GapKind { Metadata, Media, Image, Video }

    private async Task ScrapeMissingAsync(string? system, GapKind kind)
    {
        var candidates = all.Where(r => IsVisibleSystem(r.System) && (system is null || r.System.Equals(system, StringComparison.OrdinalIgnoreCase))).ToList();
        string rootPath = root.Text;
        var gap = await Task.Run(() => MediaGaps.Rows(rootPath, candidates));
        var wanted = kind switch { GapKind.Metadata => gap.NoMetadata, GapKind.Image => gap.NoImage, GapKind.Video => gap.NoVideo, _ => gap.NoMedia };
        var rows = candidates.Where(r => wanted.Contains(r.Path)).ToList();
        if (rows.Count == 0) return;
        trace.Add("scrape_missing", $"scope={system ?? "collection"}; kind={kind.ToString().ToLowerInvariant()}; roms={rows.Count}");
        // Los metadatos (la categoría) se piden con la ventana sencilla de progreso de «Categorizar», sin abrir el formulario de scrapeo.
        if (kind == GapKind.Metadata)
        {
            await CategorizeAsync(rows.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), onlyMissingCategory: true);
            return;
        }
        await OpenBulkMediaDialogAsync(onlyRows: rows, preset: kind switch { GapKind.Metadata => BulkPreset.MetadataOnly, GapKind.Image => BulkPreset.ImageOnly, GapKind.Video => BulkPreset.VideoOnly, _ => BulkPreset.MediaOnly });
    }
}
