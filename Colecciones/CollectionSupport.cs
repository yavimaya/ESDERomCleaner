using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ESDERomCleaner;

// Resultado del último análisis de una colección, guardado para recuperarlo al volver a ella sin analizar otra vez.
internal sealed record CachedAnalysis(List<RomRow> Rows, List<AccessoryFile> Accessories, Dictionary<string, int> SystemFileCounts, int Gamelists, long MediaBytes, DateTime SavedUtc);

// Caché por colección: cada colección tiene su carpeta «collections\<huella>» dentro de la carpeta de datos, con el análisis (analysis.json) y el CSV (database.csv).
// La huella sale de la carpeta de la colección (que no cambia después de crearla), así que no depende del nombre, que sí se puede editar.
internal static class CollectionCache
{
    public static string KeyOf(string root)
    {
        string normalized = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    public static string FolderOf(string root) => Path.Combine(AppPaths.Data, "collections", KeyOf(root));

    private static string AnalysisPath(string root) => Path.Combine(FolderOf(root), "analysis.json");

    public static void Save(string root, CachedAnalysis data)
    {
        string folder = FolderOf(root);
        Directory.CreateDirectory(folder);
        string target = AnalysisPath(root), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data), new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    public static CachedAnalysis? TryLoad(string root)
    {
        try
        {
            string path = AnalysisPath(root);
            if (!File.Exists(path)) return null;
            var loaded = JsonSerializer.Deserialize<CachedAnalysis>(File.ReadAllText(path));
            return loaded is null ? null : loaded with { SystemFileCounts = new Dictionary<string, int>(loaded.SystemFileCounts ?? [], StringComparer.OrdinalIgnoreCase) };
        }
        catch { return null; }
    }

    public static void Delete(string root)
    {
        try { string folder = FolderOf(root); if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
    }
}

// Nombre de la colección actual en la barra de título de todos los formularios, pegado al título normal: «Título     -     Colección "nombre"» (o «Crea o selecciona una colección» si no hay ninguna cargada).
// Se escribe directamente en la ventana (SetWindowText), sin tocar la propiedad Text: así las traducciones siguen trabajando con el título normal.
internal static class CollectionTitle
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);

    public static string? Name { get; private set; }
    public static string? Path { get; private set; }
    private static event Action? Changed;

    public static void Set(string? name, string? path = null)
    {
        string? clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        string? cleanPath = clean is null || string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (clean == Name && cleanPath == Path) return;
        Name = clean;
        Path = cleanPath;
        Changed?.Invoke();
    }

    // Engancha un formulario: pone el título al mostrarse, al cambiar de tamaño o de texto y cuando cambia la colección.
    public static void Attach(Form form)
    {
        void Refresh() { if (!form.IsDisposed && form.IsHandleCreated) Apply(form); }
        void OnChanged() { if (!form.IsDisposed && form.IsHandleCreated) try { form.BeginInvoke(Refresh); } catch (InvalidOperationException) { } }
        form.Shown += (_, _) => Refresh();
        form.Resize += (_, _) => Refresh();
        form.TextChanged += (_, _) => OnChanged();      // se reaplica cuando el programa o la traducción cambian el título
        Changed += OnChanged;
        form.FormClosed += (_, _) => Changed -= OnChanged;
        if (form.Visible) Refresh();
    }

    // El título normal de cada formulario y el último que se escribió con el nombre, para no volver a decorar un título ya decorado
    // (Form.Text devuelve el texto real de la ventana, que es el decorado).
    private sealed class TitleState { public string? Base, Applied; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, TitleState> States = new();

    public static void Apply(Form form)
    {
        var state = States.GetOrCreateValue(form);
        string current = form.Text;
        if (state.Applied is null || current != state.Applied) state.Base = current; // el programa o la traducción han puesto un título nuevo
        string label = Name is null ? Loc.T("Crea o selecciona una colección") : $"{Loc.T("Colección")} \"{Name}\"" + (Path is null ? "" : $"     -     {Path}");
        string title = $"{state.Base}     -     {label}";
        state.Applied = title;
        SetWindowText(form.Handle, title);
    }
}

// Datos de una colección: nombre, carpeta y un comentario libre. La carpeta solo se elige al crearla; en una colección ya creada no se puede cambiar.
internal sealed class CollectionDialog : LocalizedForm
{
    private readonly TextBox nameBox = new();
    private readonly TextBox folderBox = new() { ReadOnly = true };
    private readonly TextBox commentBox = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly Button ok;
    private readonly bool canChooseFolder;
    private bool nameEdited;

    public string CollectionName => nameBox.Text.Trim();
    public string Root => folderBox.Text.Trim();
    public string Comment => commentBox.Text.Trim();

    public CollectionDialog(string title, string name, string root, string comment, string okText, bool canChooseFolder = false)
    {
        this.canChooseFolder = canChooseFolder;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(520, 300);

        var nameLabel = new Label { Text = "Nombre:", Location = new Point(12, 16), Size = new Size(90, 22) };
        nameBox.Location = new Point(110, 13); nameBox.Size = new Size(398, 23); nameBox.Text = name; nameBox.MaxLength = 80;
        var folderLabel = new Label { Text = "Carpeta:", Location = new Point(12, 50), Size = new Size(90, 22) };
        folderBox.Location = new Point(110, 47); folderBox.Text = root; folderBox.Tag = "noloc";
        var commentLabel = new Label { Text = "Comentario:", Location = new Point(12, 92), Size = new Size(90, 22) };
        commentBox.Location = new Point(110, 90); commentBox.Size = new Size(398, 150); commentBox.Text = comment; commentBox.MaxLength = 2000;
        ok = new Button { Text = okText, Size = new Size(110, 28), Location = new Point(288, 256), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Size = new Size(90, 28), Location = new Point(404, 256), DialogResult = DialogResult.Cancel };
        AcceptButton = ok; CancelButton = cancel;
        Controls.AddRange([nameLabel, nameBox, folderLabel, folderBox, commentLabel, commentBox, ok, cancel]);

        if (canChooseFolder)
        {
            folderBox.Size = new Size(296, 23);
            var browse = new Button { Text = "Examinar…", Location = new Point(412, 45), Size = new Size(96, 27) };
            browse.Click += (_, _) => BrowseFolder();
            Controls.Add(browse);
        }
        else
        {
            // En una colección ya creada la carpeta no se puede cambiar: solo se muestra.
            folderBox.Size = new Size(398, 23);
        }
        nameBox.TextChanged += (_, _) => { if (nameBox.Focused) nameEdited = true; UpdateOk(); };
        UpdateOk();
        Shown += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
    }

    private void UpdateOk() => ok.Enabled = nameBox.Text.Trim().Length > 0 && (!canChooseFolder || Directory.Exists(folderBox.Text.Trim()));

    private void BrowseFolder()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = Loc.T("Selecciona la carpeta de la colección (la que contiene Roms y ES-DE; puedes crear una nueva)."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(folderBox.Text)) picker.SelectedPath = folderBox.Text;
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        folderBox.Text = picker.SelectedPath;
        // Si todavía no se ha escrito un nombre propio, se propone el de la carpeta.
        if (!nameEdited) nameBox.Text = Path.GetFileName(picker.SelectedPath.TrimEnd('\\', '/')) is { Length: > 0 } folderName ? folderName : picker.SelectedPath;
        UpdateOk();
    }
}
