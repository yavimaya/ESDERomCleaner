namespace ESDERomCleaner;

// Barra inferior con el uso de CPU del proceso (sobre el total de la máquina), igual que la casilla de la barra de la descarga masiva.
// Con withQuota (formularios que consultan ScreenScraper) lleva además, a la izquierda, las peticiones de hoy, y la CPU pasa a la derecha.
internal sealed class CpuUsageStrip : StatusStrip
{
    private readonly ToolStripStatusLabel cpu = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Uso de CPU del proceso (sobre el total de la máquina).",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private TimeSpan lastCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
    private long lastTick = Environment.TickCount64;

    private readonly ToolStripStatusLabel quota = new()
    {
        AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Peticiones de hoy según ScreenScraper y límite diario de tu cuenta.",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched, Tag = "noloc"
    };
    private readonly bool withQuota;

    // Cuentas conectadas (formulario principal): usuario de ScreenScraper (o invitado) y de RetroAchievements.
    private readonly ToolStripStatusLabel ssAccount = new()
    {
        AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Cuenta de ScreenScraper con la que se consulta (sin usuario es la cuenta anónima de invitado).",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched, Tag = "noloc"
    };
    private readonly ToolStripStatusLabel raAccount = new()
    {
        AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, ToolTipText = "Cuenta de RetroAchievements conectada.",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched, Tag = "noloc"
    };
    private readonly bool withAccounts;

    // Contador de tiempo a la izquierda del todo, para operaciones largas; vacío = oculto.
    private readonly ToolStripStatusLabel timing = new()
    {
        AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Visible = false, Tag = "noloc",
        BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched
    };

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string TimeText { set { timing.Text = value; timing.Visible = value.Length > 0; } }

    public CpuUsageStrip(bool withQuota = false, bool withAccounts = false, bool withTimer = false)
    {
        this.withQuota = withQuota; this.withAccounts = withAccounts;
        Dock = DockStyle.Bottom;
        AutoSize = false;
        SizingGrip = false;
        ShowItemToolTips = true;
        if (withQuota)
        {
            Items.Add(quota);
            Items.Add(new ToolStripStatusLabel { Spring = true });
            cpu.BorderSides = ToolStripStatusLabelBorderSides.Left;
            UpdateQuota();
            ScreenScraperQuota.Changed += OnQuotaChanged;
        }
        if (withAccounts)
        {
            // Logos oficiales (incrustados) a 16 px junto al nombre.
            ssAccount.Image = Logo("screenscraper"); raAccount.Image = Logo("retroachievements");
            ssAccount.ImageAlign = raAccount.ImageAlign = ContentAlignment.MiddleLeft;
            ssAccount.TextImageRelation = raAccount.TextImageRelation = TextImageRelation.ImageBeforeText;
            Items.Add(ssAccount); Items.Add(raAccount);
            if (!withQuota) Items.Add(new ToolStripStatusLabel { Spring = true });
            UpdateAccounts();
            ScreenScraperCredentialStore.Changed += OnAccountsChanged;
            RaCredentialStore.Changed += OnAccountsChanged;
        }
        if (withTimer && !withQuota && !withAccounts) Items.Add(new ToolStripStatusLabel { Spring = true });
        if (withTimer) Items.Insert(0, timing); // el tiempo, a la izquierda del todo; la CPU, a la derecha del todo
        // La CPU, pegada al final de la barra (sin el hueco que el StatusStrip reserva a la derecha para el agarrador) y con el separador solo por delante.
        if (!Items.Cast<ToolStripItem>().Any(i => i is ToolStripStatusLabel { Spring: true })) Items.Add(new ToolStripStatusLabel { Spring = true });
        Padding = new Padding(1, 0, 0, 0);
        cpu.BorderSides = ToolStripStatusLabelBorderSides.Left;
        Items.Add(cpu);
        cpu.Width = TextRenderer.MeasureText("CPU: 100 %", Font).Width + 18;
        timer.Tick += (_, _) => Update();
        timer.Start();
    }

    // Misma altura (22 px lógicos) que la barra de la descarga masiva en todas las ventanas, sea cual sea el autoescalado de la ventana.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = LogicalToDeviceUnits(22);
    }

    // Porcentaje de CPU del proceso desde la última lectura, sobre todos los núcleos.
    private void Update()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var total = process.TotalProcessorTime;
            long now = Environment.TickCount64;
            double elapsedMs = now - lastTick;
            if (elapsedMs <= 0) return;
            double percent = (total - lastCpu).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100;
            lastCpu = total; lastTick = now;
            cpu.Text = $"CPU: {Math.Clamp(percent, 0, 100):0} %";
        }
        catch { cpu.Text = ""; }
    }

    private void OnQuotaChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateQuota); } catch (InvalidOperationException) { }
    }

    // Logo incrustado (Assets\Logos) reducido a 16 px; null si no se puede leer.
    private static Bitmap? Logo(string name)
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"Logos.{name}.png");
            if (stream is null) return null;
            using var source = new Bitmap(stream);
            var icon = new Bitmap(16, 16);
            using var g = Graphics.FromImage(icon);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, 16, 16));
            return icon;
        }
        catch { return null; }
    }

    private void OnAccountsChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateAccounts); } catch (InvalidOperationException) { }
    }

    private void UpdateAccounts()
    {
        string ss = ScreenScraperCredentialStore.Load().UserId;
        ssAccount.Text = string.IsNullOrWhiteSpace(ss) ? Loc.T("Invitado") : ss;
        string ra = RaCredentialStore.Load().UserName;
        raAccount.Text = string.IsNullOrWhiteSpace(ra) ? Loc.T("Sin conectar") : ra;
    }

    private void UpdateQuota() => quota.Text = Loc.T(ScreenScraperQuota.TodayText());

    protected override void Dispose(bool disposing)
    {
        if (disposing && withQuota) ScreenScraperQuota.Changed -= OnQuotaChanged;
        if (disposing && withAccounts) { ScreenScraperCredentialStore.Changed -= OnAccountsChanged; RaCredentialStore.Changed -= OnAccountsChanged; }
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
