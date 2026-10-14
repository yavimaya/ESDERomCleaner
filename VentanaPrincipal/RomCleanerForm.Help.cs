using System.Diagnostics;

namespace ESDERomCleaner;

// Menú Ayuda: enlace al repositorio y ventana «Acerca de» con los terceros que usa la aplicación, su licencia y su web.
public sealed partial class RomCleanerForm
{
    internal const string RepositoryUrl = "https://github.com/yavimaya/ESDERomCleaner";

    private readonly ToolStripMenuItem helpMenu = new("Ayuda");

    internal static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* sin navegador predeterminado: no se hace nada */ }
    }

    private void WireHelp()
    {
        helpMenu.DropDownItems.Add("Repositorio ESDERomCleaner", null, (_, _) => { OpenUrl(RepositoryUrl); trace.Add("help_repository_opened", RepositoryUrl); });
        helpMenu.DropDownItems.Add("Acerca de…", null, (_, _) =>
        {
            using var dialog = new AboutDialog();
            dialog.ShowDialog(this);
        });
    }
}

// Qué usa la aplicación y con qué licencia. Lo que se descarga en tiempo de ejecución (DAT, metadatos, medios) no va incluido en el programa.
internal sealed class AboutDialog : LocalizedForm
{
    private sealed record Credit(string Kind, string Name, string Use, string License, string Url);

    private static readonly Credit[] Credits =
    [
        new("Incluido en el programa", ".NET (Microsoft)", "Entorno de ejecución", "MIT", "https://dotnet.microsoft.com/"),
        new("Incluido en el programa", "LibVLC / LibVLCSharp (VideoLAN)", "Reproducción de vídeo", "LGPL 2.1 o posterior", "https://www.videolan.org/"),
        new("Incluido en el programa", "SharpCompress", "Leer archivos 7z y rar", "MIT", "https://github.com/adamhathcock/sharpcompress"),
        new("Incluido en el programa", "System.IO.Hashing (Microsoft)", "Cálculo de CRC32", "MIT", "https://github.com/dotnet/runtime"),
        new("Incluido en el programa", "7-Zip (7za.exe)", "Crear archivos 7z", "LGPL", "https://www.7-zip.org/"),
        new("Incluido en el programa", "Iconos de los sistemas (libretro, dot-art)", "Resumen de la colección", "CC BY 4.0", "https://github.com/libretro/retroarch-assets"),
        new("Incluido en el programa", "Logotipo de RetroAchievements", "Icono del sistema custom «RetroAchievements»", "Marca de RetroAchievements (solo para identificar su servicio)", "https://retroachievements.org/"),
        new("Descargado al usarlo", "RetroAchievements (API web)", "Logros, puntos y progreso de los juegos", "Condiciones de la API de RetroAchievements (uso personal con tu clave)", "https://api-docs.retroachievements.org/"),
        new("Descargado al usarlo", "libretro-database", "DAT de No-Intro, Redump, TOSEC, MAME y FBNeo para identificar ROMs", "CC BY-SA 4.0", "https://github.com/libretro/libretro-database"),
        new("Descargado al usarlo", "ScreenScraper", "Metadatos y medios de los juegos", "Condiciones de la API de ScreenScraper (uso personal con tu cuenta)", "https://www.screenscraper.fr/"),
        new("Referencia", "ES-DE", "Estructura de carpetas, sistemas y extensiones", "MIT", "https://es-de.org/"),
    ];

    public AboutDialog()
    {
        Text = "Acerca de ESDERomCleaner";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(960, 440);

        var grid = new StaticGrid { Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        grid.ApplyInfoStyle(24, 26);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tipo", AutoSizeMode = fill, FillWeight = 17, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Elemento", AutoSizeMode = fill, FillWeight = 24, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Para qué se usa", AutoSizeMode = fill, FillWeight = 31, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Licencia", AutoSizeMode = fill, FillWeight = 24, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewLinkColumn { HeaderText = "Web", AutoSizeMode = fill, FillWeight = 26, SortMode = DataGridViewColumnSortMode.NotSortable, LinkBehavior = LinkBehavior.HoverUnderline });
        foreach (var credit in Credits)
        {
            int index = grid.Rows.Add(credit.Kind, credit.Name, credit.Use, credit.License, credit.Url.Replace("https://", "").TrimEnd('/'));
            grid.Rows[index].Tag = credit.Url;
            grid.Rows[index].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        }
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 4 && grid.Rows[e.RowIndex].Tag is string url) RomCleanerForm.OpenUrl(url); };

        var note = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = SystemColors.GrayText, Padding = new Padding(10, 6, 10, 0),
            Text = "Pulsa el enlace de la última columna para abrir su web.\r\nLas imágenes, vídeos y textos que descargas de ScreenScraper son de sus propietarios: la aplicación solo los guarda en tu colección y no los redistribuye."
        };
        RaUi.StyleGrid(grid, 24, 26, selectionVisible: false);
        note.BackColor = RaUi.Body;
        var close = new Button { Text = "Cerrar", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel };
        RaUi.StyleButton(close, true);
        var footer = RaUi.Footer(52);
        close.Location = new Point(960 - 20 - 90, 10);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        footer.Controls.Add(close);
        // Cabecera oscura con el nombre, la versión y la licencia; el enlace del repositorio va en el pie de la cabecera.
        var banner = new RaHeader("ESDERomCleaner", $"{RomCleanerForm.AppVersion()} · GPL 3.0 · {RomCleanerForm.RepositoryUrl}", 68, 48, logo: false);
        banner.Subtitle.Tag = "noloc"; banner.Subtitle.Cursor = Cursors.Hand;
        banner.Subtitle.Click += (_, _) => RomCleanerForm.OpenUrl(RomCleanerForm.RepositoryUrl);
        banner.Icon.Image = RaUi.InfoIcon();
        var frame = RaUi.Frame(grid, dock: DockStyle.Fill);
        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 6), BackColor = RaUi.Body };
        gridHost.Controls.Add(frame);
        note.Dock = DockStyle.Bottom; note.Height = 52;
        Controls.Add(gridHost); Controls.Add(note); Controls.Add(footer); Controls.Add(banner);
        BackColor = RaUi.Body;
        ClientSize = new Size(960, 440 + 68);
        CancelButton = close;
    }
}
