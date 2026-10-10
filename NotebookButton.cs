namespace ESDERomCleaner;

internal sealed class NotebookButton : Button
{
    public NotebookButton()
    {
        FlatStyle = FlatStyle.Standard;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        float width = 17 * scale, height = 19 * scale;
        float x = (ClientSize.Width - width) / 2f;
        float y = (ClientSize.Height - height) / 2f;
        using var outline = new Pen(ForeColor, Math.Max(1, 1.2f * scale));
        using var page = new SolidBrush(Enabled ? SystemColors.Window : SystemColors.Control);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillRectangle(page, x + 2 * scale, y, width - 2 * scale, height);
        e.Graphics.DrawRectangle(outline, x + 2 * scale, y, width - 2 * scale, height);
        e.Graphics.DrawLine(outline, x + 5 * scale, y, x + 5 * scale, y + height);
        for (int line = 1; line <= 3; line++)
        {
            float lineY = y + (4 + line * 3) * scale;
            e.Graphics.DrawLine(outline, x + 7 * scale, lineY, x + width - 2 * scale, lineY);
        }
        for (int mark = 0; mark < 4; mark++)
        {
            float markY = y + (2 + mark * 4) * scale;
            e.Graphics.DrawLine(outline, x, markY, x + 3 * scale, markY);
        }
    }
}
