using System.Collections.Concurrent;
using System.Diagnostics;

namespace ESDERomCleaner;

internal sealed class MirrorSyncDialog : Form
{
    private string sourcePath;
    private readonly Action<string, string> trace;
    private readonly TextBox sourceBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox destinationBox = new() { ReadOnly = true, Dock = DockStyle.Fill, PlaceholderText = "Selecciona una carpeta de destino" };
    private readonly Button sourceBrowseButton = new() { Text = "Examinar…", Dock = DockStyle.Fill };
    private readonly Button destinationBrowseButton = new() { Text = "Examinar…", Dock = DockStyle.Fill };
    private readonly Button cloneButton = new() { Text = "Clonar", Enabled = false, Width = 100 };
    private readonly Button closeButton = new() { Text = "Cerrar", Width = 90, DialogResult = DialogResult.Cancel };
    private readonly RichTextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, Font = new Font("Consolas", 9F) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Selecciona una carpeta de destino." };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100, Value = 0 };
    private readonly ConcurrentQueue<string> lastRobocopyLines = new();
    private bool operationRunning;

    public MirrorSyncDialog(string sourcePath, Action<string, string> trace)
    {
        this.sourcePath = NormalizePath(sourcePath);
        this.trace = trace;
        Text = "Clonar carpeta de trabajo";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        ClientSize = new Size(900, 620);
        ShowIcon = false;
        BuildLayout();
        sourceBox.Text = this.sourcePath;
        sourceBrowseButton.Click += (_, _) => SelectSource();
        destinationBrowseButton.Click += (_, _) => SelectDestination();
        cloneButton.Click += async (_, _) => await ConfirmAndCloneAsync();
        FormClosing += (_, e) => { if (operationRunning) { e.Cancel = true; status.Text = "Espera a que termine Robocopy para cerrar esta ventana."; } };
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        paths.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        paths.Controls.Add(new Label { Text = "Origen:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        paths.Controls.Add(sourceBox, 1, 0);
        paths.Controls.Add(sourceBrowseButton, 2, 0);
        paths.Controls.Add(new Label { Text = "Destino:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        paths.Controls.Add(destinationBox, 1, 1);
        paths.Controls.Add(destinationBrowseButton, 2, 1);
        shell.Controls.Add(paths, 0, 0);

        var logGroup = new GroupBox { Text = "Resumen de la sincronización", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logGroup.Controls.Add(output);
        shell.Controls.Add(logGroup, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        cloneButton.Margin = new Padding(0, 4, 8, 4);
        closeButton.Margin = new Padding(0, 4, 0, 4);
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(cloneButton);
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(progress, 0, 1);
        footer.Controls.Add(buttons, 1, 0);
        footer.SetRowSpan(buttons, 2);
        shell.Controls.Add(footer, 0, 2);
        Controls.Add(shell);
        AcceptButton = cloneButton;
        CancelButton = closeButton;
    }

    private void SelectDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecciona la carpeta que se sincronizará para que coincida con la carpeta de trabajo.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(destinationBox.Text)) dialog.SelectedPath = destinationBox.Text;
        else if (Directory.Exists(sourcePath)) dialog.SelectedPath = Path.GetPathRoot(sourcePath) ?? "";
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        destinationBox.Text = NormalizePath(dialog.SelectedPath);
        UpdateSelectionState();
    }

    private void SelectSource()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecciona la carpeta de origen que contiene Roms y ES-DE.",
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
        string message = "Se sincronizará el destino para que quede como una imagen espejo del origen mediante Robocopy /MIR.\r\n\r\n"
            + "ORIGEN:\r\n" + sourcePath + "\r\n\r\nDESTINO:\r\n" + destination + "\r\n\r\n"
            + (destinationIsDriveRoot
                ? "ATENCIÓN: el destino es la raíz de una unidad. Se copiarán los archivos nuevos o modificados y se eliminarán de toda la unidad los archivos y carpetas que no existan en el origen.\r\n"
                : "ATENCIÓN: se copiarán los archivos nuevos o modificados y se eliminarán del destino los archivos y carpetas que no existan en el origen.\r\n")
            + "El contenido del origen no se modifica.\r\n\r\n¿Deseas continuar?";
        if (MessageBox.Show(this, message, "Confirmar clonación /MIR", MessageBoxButtons.YesNo, MessageBoxIcon.None, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            trace("mirror_confirmation_cancelled", $"source={sourcePath}; destination={destination}");
            return;
        }

        operationRunning = true;
        cloneButton.Enabled = false;
        sourceBrowseButton.Enabled = false;
        destinationBrowseButton.Enabled = false;
        destinationBox.Enabled = false;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 25;
        status.Text = "Clonando… Robocopy está sincronizando el destino.";
        AppendLine("");
        AppendLine("=== INICIO DE CLONACIÓN /MIR ===");
        trace("mirror_started", $"source={sourcePath}; destination={destination}");
        int exitCode;
        try
        {
            exitCode = await RunRobocopyAsync(destination);
            progress.Style = ProgressBarStyle.Blocks;
            progress.MarqueeAnimationSpeed = 0;
            progress.Value = exitCode < 8 ? 100 : 0;
            bool success = exitCode < 8;
            string result = success
                ? $"Robocopy terminó sin errores críticos (código {exitCode})."
                : $"Robocopy informó de errores (código {exitCode}).";
            AppendLine("RESUMEN DE ROBOCOPY:");
            foreach (string line in lastRobocopyLines)
                AppendLine(line);
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
            cloneButton.Enabled = false;
        }
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
        foreach (string argument in new[] { "/MIR", "/R:1", "/W:1", "/XJ", "/FFT", "/COPY:DAT", "/DCOPY:DAT", "/NFL", "/NDL", "/NJH", "/BYTES" })
            start.ArgumentList.Add(argument);

        while (lastRobocopyLines.TryDequeue(out _)) { }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Windows no pudo iniciar Robocopy.");
        Task stdout = CaptureOutputAsync(process.StandardOutput);
        Task stderr = CaptureOutputAsync(process.StandardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private async Task CaptureOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lastRobocopyLines.Enqueue(line);
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
