namespace ESDERomCleaner;

// Insignia que va en la franja del título del panel de medios, a la derecha (no ocupa sitio del medio): logotipo de RetroAchievements, logros
// totales del juego y, si el usuario tiene credenciales, los conseguidos en hardcore (amarillo) y solo en softcore (blanco). Va sobre una píldora
// oscura para que el blanco se lea.
internal sealed class RaBadge : Control
{
    private int total;
    private string award = "";   // premio del usuario en el juego: «mastered» (trofeo dorado), «beaten-…» (trofeo plateado) o vacío
    private (int Hardcore, int Softcore)? earned;   // null = no se muestra; (-1, -1) = se desconoce todavía
    private readonly Font numberFont, captionFont;
    private static readonly Color Pill = Color.FromArgb(30, 33, 43), TotalColor = Color.FromArgb(222, 226, 234), HardcoreColor = Color.FromArgb(246, 196, 48),
        SoftcoreColor = Color.White, CaptionColor = Color.FromArgb(150, 158, 176);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left;
    private const int LogoSize = 16, Gap = 12, Pad = 6, TrophyWidth = 14;

    public RaBadge()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Control; // tapa el trazo del marco del panel que pasaría por detrás
        TabStop = false;
        Margin = Padding.Empty;
        Height = 18;
        Anchor = AnchorStyles.Top | AnchorStyles.Right;
        numberFont = Pick("Bahnschrift SemiBold", 10.5F);
        captionFont = Pick("Bahnschrift SemiLight", 8.5F);
        Loc.LanguageChanged += OnLanguageChanged;
    }

    // Una fuente de carácter (Bahnschrift, de Windows 10) y, si falta, la más parecida que haya.
    private static Font Pick(string family, float size)
    {
        var installed = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { family, "Segoe UI Semibold", "Segoe UI" })
            if (installed.Contains(name)) return new Font(name, size, GraphicsUnit.Point);
        return new Font(SystemFonts.DefaultFont.FontFamily, size);
    }

    public void SetAchievements(int totalAchievements, (int Hardcore, int Softcore)? earnedAchievements, string awardKind = "")
    {
        total = totalAchievements; earned = earnedAchievements; award = awardKind;
        Refit();
    }

    private void OnLanguageChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(Refit); }

    private IEnumerable<(string Number, Color Color, string Caption)> Segments()
    {
        yield return (total.ToString("N0"), TotalColor, Loc.T("logros"));
        if (earned is { } e)
        {
            yield return (e.Hardcore < 0 ? "—" : e.Hardcore.ToString("N0"), HardcoreColor, "hardcore");
            yield return (e.Softcore < 0 ? "—" : e.Softcore.ToString("N0"), SoftcoreColor, "softcore");
        }
    }

    private bool HasTrophy => award == "mastered" || award.StartsWith("beaten", StringComparison.Ordinal);

    // Trofeo pequeño dibujado a mano: dorado si está «mastered» y plateado si está «beaten».
    private void DrawTrophy(Graphics g, int x, int y)
    {
        bool gold = award == "mastered";
        var fill = gold ? Color.FromArgb(244, 196, 44) : Color.FromArgb(208, 213, 223);
        var edge = gold ? Color.FromArgb(168, 120, 0) : Color.FromArgb(118, 124, 138);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(edge, 1.2F);
        using var cup = new System.Drawing.Drawing2D.GraphicsPath();
        cup.AddLine(x + 3, y + 1, x + 11, y + 1);
        cup.AddBezier(x + 11, y + 1, x + 11, y + 8, x + 9, y + 9.5F, x + 7, y + 9.5F);
        cup.AddBezier(x + 7, y + 9.5F, x + 5, y + 9.5F, x + 3, y + 8, x + 3, y + 1);
        cup.CloseFigure();
        g.DrawArc(pen, x + 0.6F, y + 2, 5, 5, 90, 180);     // asa izquierda
        g.DrawArc(pen, x + 8.4F, y + 2, 5, 5, 270, 180);    // asa derecha
        g.FillPath(brush, cup); g.DrawPath(pen, cup);
        g.FillRectangle(brush, x + 6, y + 9.5F, 2, 2); g.DrawRectangle(pen, x + 6, y + 9.5F, 2, 2);
        g.FillRectangle(brush, x + 4, y + 11.5F, 6, 2); g.DrawRectangle(pen, x + 4, y + 11.5F, 6, 2);
    }

    // Ajusta el ancho al contenido y lo pega al borde derecho del panel.
    private void Refit()
    {
        int width = Pad + (HasTrophy ? TrophyWidth + 4 : 0) + LogoSize + 6;
        foreach (var (number, _, caption) in Segments())
            width += TextRenderer.MeasureText(number, numberFont, new Size(int.MaxValue, 0), Flags).Width + 3 + TextRenderer.MeasureText(caption, captionFont, new Size(int.MaxValue, 0), Flags).Width + Gap;
        Width = width - Gap + Pad;
        if (Parent is not null) Left = Parent.ClientSize.Width - Width - 8;
        Top = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
        using (var brush = new SolidBrush(Pill)) g.FillPath(brush, path);
        int logoX = Pad;
        if (HasTrophy) { DrawTrophy(g, Pad, (Height - 14) / 2); logoX += TrophyWidth + 4; }
        g.DrawImage(PixelSystemIcons.Get("★ RetroAchievements", 64), new Rectangle(logoX, (Height - LogoSize) / 2, LogoSize, LogoSize));
        int x = logoX + LogoSize + 6;
        foreach (var (number, color, caption) in Segments())
        {
            var nSize = TextRenderer.MeasureText(g, number, numberFont, new Size(int.MaxValue, 0), Flags);
            var cSize = TextRenderer.MeasureText(g, caption, captionFont, new Size(int.MaxValue, 0), Flags);
            int top = (Height - nSize.Height) / 2;
            TextRenderer.DrawText(g, number, numberFont, new Point(x, top), color, Flags);
            TextRenderer.DrawText(g, caption, captionFont, new Point(x + nSize.Width + 3, top + nSize.Height - cSize.Height - 1), CaptionColor, Flags);
            x += nSize.Width + 3 + cSize.Width + Gap;
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST → HTTRANSPARENT: los clics pasan a lo que hay debajo
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Loc.LanguageChanged -= OnLanguageChanged; numberFont.Dispose(); captionFont.Dispose(); }
        base.Dispose(disposing);
    }
}
