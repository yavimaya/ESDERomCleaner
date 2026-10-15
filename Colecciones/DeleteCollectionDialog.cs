namespace ESDERomCleaner;

// Confirmación de «Eliminar colección», con el formato de las demás confirmaciones de borrado (cabecera roja, tarjetas y «BORRAR»/«Cancelar», con este último
// como predeterminado). Siempre se quita de la lista y se borran sus datos de la aplicación (análisis y temporales); además se puede borrar la carpeta de ROMs,
// la estructura de ES-DE (gamelists y medios) o las dos, es decir, todo.
internal sealed class DeleteCollectionDialog : LocalizedForm
{
    private readonly CheckBox romsCheck = new() { Text = "Borrar también la carpeta de ROMs", AutoSize = true };
    private readonly CheckBox esdeCheck = new() { Text = "Borrar también la estructura de ES-DE", AutoSize = true };

    public bool DeleteRoms => romsCheck.Checked;
    public bool DeleteEsDe => esdeCheck.Checked;

    private bool running, finished;

    // «prepare» hace las comprobaciones de seguridad y descarga la colección (devuelve el problema, o null si se puede seguir); «complete» la quita de la lista al terminar.
    public DeleteCollectionDialog(SavedCollection collection, string romsText, string esdeText, Func<bool, bool, string?> prepare, Action<bool, bool, List<string>> complete)
    {
        Text = "Confirmar eliminación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(640, 486);
        var danger = Color.FromArgb(150, 30, 30);

        var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 92), BackColor = danger };
        header.Paint += (_, ev) => RaUi.PaintHeaderTexture(ev.Graphics, header.ClientRectangle, header.BackColor);
        // Icono: la carpeta de la colección con una X roja que se quema de arriba abajo según el porcentaje borrado.
        var icon = new PictureBox { Location = new Point(20, 14), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Image = RaUi.BurningFolderIcon(-1, 0) };
        header.Controls.Add(icon);
        double burn = 0; int tick = 0;
        void ShowIcon(double progress)
        {
            var old = icon.Image;
            icon.Image = RaUi.BurningFolderIcon(progress, tick);
            old?.Dispose();
        }
        var flicker = new System.Windows.Forms.Timer { Interval = 120 };
        flicker.Tick += (_, _) => { tick++; ShowIcon(burn); };
        FormClosed += (_, _) => { flicker.Dispose(); icon.Image?.Dispose(); };
        var title = new ShadowLabel
        {
            Text = "Eliminar colección", Location = new Point(96, 14), Size = new Size(520, 32), AutoSize = false,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
        };
        header.Controls.Add(title);
        var subtitle = new ShadowLabel
        {
            Tag = "noloc", Location = new Point(96, 50), Size = new Size(520, 38), AutoSize = false, ForeColor = Color.FromArgb(255, 226, 226), BackColor = Color.Transparent
        };
        header.Controls.Add(subtitle);

        // Tarjeta con la colección que se va a eliminar: icono de carpeta, nombre y ruta.
        var who = RaUi.Card(new Rectangle(24, 102, 592, 46));
        who.Controls.Add(new PictureBox { Image = RaUi.FolderIcon(2), SizeMode = PictureBoxSizeMode.CenterImage, Location = new Point(10, 7), Size = new Size(32, 32), BackColor = Color.Transparent });
        who.Controls.Add(new Label { Text = collection.Name, Tag = "noloc", Location = new Point(50, 4), Size = new Size(534, 22), AutoSize = false, AutoEllipsis = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), ForeColor = RaUi.Dark });
        who.Controls.Add(new Label { Text = collection.Root, Tag = "noloc", Location = new Point(50, 26), Size = new Size(534, 16), AutoSize = false, AutoEllipsis = true, ForeColor = RaUi.Muted });

        Panel Card(int top, int height, Color back) => new() { Location = new Point(24, top), Size = new Size(592, height), BackColor = back };
        Label Text2(string text, int x, int y, int width, int height, bool noLoc = false, Color? color = null) => new()
        {
            Text = text, Tag = noLoc ? "noloc" : null, Location = new Point(x, y), Size = new Size(width, height), AutoSize = false, ForeColor = color ?? SystemColors.ControlText
        };

        // Lo que siempre se hace.
        var always = Card(154, 64, Color.FromArgb(230, 242, 230));
        always.Controls.Add(Text2("Siempre: quitarla de la lista y borrar sus datos", 14, 8, 564, 22, color: Color.FromArgb(30, 100, 40)));
        always.Controls[0].Font = new Font(Font, FontStyle.Bold);
        always.Controls.Add(Text2("Se borran el análisis guardado (base de datos) y los ficheros temporales de la aplicación.", 14, 32, 564, 28));

        Panel Option(int top, CheckBox check, string description, string detail)
        {
            var card = Card(top, 74, Color.FromArgb(247, 240, 240));
            check.Location = new Point(14, 8); check.Font = new Font(Font, FontStyle.Bold); check.ForeColor = danger;
            card.Controls.Add(check);
            card.Controls.Add(Text2(description, 34, 32, 544, 18));
            card.Controls.Add(Text2(detail, 34, 51, 544, 18, noLoc: true, color: Color.DimGray));
            // Un clic en cualquier parte de la tarjeta marca o desmarca su casilla.
            foreach (Control c in card.Controls.OfType<Label>()) c.Click += (_, _) => check.Checked = !check.Checked;
            card.Click += (_, _) => check.Checked = !check.Checked;
            return card;
        }
        var romsCard = Option(226, romsCheck, "Se borra la carpeta Roms con todos sus sistemas.", romsText);
        var esdeCard = Option(308, esdeCheck, "Se borra la carpeta ES-DE: gamelists y medios descargados.", esdeText);

        var warning = new Panel { Location = new Point(24, 390), Size = new Size(592, 0), BackColor = Color.FromArgb(255, 243, 205), Visible = false };
        warning.Controls.Add(new Label
        {
            Text = "ATENCIÓN: esto no se puede deshacer. Comprueba que la carpeta de la colección es la correcta.",
            Location = new Point(12, 0), Size = new Size(568, 36), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold)
        });

        // Progreso del borrado (sustituye al aviso mientras se borra).
        var progress = new ProgressBar { Location = new Point(24, 390), Size = new Size(592, 20), Visible = false, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
        var statusLabel = new Label { Tag = "noloc", Location = new Point(24, 414), Size = new Size(592, 20), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, Visible = false, ForeColor = RaUi.Dark };

        var cancel = new Button { Text = "Cancelar", Size = new Size(120, 36), Location = new Point(496, 438), DialogResult = DialogResult.Cancel };
        var erase = new Button
        {
            Text = "BORRAR", Size = new Size(150, 36), Location = new Point(336, 438),
            FlatStyle = FlatStyle.Flat, BackColor = danger, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), UseVisualStyleBackColor = false
        };
        erase.FlatAppearance.BorderSize = 0;
        AcceptButton = cancel; CancelButton = cancel;

        void Refresh()
        {
            if (running || finished) return;
            bool any = romsCheck.Checked || esdeCheck.Checked;
            subtitle.Text = Loc.T(any
                ? "Se borrarán de forma permanente las carpetas marcadas. No pasan por la Papelera y no se pueden recuperar."
                : "Se quitará de la lista y se borrarán los datos que la aplicación guarda de ella. Tus archivos no se tocan.");
            warning.Visible = any; warning.Height = any ? 36 : 0;
        }
        // Al marcar una casilla se pide confirmación; si no se acepta, vuelve a quedar desmarcada.
        bool reverting = false;
        void Guard(CheckBox box)
        {
            box.CheckedChanged += (_, _) =>
            {
                if (reverting) return;
                if (box.Checked && !AreYouSureDialog.Ask(this))
                {
                    reverting = true; box.Checked = false; reverting = false;
                }
                Refresh();
            };
        }
        Guard(romsCheck);
        Guard(esdeCheck);
        Refresh();

        erase.Click += async (_, _) =>
        {
            bool roms = romsCheck.Checked, esde = esdeCheck.Checked;
            if (prepare(roms, esde) is { } problem) { QuietMessage.Show(this, "Eliminar colección", problem); return; }
            running = true; burn = 0; flicker.Start();
            foreach (Control c in new Control[] { romsCard, esdeCard, erase, cancel }) c.Enabled = false;
            warning.Visible = false; progress.Visible = statusLabel.Visible = true;
            title.Text = Loc.T("Eliminando colección…"); title.Tag = "noloc";
            subtitle.Text = Loc.T("No cierres la aplicación hasta que termine.");
            statusLabel.Text = Loc.T("Preparando…");
            var folders = new List<string>();
            if (roms) folders.Add(Path.Combine(collection.Root, "Roms"));
            if (esde) folders.Add(Path.Combine(collection.Root, "ES-DE"));
            var errors = new List<string>();
            long files = 0, bytes = 0;
            if (folders.Count > 0)
            {
                var reporter = new Progress<(int Done, int Total)>(p =>
                {
                    if (progress.Style != ProgressBarStyle.Continuous) progress.Style = ProgressBarStyle.Continuous;
                    progress.Maximum = Math.Max(1, p.Total); progress.Value = Math.Min(p.Done, progress.Maximum);
                    statusLabel.Text = $"{p.Done:N0} / {p.Total:N0}";
                    burn = p.Total > 0 ? Math.Min(0.995, p.Done / (double)p.Total) : 0;
                });
                (files, bytes) = await Task.Run(() => DeleteFolders(folders, errors, reporter));
            }
            complete(roms, esde, errors);
            // Aviso final: la cabecera pasa a verde (o ámbar si hubo errores) con el resumen, y el único botón es «Cerrar».
            running = false; finished = true; flicker.Stop(); burn = 1; ShowIcon(1);
            progress.Style = ProgressBarStyle.Continuous; progress.Maximum = 1; progress.Value = 1;
            header.BackColor = errors.Count == 0 ? Color.FromArgb(30, 110, 60) : Color.FromArgb(190, 110, 0);
            title.Text = Loc.T(errors.Count == 0 ? "Colección eliminada" : "Colección eliminada con errores");
            subtitle.ForeColor = Color.White;
            subtitle.Text = folders.Count > 0
                ? $"{files:N0} {Loc.T("archivos borrados")} · {ScreenScraperDialog.FormatBytes(bytes)}"
                : Loc.T("Se ha quitado de la lista y se han borrado sus datos.");
            statusLabel.Text = errors.Count == 0 ? Loc.T("Listo.") : $"{errors.Count:N0} {Loc.T("errores")}: {errors[0]}";
            statusLabel.ForeColor = errors.Count == 0 ? Color.FromArgb(30, 100, 40) : Color.FromArgb(150, 30, 30);
            erase.Visible = false;
            cancel.Text = Loc.T("Cerrar"); cancel.Tag = "noloc"; cancel.Enabled = true; cancel.Location = new Point(496, 438); cancel.Focus();
            DialogResult = DialogResult.None;
        };
        FormClosing += (_, e) => { if (running) e.Cancel = true; };
        Controls.AddRange([header, who, always, romsCard, esdeCard, warning, progress, statusLabel, erase, cancel]);
        Shown += (_, _) => cancel.Focus();
        Loc.LanguageChanged += OnLanguage;
        FormClosed += (_, _) => Loc.LanguageChanged -= OnLanguage;
        void OnLanguage() => Refresh();
    }

    // Borra los archivos uno a uno informando del avance y, al final, las carpetas (y lo que quede, como enlaces). Devuelve archivos y bytes borrados.
    private static (long Files, long Bytes) DeleteFolders(List<string> folders, List<string> errors, IProgress<(int Done, int Total)> progress)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new List<string>();
        foreach (string folder in folders)
            if (Directory.Exists(folder)) files.AddRange(Directory.EnumerateFiles(folder, "*", options));
        int total = files.Count, done = 0;
        long deleted = 0, bytes = 0;
        progress.Report((0, total));
        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                long length = info.Length;
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                bytes += length; deleted++;
            }
            catch (Exception ex) { if (errors.Count < 50) errors.Add($"{file}: {ex.Message}"); }
            if (++done % 25 == 0 || done == total) progress.Report((done, total));
        }
        foreach (string folder in folders)
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) { if (errors.Count < 50) errors.Add($"{folder}: {ex.Message}"); }
        return (deleted, bytes);
    }
}

// Pregunta mínima «¿Estás seguro?»: texto en negrita centrado y los botones centrados abajo («No» es el predeterminado).
internal sealed class AreYouSureDialog : LocalizedForm
{
    private AreYouSureDialog()
    {
        Text = "Confirmar eliminación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(320, 120);
        var danger = Color.FromArgb(150, 30, 30);
        var question = new Label
        {
            Text = "¿Estás seguro?", Dock = DockStyle.Top, Height = 66, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold)
        };
        var yes = new Button
        {
            Text = "Sí, borrar", Size = new Size(120, 32), Location = new Point(34, 74), DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat, BackColor = danger, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), UseVisualStyleBackColor = false
        };
        yes.FlatAppearance.BorderSize = 0;
        var no = new Button { Text = "No", Size = new Size(120, 32), Location = new Point(166, 74), DialogResult = DialogResult.Cancel };
        AcceptButton = no; CancelButton = no;
        Controls.AddRange([question, yes, no]);
        Shown += (_, _) => no.Focus();
    }

    public static bool Ask(IWin32Window owner)
    {
        using var dialog = new AreYouSureDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
