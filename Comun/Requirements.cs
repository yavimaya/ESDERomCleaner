namespace ESDERomCleaner;

// Lo que necesita una acción para poder hacerse.
[Flags]
internal enum Need
{
    None = 0,
    Internet = 1,
    ScreenScraper = 2,       // credenciales de ScreenScraper
    RetroAchievements = 4,   // usuario y clave de RetroAchievements
    Dats = 8                 // bases de datos de ROMs (DAT) descargadas
}

// Comprobación previa de lo que necesita una acción (cuenta de ScreenScraper o RetroAchievements, DAT descargados, conexión a internet). Se llama antes de abrir
// el formulario o de empezar la acción: si falta algo, lo explica (y ofrece ponerle remedio cuando se puede) y devuelve false.
internal static class Requirements
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, DateTime> Reached = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> CheckAsync(IWin32Window owner, TraceLog trace, string action, Need needs, string? host = null)
    {
        if (needs.HasFlag(Need.ScreenScraper))
        {
            // Si faltan, ofrece registrarlas o darse de alta; si no se consiguen, no se sigue.
            var credentials = ScreenScraperAccount.Ensure(owner, trace, ScreenScraperCredentialStore.Load());
            if (!credentials.HasDeveloper) { trace.Add("requirement_missing", $"action={action}; need=screenscraper"); return false; }
        }
        if (needs.HasFlag(Need.RetroAchievements) && RaAccount.Ensure(owner, trace) is null)
        {
            trace.Add("requirement_missing", $"action={action}; need=retroachievements");
            return false;
        }
        if (needs.HasFlag(Need.Dats) && RomDatabase.InstalledCount() == 0)
        {
            trace.Add("requirement_missing", $"action={action}; need=dats");
            var answer = RequirementDialog.Show(owner, action, "Faltan las bases de datos de ROMs",
                $"«{action}» identifica las ROMs con los DAT (No-Intro, Redump…) y no hay ninguno descargado: sin ellos solo se identifica por extensión.",
                ["Descargarlos en Opciones › Bases de datos de ROMs."],
                ("Continuar sin ellos", DialogResult.OK), ("Cancelar", DialogResult.Cancel));
            if (answer != DialogResult.OK) return false;
        }
        if (needs.HasFlag(Need.Internet))
        {
            host ??= needs.HasFlag(Need.ScreenScraper) ? "api.screenscraper.fr" : needs.HasFlag(Need.RetroAchievements) ? "retroachievements.org"
                : needs.HasFlag(Need.Dats) ? "raw.githubusercontent.com" : "www.google.com";
            while (!await ReachableAsync(host))
            {
                trace.Add("requirement_missing", $"action={action}; need=internet; host={host}");
                // La comprobación puede fallar aunque haya conexión (proxy, red lenta): se puede reintentar o seguir de todos modos.
                var answer = RequirementDialog.Show(owner, action, $"Sin conexión con {host}",
                    $"«{action}» necesita conexión a internet y el servidor {host} no ha respondido.",
                    ["Comprobar que el equipo tiene conexión a internet.", "Revisar el cortafuegos o el antivirus.", "Volver a intentarlo dentro de unos segundos."],
                    ("Reintentar", DialogResult.Retry), ("Continuar de todos modos", DialogResult.Ignore), ("Cancelar", DialogResult.Cancel));
                if (answer == DialogResult.Retry) continue;
                if (answer == DialogResult.Ignore) break;
                return false;
            }
        }
        return true;
    }

    // Alcanzable si se puede abrir una conexión con el puerto 443 del servidor (no depende de lo que conteste) o, si no, si contesta a una petición HTTP
    // (por si hay un proxy). El resultado bueno se recuerda 30 segundos.
    private static async Task<bool> ReachableAsync(string host)
    {
        lock (Reached) if (Reached.TryGetValue(host, out var at) && DateTime.Now - at < TimeSpan.FromSeconds(30)) return true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, 443, timeout.Token);
            lock (Reached) Reached[host] = DateTime.Now;
            return true;
        }
        catch { /* se prueba por HTTP */ }
        try
        {
            using var response = await Http.GetAsync("https://" + host + "/", HttpCompletionOption.ResponseHeadersRead);
            lock (Reached) Reached[host] = DateTime.Now;
            return true;
        }
        catch { return false; }
    }
}

// Aviso de que falta algo para hacer una acción: cabecera naranja con una señal de advertencia, qué pasa, qué se puede hacer (lista) y los botones.
// El primer botón es el principal (naranja).
internal sealed class RequirementDialog : LocalizedForm
{
    private static readonly Color Orange = Color.FromArgb(232, 132, 36), OrangeDark = Color.FromArgb(150, 80, 8), OrangeSoft = Color.FromArgb(255, 244, 228);

    public static DialogResult Show(IWin32Window owner, string action, string headline, string what, string[] steps, params (string Text, DialogResult Result)[] buttons)
    {
        using var dialog = new RequirementDialog(action, headline, what, steps, buttons);
        return dialog.ShowDialog(owner);
    }

    private RequirementDialog(string action, string headline, string what, string[] steps, (string Text, DialogResult Result)[] buttons)
    {
        Text = action;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = Color.White;
        const int width = 580, headerHeight = 80;
        int footerHeight;

        var header = new Panel { Dock = DockStyle.Top, Height = headerHeight, BackColor = Orange };
        header.Paint += (_, ev) => RaUi.PaintHeaderTexture(ev.Graphics, header.ClientRectangle, header.BackColor);
        var icon = new PictureBox { Location = new Point(20, 16), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Image = WarningIcon() };
        var title = new ShadowLabel { Location = new Point(80, 14), Size = new Size(width - 96, 30), Text = headline, AutoEllipsis = true, ForeColor = Color.White, BackColor = Color.Transparent,
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), Tag = null };
        var subtitle = new ShadowLabel { Location = new Point(80, 46), Size = new Size(width - 96, 22), Text = action, AutoEllipsis = true, ForeColor = Color.FromArgb(255, 238, 214), BackColor = Color.Transparent };
        header.Controls.AddRange([icon, title, subtitle]);

        // Cuerpo: qué ocurre (en un recuadro naranja claro) y qué se puede hacer (lista con viñetas).
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 18, 24, 10), BackColor = Color.White };
        var whatBox = new Panel { Width = width - 48, BackColor = OrangeSoft, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 14) };
        var whatLabel = new Label { AutoSize = true, MaximumSize = new Size(width - 48 - 28, 0), Text = what, ForeColor = Color.FromArgb(60, 40, 10), Dock = DockStyle.Top };
        whatBox.Controls.Add(whatLabel);
        body.Controls.Add(whatBox);
        if (steps.Length > 0)
        {
            body.Controls.Add(new Label { AutoSize = true, Text = "Qué puedes hacer", Font = new Font(Font, FontStyle.Bold), ForeColor = OrangeDark, Margin = new Padding(0, 0, 0, 4) });
            foreach (string step in steps)
            {
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(8, 0, 0, 2) };
                row.Controls.Add(new Label { AutoSize = true, Text = "•", ForeColor = Orange, Tag = "noloc", Margin = new Padding(0, 0, 6, 0) });
                row.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(width - 48 - 40, 0), Text = step });
                body.Controls.Add(row);
            }
        }

        // Botones de ancho fijo (entre 110 y 150 px, según el texto ya traducido): si el texto no cabe en una línea se parte en dos y todos los botones del pie
        // pasan a ser más altos. Antes se dimensionaban con el texto en español y, al traducirse, el texto se cortaba.
        const int minButton = 110, maxButton = 150, gap = 8;
        var boldFont = new Font(Font, FontStyle.Bold);
        int[] widths = buttons.Select((b, i) => Math.Clamp(TextRenderer.MeasureText(Loc.T(b.Text), i == 0 ? boldFont : Font).Width + 36, minButton, maxButton)).ToArray();
        bool twoLines = buttons.Select((b, i) => TextRenderer.MeasureText(Loc.T(b.Text), i == 0 ? boldFont : Font).Width + 36 > maxButton).Any(x => x);
        int buttonHeight = twoLines ? 44 : 32;
        footerHeight = twoLines ? 68 : 56;
        var footer = RaUi.Footer(footerHeight);
        int x = width - 20;
        var created = new List<Button>();
        foreach (var (text, result) in buttons.Reverse())
        {
            var button = new Button { Text = text, DialogResult = result, AutoSize = false, Size = new Size(widths[buttons.Length - 1 - created.Count], buttonHeight) };
            created.Add(button);
        }
        for (int i = 0; i < created.Count; i++)
        {
            bool primary = i == created.Count - 1; // el primero de la lista es el principal
            RaUi.StyleButton(created[i], primary, primary ? Orange : null);
            created[i].Size = new Size(created[i].Width, buttonHeight); // StyleButton sube el alto mínimo; se fija el de todo el pie
            created[i].Location = new Point(x - created[i].Width, (footerHeight - buttonHeight) / 2);
            x = created[i].Left - gap;
            footer.Controls.Add(created[i]);
        }
        Controls.AddRange([body, header, footer]);
        // Botón predeterminado el principal; Esc equivale al último («Cancelar»).
        AcceptButton = created[^1];
        CancelButton = created[0];
        int bodyHeight = body.GetPreferredSize(new Size(width, 0)).Height;
        ClientSize = new Size(width, headerHeight + Math.Max(60, bodyHeight) + footerHeight);
    }

    // Triángulo de advertencia con un signo de exclamación.
    private static Bitmap WarningIcon()
    {
        var bitmap = new Bitmap(96, 96);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        PointF[] triangle = [new(48, 8), new(92, 84), new(4, 84)];
        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(OrangeDark, 6F) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        g.FillPolygon(fill, triangle); g.DrawPolygon(edge, triangle);
        using var ink = new SolidBrush(OrangeDark);
        g.FillRectangle(ink, 43, 34, 10, 28);
        g.FillEllipse(ink, 42, 66, 12, 12);
        return bitmap;
    }
}
