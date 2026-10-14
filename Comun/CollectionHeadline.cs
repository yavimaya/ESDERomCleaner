namespace ESDERomCleaner;

// Cifras principales de la colección (sistemas y ROMs) en grande, con un subrayado dorado: es lo primero que se mira en la ventana principal.
internal sealed class CollectionHeadline : Control
{
    private static readonly Color Label = Color.FromArgb(70, 70, 80), Number = Color.FromArgb(20, 90, 160);
    private string systems = "0", roms = "0";
    private readonly Font labelFont = new("Segoe UI", 11F), numberFont = new("Segoe UI", 20F, FontStyle.Bold);

    public CollectionHeadline()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 40; Anchor = AnchorStyles.None; Margin = Padding.Empty;
        Loc.LanguageChanged += Relayout;
        Relayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Loc.LanguageChanged -= Relayout; labelFont.Dispose(); numberFont.Dispose(); }
        base.Dispose(disposing);
    }

    public void SetValues(string systems, string roms)
    {
        this.systems = systems; this.roms = roms;
        Relayout();
    }

    private (string Text, Font Font, Color Color)[] Parts() =>
    [
        (Loc.T("Sistemas") + " ", labelFont, Label), (systems, numberFont, Number), ("      " + Loc.T("ROMs") + " ", labelFont, Label), (roms, numberFont, Number)
    ];

    private int TextWidth() => Parts().Sum(p => TextRenderer.MeasureText(p.Text, p.Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width);

    private void Relayout() { Width = TextWidth() + 2; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int x = 0, baseline = 30;
        foreach (var (text, font, color) in Parts())
        {
            var size = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding);
            int y = baseline - (int)(font.GetHeight(e.Graphics) * 0.82f);
            TextRenderer.DrawText(e.Graphics, text, font, new Point(x, y), color, TextFormatFlags.NoPadding);
            x += size.Width;
        }
        using var gold = new SolidBrush(RaUi.Gold);
        e.Graphics.FillRectangle(gold, 0, Height - 4, Math.Max(0, x), 3);
    }
}
