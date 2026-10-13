namespace ESDERomCleaner;

// Huecos de la colección: juegos sin categoría (género) en el gamelist y juegos sin miximage o sin vídeo en downloaded_media.
internal readonly record struct GapCount(int Total, int NoMetadata, int NoImage, int NoVideo, int NoMedia)
{
    public static GapCount operator +(GapCount a, GapCount b) =>
        new(a.Total + b.Total, a.NoMetadata + b.NoMetadata, a.NoImage + b.NoImage, a.NoVideo + b.NoVideo, a.NoMedia + b.NoMedia);
}

internal sealed class GapRows
{
    public readonly HashSet<string> NoMetadata = new(StringComparer.OrdinalIgnoreCase), NoImage = new(StringComparer.OrdinalIgnoreCase),
        NoVideo = new(StringComparer.OrdinalIgnoreCase), NoMedia = new(StringComparer.OrdinalIgnoreCase);
}

internal static class MediaGaps
{
    // Un juego identificado «sin categoría» no tiene género en el gamelist (el lector de gamelists pone «Sin categoría» cuando falta).
    private static bool NoMetadata(RomRow row) => string.IsNullOrWhiteSpace(row.Genre) || row.Genre == "Sin categoría";

    // Recorre solo los juegos identificados. Cada carpeta de medios se lista una sola vez (ExistingMediaIndex).
    private static IEnumerable<(RomRow Row, bool NoMeta, bool NoMix, bool NoVideo)> Evaluate(string root, IEnumerable<RomRow> rows, CancellationToken token)
    {
        var index = new ExistingMediaIndex();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Status != RomStatus.Ok || row.Path.Length == 0) continue;
            bool noMix, noVideo;
            try
            {
                noMix = !ImageSettings.Choices.Any(c => index.Has(EsDeMediaInstaller.TargetBase(root, row, c.Folder))); // sin imagen = ninguna de las carpetas de imagen tiene archivo
                noVideo = !index.Has(EsDeMediaInstaller.TargetBase(root, row, "videos"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; } // ROM fuera de su carpeta o ruta no válida: no se puede deducir dónde estarían sus medios
            yield return (row, NoMetadata(row), noMix, noVideo);
        }
    }

    public static Dictionary<string, GapCount> Compute(string root, IEnumerable<RomRow> rows, CancellationToken token = default)
    {
        var result = new Dictionary<string, GapCount>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, meta, mix, video) in Evaluate(root, rows, token))
        {
            var gap = new GapCount(1, meta ? 1 : 0, mix ? 1 : 0, video ? 1 : 0, mix || video ? 1 : 0);
            result[row.System] = result.TryGetValue(row.System, out var old) ? old + gap : gap;
        }
        return result;
    }

    // Rutas de las ROMs sin categoría, sin miximage, sin vídeo y las que les falta alguno de los dos medios.
    public static GapRows Rows(string root, IEnumerable<RomRow> rows, CancellationToken token = default)
    {
        var result = new GapRows();
        foreach (var (row, meta, mix, video) in Evaluate(root, rows, token))
        {
            if (meta) result.NoMetadata.Add(row.Path);
            if (mix) result.NoImage.Add(row.Path);
            if (video) result.NoVideo.Add(row.Path);
            if (mix || video) result.NoMedia.Add(row.Path);
        }
        return result;
    }

    public static string Text(GapCount g) => $"Sin categoría: {g.NoMetadata:N0} | Sin imagen: {g.NoImage:N0} | Sin vídeo: {g.NoVideo:N0}";
}

// Resumen de huecos dibujado a mano: cada dato distinto de 0 sale en rojo y negrita (el texto y el número), el resto en azul como un enlace.
internal sealed class GapSummary : Control
{
    private readonly bool center;
    private readonly Font bold;
    private string plain = "";
    private string prefix = "";
    private (string Text, bool Alert)[] parts = [];

    public GapSummary(bool center)
    {
        this.center = center;
        bold = new Font(Font, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Loc.LanguageChanged -= OnLanguageChanged; bold.Dispose(); }
        base.Dispose(disposing);
    }

    public void SetPlain(string text) { plain = text; prefix = ""; parts = []; Invalidate(); }

    public void Set(string prefixText, GapCount g)
    {
        plain = "";
        prefix = prefixText;
        parts = [($"Sin categoría: {g.NoMetadata:N0}", g.NoMetadata > 0), ($"Sin imagen: {g.NoImage:N0}", g.NoImage > 0), ($"Sin vídeo: {g.NoVideo:N0}", g.NoVideo > 0)];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var pieces = new List<(string Text, Font Font, Color Color)>();
        Color link = Color.MediumBlue, alert = Color.Red;
        if (parts.Length == 0) { if (plain.Length > 0) pieces.Add((Loc.T(plain), Font, link)); }
        else
        {
            if (prefix.Length > 0) pieces.Add((Loc.T(prefix) + " ", Font, link));
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) pieces.Add((" | ", Font, link));
                pieces.Add((Loc.T(parts[i].Text), parts[i].Alert ? bold : Font, parts[i].Alert ? alert : link));
            }
            pieces.Add((" ▾", Font, link));
        }
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        int total = pieces.Sum(p => TextRenderer.MeasureText(e.Graphics, p.Text, p.Font, Size.Empty, flags).Width);
        int x = center ? Math.Max(0, (Width - total) / 2) : 0;
        foreach (var (text, font, color) in pieces)
        {
            int w = TextRenderer.MeasureText(e.Graphics, text, font, Size.Empty, flags).Width;
            TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(x, 0, w + 2, Height), color, flags);
            x += w;
        }
    }
}
