namespace ESDERomCleaner;

// Etiqueta para las cabeceras con textura: dibuja el texto con una sombra oscura de un píxel para que se lea bien sobre los píxeles y las líneas de fondo.
internal sealed class ShadowLabel : Label
{
    public ShadowLabel() { BackColor = Color.Transparent; DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentBack = Parent?.BackColor ?? Color.Black;
        var shadow = Color.FromArgb(parentBack.R / 4, parentBack.G / 4, parentBack.B / 4);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.Left;
        if (AutoEllipsis) flags |= TextFormatFlags.EndEllipsis;
        if (TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight) flags |= TextFormatFlags.VerticalCenter;
        int offset = Math.Max(1, DeviceDpi / 96);
        var area = ClientRectangle;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(area.X + offset, area.Y + offset, area.Width, area.Height), shadow, flags);
        TextRenderer.DrawText(e.Graphics, Text, Font, area, ForeColor, flags);
    }
}
