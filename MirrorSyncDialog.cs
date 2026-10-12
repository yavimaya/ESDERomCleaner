using System.Collections.Concurrent;
using System.Diagnostics;

namespace ESDERomCleaner;

internal sealed class MirrorSyncDialog : LocalizedForm
{
    private string sourcePath;
    private readonly Action<string, string> trace;
    private readonly TextBox sourceBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox destinationBox = new() { ReadOnly = true, Dock = DockStyle.Fill, PlaceholderText = "Selecciona una carpeta de destino" };
    private readonly Button sourceBrowseButton = new() { Text = "Examinar…", Dock = DockStyle.Fill };
    private readonly Button destinationBrowseButton = new() { Text = "Examinar…", Dock = DockStyle.Fill };
    private readonly CloneArrow arrow = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly CheckBox sdCardCheck = new() { Text = "El destino es una tarjeta SD (un solo hilo)", AutoSize = true };
    private readonly Button cloneButton = new() { Text = "Clonar", Enabled = false, Width = 100 };
    private readonly Button closeButton = new() { Text = "Cerrar", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly RichTextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, Font = new Font("Consolas", 9F) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Selecciona una carpeta de destino." };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100, Value = 0 };
    private readonly ConcurrentQueue<string> lastRobocopyLines = new();
    private bool operationRunning;
    private Process? currentProcess;
    private bool stopRequested;
    private long processedLines;
    private DateTime startedAt;
    // Las líneas de Robocopy se muestran todas, volcadas por lotes para no saturar el cuadro de texto.
    private readonly ConcurrentQueue<string> outputQueue = new();
    private readonly System.Windows.Forms.Timer flushTimer = new() { Interval = 150 };

    public MirrorSyncDialog(string sourcePath, Action<string, string> trace)
    {
        this.sourcePath = NormalizePath(sourcePath);
        this.trace = trace;
        Text = "Clonar colección";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 500);
        ClientSize = new Size(640, 580);
        ShowIcon = false;
        BuildLayout();
        sourceBox.Text = this.sourcePath;
        sourceBrowseButton.Click += (_, _) => SelectSource();
        destinationBrowseButton.Click += (_, _) => SelectDestination();
        cloneButton.Click += async (_, _) => { if (operationRunning) StopClone(); else await ConfirmAndCloneAsync(); };
        flushTimer.Tick += (_, _) =>
        {
            FlushOutput();
            if (operationRunning && !stopRequested) status.Text = $"Clonando… {processedLines:N0} entradas procesadas · {(DateTime.Now - startedAt):mm\\:ss}";
        };
        FormClosed += (_, _) => flushTimer.Dispose();
        FormClosing += (_, e) => { if (operationRunning) { e.Cancel = true; status.Text = "Espera a que termine Robocopy para cerrar esta ventana."; } };
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));

        // Origen arriba, destino abajo y, entre los dos, una flecha con un disquete que indica el sentido del clonado.
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        foreach (var box in new[] { sourceBox, destinationBox }) { box.Dock = DockStyle.None; box.Anchor = AnchorStyles.Left | AnchorStyles.Right; box.Margin = new Padding(0, 4, 8, 0); }
        foreach (var button in new[] { sourceBrowseButton, destinationBrowseButton }) { button.Dock = DockStyle.None; button.Anchor = AnchorStyles.Left | AnchorStyles.Right; button.Margin = new Padding(0, 1, 0, 0); button.Height = 28; }
        paths.Controls.Add(new Label { Text = "Origen:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        paths.Controls.Add(sourceBox, 1, 0);
        paths.Controls.Add(sourceBrowseButton, 2, 0);
        paths.Controls.Add(arrow, 0, 1);
        paths.SetColumnSpan(arrow, 3);
        paths.Controls.Add(new Label { Text = "Destino:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        paths.Controls.Add(destinationBox, 1, 2);
        paths.Controls.Add(destinationBrowseButton, 2, 2);
        // Una tarjeta SD se escribe con un solo hilo de Robocopy; con cualquier otro medio se usan 4.
        sdCardCheck.Margin = new Padding(0, 6, 0, 0);
        paths.Controls.Add(sdCardCheck, 1, 3);
        shell.Controls.Add(paths, 0, 0);

        var logGroup = new GroupBox { Text = "Resumen de la sincronización", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logGroup.Controls.Add(output);
        shell.Controls.Add(logGroup, 0, 1);

        // Pie: el estado, los botones centrados y, al final, la barra de progreso fina (como la del formulario principal).
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        status.TextAlign = ContentAlignment.MiddleCenter;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, Anchor = AnchorStyles.None };
        cloneButton.Margin = new Padding(0, 4, 12, 4);
        closeButton.Margin = new Padding(0, 4, 0, 4);
        buttons.Controls.Add(cloneButton);
        buttons.Controls.Add(closeButton);
        var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.Controls.Add(buttons, 1, 0);
        var progressHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        progress.Dock = DockStyle.None;
        progress.Margin = Padding.Empty;
        progressHost.Controls.Add(progress);
        void LayoutProgress()
        {
            int total = progressHost.ClientSize.Width, thickness = 8;
            progress.SetBounds((int)(total * 0.02), Math.Max(0, (progressHost.ClientSize.Height - thickness) / 2), (int)(total * 0.96), thickness);
        }
        progressHost.Resize += (_, _) => LayoutProgress();
        LayoutProgress();
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(buttonRow, 0, 1);
        footer.Controls.Add(progressHost, 0, 2);
        shell.Controls.Add(footer, 0, 2);
        Controls.Add(shell);
        AcceptButton = cloneButton;
        CancelButton = closeButton;
    }
    private void SelectDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("Selecciona la carpeta que se sincronizará para que coincida con la carpeta de trabajo."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(destinationBox.Text)) dialog.SelectedPath = destinationBox.Text;
        else if (Directory.Exists(sourcePath)) dialog.SelectedPath = Path.GetPathRoot(sourcePath) ?? "";
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        destinationBox.Text = NormalizePath(dialog.SelectedPath);
        sdCardCheck.Checked = IsRemovableDrive(destinationBox.Text); // sugerencia: las unidades extraíbles se marcan como tarjeta SD (se puede cambiar)
        UpdateSelectionState();
    }

    private void SelectSource()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T("Selecciona la carpeta de origen que contiene Roms y ES-DE."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (Directory.Exists(sourcePath)) dialog.SelectedPath = sourcePath;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        sourcePath = NormalizePath(dialog.SelectedPath);
        sourceBox.Text = sourcePath;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        output.Clear();
        if (!ValidatePaths(out string error))
        {
            cloneButton.Enabled = false;
            bool sourceValid = ValidateSourcePath(out string sourceError);
            if (!sourceValid)
            {
                status.Text = sourceError;
                AppendLine("ERROR: " + sourceError);
            }
            else if (string.IsNullOrWhiteSpace(destinationBox.Text))
            {
                status.Text = "Origen seleccionado. Selecciona una carpeta de destino.";
                AppendLine("Origen: " + sourcePath);
            }
            else
            {
                status.Text = error;
                AppendLine("ERROR: " + error);
            }
            return;
        }

        cloneButton.Enabled = true;
        status.Text = "Destino seleccionado. Pulsa Clonar para revisar la confirmación.";
        AppendLine("Origen: " + sourcePath);
        AppendLine("Destino: " + destinationBox.Text);
        AppendLine("Aún no se ha iniciado ninguna operación.");
    }

    private static bool IsRemovableDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path) ?? path).DriveType == DriveType.Removable; }
        catch { return false; }
    }

    private bool ValidateSourcePath(out string error)
    {
        error = "";
        if (!Directory.Exists(sourcePath)) { error = "La carpeta de origen ya no existe."; return false; }
        if (!Directory.Exists(Path.Combine(sourcePath, "Roms")) || !Directory.Exists(Path.Combine(sourcePath, "ES-DE")))
        {
            error = "El origen debe ser la carpeta raíz del proyecto y contener Roms y ES-DE.";
            return false;
        }
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "robocopy.exe")))
        {
            error = "No se encuentra Robocopy en Windows.";
            return false;
        }
        return true;
    }

    private bool ValidatePaths(out string error)
    {
        if (!ValidateSourcePath(out error)) return false;
        if (string.IsNullOrWhiteSpace(destinationBox.Text) || !Directory.Exists(destinationBox.Text))
        {
            error = "Selecciona una carpeta de destino existente.";
            return false;
        }
        string destination = NormalizePath(destinationBox.Text);
        if (destination.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) || IsInside(sourcePath, destination) || IsInside(destination, sourcePath))
        {
            error = "El destino no puede ser la carpeta de origen, un padre suyo ni estar dentro de ella.";
            return false;
        }
        string windows = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string programFiles = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string userProfile = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (destination.Equals(windows, StringComparison.OrdinalIgnoreCase) || IsInside(windows, destination)
            || destination.Equals(programFiles, StringComparison.OrdinalIgnoreCase) || IsInside(programFiles, destination)
            || destination.Equals(userProfile, StringComparison.OrdinalIgnoreCase) || IsInside(userProfile, destination))
        {
            error = "El destino está en una carpeta protegida del sistema o del perfil de usuario.";
            return false;
        }
        return true;
    }

    private async Task ConfirmAndCloneAsync()
    {
        if (!ValidatePaths(out string error))
        {
            cloneButton.Enabled = false;
            status.Text = error;
            AppendLine("ERROR: " + error);
            return;
        }

        string destination = NormalizePath(destinationBox.Text);
        bool destinationIsDriveRoot = destination.Equals(
            NormalizePath(Path.GetPathRoot(destination) ?? destination),
            StringComparison.OrdinalIgnoreCase);
        bool confirmed;
        using (var confirm = new CloneConfirmDialog(sourcePath, destination, destinationIsDriveRoot)) confirmed = confirm.ShowDialog(this) == DialogResult.OK;
        if (!confirmed)
        {
            trace("mirror_confirmation_cancelled", $"source={sourcePath}; destination={destination}");
            return;
        }
        operationRunning = true;
        stopRequested = false;
        processedLines = 0;
        startedAt = DateTime.Now;
        while (outputQueue.TryDequeue(out _)) { }
        cloneButton.Text = "Parar"; // mientras clona, el botón detiene Robocopy
        arrow.Active = true; // el disquete parpadea mientras se copia
        cloneButton.Enabled = true;
        flushTimer.Start();
        sourceBrowseButton.Enabled = false;
        destinationBrowseButton.Enabled = false;
        destinationBox.Enabled = false;
        sdCardCheck.Enabled = false;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 25;
        status.Text = "Clonando… Robocopy está sincronizando el destino.";
        AppendLine("");
        AppendLine("=== INICIO DE CLONACIÓN /MIR ===");
        int threads = sdCardCheck.Checked ? 1 : 4;
        AppendLine($"Hilos de Robocopy: {threads}" + (sdCardCheck.Checked ? " (tarjeta SD)" : ""));
        trace("mirror_started", $"source={sourcePath}; destination={destination}; sd={sdCardCheck.Checked}; threads={threads}");
        int exitCode;
        try
        {
            exitCode = await RunRobocopyAsync(destination);
            flushTimer.Stop();
            FlushOutput(all: true);
            if (stopRequested)
            {
                progress.Style = ProgressBarStyle.Blocks;
                progress.MarqueeAnimationSpeed = 0;
                progress.Value = 0;
                AppendLine("=== CLONACIÓN DETENIDA ===");
                status.Text = "Clonación detenida.";
                trace("mirror_stopped", $"source={sourcePath}; destination={destination}");
                QuietMessage.Show(this, "Clonar colección", "Has detenido la clonación. El destino puede haber quedado a medias: vuelve a clonar para completarlo.");
                return;
            }
            progress.Style = ProgressBarStyle.Blocks;
            progress.MarqueeAnimationSpeed = 0;
            progress.Value = exitCode < 8 ? 100 : 0;
            bool success = exitCode < 8;
            string result = success
                ? $"Robocopy terminó sin errores críticos (código {exitCode})."
                : $"Robocopy informó de errores (código {exitCode}).";
            AppendLine("=== RESUMEN FINAL ===");
            AppendLine($"Origen: {sourcePath}");
            AppendLine($"Destino: {destination}");
            AppendLine(result);
            status.Text = success ? $"Clonación terminada (código {exitCode})." : $"Clonación con errores (código {exitCode}).";
            trace(success ? "mirror_completed" : "mirror_failed", $"exitCode={exitCode}; source={sourcePath}; destination={destination}");
            MessageBox.Show(this, result + "\r\n\r\nOrigen: " + sourcePath + "\r\nDestino: " + destination,
                "Resultado de la clonación", MessageBoxButtons.OK, success ? MessageBoxIcon.None : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            progress.Style = ProgressBarStyle.Blocks;
            progress.MarqueeAnimationSpeed = 0;
            progress.Value = 0;
            status.Text = "La clonación terminó con un error.";
            AppendLine("ERROR: " + ex.Message);
            trace("mirror_error", $"source={sourcePath}; destination={destination}; error={ex}");
            MessageBox.Show(this, "No se pudo completar la clonación. Consulta el registro.\r\n\r\n" + ex.Message,
                "Error de clonación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            operationRunning = false;
            sourceBrowseButton.Enabled = true;
            destinationBrowseButton.Enabled = true;
            destinationBox.Enabled = true;
            sdCardCheck.Enabled = true;
            arrow.Active = false;
            cloneButton.Text = "Clonar";
            cloneButton.Enabled = false;
            flushTimer.Stop();
            FlushOutput(all: true);
        }
    }

    // Detiene Robocopy (y sus procesos hijos). Lo ya copiado o borrado en el destino se queda como estaba en ese momento.
    private void StopClone()
    {
        if (currentProcess is not { } process) return;
        stopRequested = true;
        cloneButton.Enabled = false;
        status.Text = "Parando…";
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ya había terminado */ }
    }

    private void FlushOutput(bool all = false)
    {
        if (IsDisposed || outputQueue.IsEmpty) return;
        var text = new System.Text.StringBuilder();
        int taken = 0;
        while ((all || taken < 600) && outputQueue.TryDequeue(out var line)) { text.AppendLine(line); taken++; }
        if (text.Length == 0) return;
        if (output.TextLength > 1_000_000) { output.Select(0, 400_000); output.SelectedText = ""; }
        output.AppendText(text.ToString());
        output.SelectionStart = output.TextLength;
        output.ScrollToCaret();
    }

    private async Task<int> RunRobocopyAsync(string destination)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Robocopy escribe en la página de códigos OEM de la consola (850 en español), no en UTF-8: sin esto «sábado» sale como «s bado».
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            start.StandardOutputEncoding = oem;
            start.StandardErrorEncoding = oem;
        }
        catch (ArgumentException) { /* se usa la codificación predeterminada */ }
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add(destination);
        foreach (string argument in new[] { "/MIR", "/R:1", "/W:1", "/XJ", "/FFT", "/COPY:DAT", "/DCOPY:DAT", "/NJH", "/BYTES", "/NP", $"/MT:{(sdCardCheck.Checked ? 1 : 4)}" })
            start.ArgumentList.Add(argument);

        while (lastRobocopyLines.TryDequeue(out _)) { }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Windows no pudo iniciar Robocopy.");
        currentProcess = process;
        Task stdout = CaptureOutputAsync(process.StandardOutput);
        Task stderr = CaptureOutputAsync(process.StandardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        currentProcess = null;
        return process.ExitCode;
    }

    private async Task CaptureOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lastRobocopyLines.Enqueue(line);
            outputQueue.Enqueue(line);
            if (line.Trim().Length > 0) Interlocked.Increment(ref processedLines);
            while (lastRobocopyLines.Count > 24)
                lastRobocopyLines.TryDequeue(out _);
        }
    }

    private void AppendLine(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLine(line))); } catch (InvalidOperationException) { }
            return;
        }
        output.AppendText(line + Environment.NewLine);
        output.SelectionStart = output.TextLength;
        output.ScrollToCaret();
    }

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        string root = Path.GetPathRoot(full) ?? full;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInside(string parent, string candidate)
    {
        string prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

// Flecha hacia abajo con un disquete al lado: indica que se clona del origen (arriba) al destino (abajo).
// Mientras «Active» es true, el disquete parpadea levemente (su opacidad sube y baja) para indicar que se está copiando.
internal sealed class CloneArrow : Control
{
    private readonly System.Windows.Forms.Timer blink = new() { Interval = 60 };
    private double phase;
    private bool active;

    public CloneArrow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        blink.Tick += (_, _) => { phase += 0.28; Invalidate(); };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set { active = value; phase = 0; if (value) blink.Start(); else blink.Stop(); Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) blink.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cx = Width / 2, top = 3, bottom = Height - 3;
        // Flecha: tallo y punta, de arriba (origen) abajo (destino).
        using (var pen = new Pen(Color.FromArgb(40, 110, 200), 5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round })
        {
            pen.CustomEndCap = new System.Drawing.Drawing2D.AdjustableArrowCap(4.5f, 4.5f, true);
            g.DrawLine(pen, cx, top, cx, bottom);
        }
        // Disquete (a la izquierda de la flecha): carcasa azul, ventana metálica arriba y etiqueta blanca abajo. Opacidad entre ~60 % y 100 % al parpadear.
        int alpha = active ? (int)(255 * (0.8 + 0.2 * Math.Sin(phase))) : 255;
        Color Fade(int r, int gr, int b) => Color.FromArgb(alpha, r, gr, b);
        int size = 30, x = cx - 34 - size, y = (Height - size) / 2;
        using (var body = new SolidBrush(Fade(40, 70, 160))) g.FillRectangle(body, x, y, size, size);
        using (var cut = new SolidBrush(BackColor)) g.FillPolygon(cut, new[] { new Point(x + size - 6, y), new Point(x + size, y), new Point(x + size, y + 6) });
        using (var metal = new SolidBrush(Fade(205, 210, 220))) g.FillRectangle(metal, x + 7, y, size - 15, 10);
        using (var slot = new SolidBrush(Fade(60, 60, 80))) g.FillRectangle(slot, x + 18, y + 2, 4, 6);
        using (var label = new SolidBrush(Fade(255, 255, 255))) g.FillRectangle(label, x + 5, y + 15, size - 10, size - 17);
        using (var line = new Pen(Fade(150, 160, 190))) { g.DrawLine(line, x + 8, y + 19, x + size - 8, y + 19); g.DrawLine(line, x + 8, y + 23, x + size - 8, y + 23); }
    }
}