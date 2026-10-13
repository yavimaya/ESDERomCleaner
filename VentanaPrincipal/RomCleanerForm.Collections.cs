namespace ESDERomCleaner;

// Una colección guardada: un nombre, la carpeta raíz (la que contiene Roms y ES-DE) y un comentario libre.
// Systems, Roms y Bytes son las cifras del último análisis (null si la colección aún no se ha analizado); el menú las muestra junto al nombre.
internal sealed record SavedCollection(string Name, string Root, string Comment = "", int? Systems = null, int? Roms = null, long? Bytes = null);

// Menú Colecciones: crear una colección con la carpeta actual, guardarla, editar su nombre y comentario, recuperarla más tarde con un clic (se abre y se analiza)
// o quitarla de la lista. Para analizar una carpeta hace falta que pertenezca a una colección. Quitar una colección solo la borra de esta lista: no se toca
// ningún archivo de la carpeta.
public sealed partial class RomCleanerForm
{
    private readonly ToolStripMenuItem collectionsMenu = new("Colecciones");
    private SavedCollection? activeCollectionValue;
    // La colección cargada; al cambiarla, su nombre se pone en el título de todos los formularios.
    private SavedCollection? activeCollection
    {
        get => activeCollectionValue;
        set { activeCollectionValue = value; CollectionTitle.Set(value?.Name, value?.Root); UpdateScanEnabled(); ApplyWelcome(); }
    }

    private static List<SavedCollection> LoadCollections() => AppSettings.Get("Collections", new List<SavedCollection>());

    private static void StoreCollections(List<SavedCollection> list) => AppSettings.Set("Collections", list);

    private static bool SameRoot(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private string CurrentRoot() => root.Text.Trim().Trim('"');

    private void WireCollections()
    {
        // Al arrancar no hay ninguna colección cargada: hay que elegirla en el menú Colecciones. Sin colección cargada no se puede analizar.
        scan.EnabledChanged += (_, _) => { if (scan.Enabled && activeCollectionValue is null) scan.Enabled = false; };
        UpdateScanEnabled();
        collectionsMenu.DropDownOpening += (_, _) => BuildCollectionsMenu();
        BuildCollectionsMenu();
    }

    // Guarda en la colección cargada las cifras del último análisis (sistemas, ROMs y tamaño), para mostrarlas en el menú Colecciones.
    private void UpdateActiveCollectionStats()
    {
        if (activeCollection is not { } active || all.Count == 0) return; // sin análisis no se pisan las cifras guardadas
        var identified = all.Where(r => r.Status == RomStatus.Ok).ToList();
        int systemsCount = identified.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        long bytes = identified.Sum(r => r.Size);
        if (active.Systems == systemsCount && active.Roms == identified.Count && active.Bytes == bytes) return;
        ReplaceCollection(active, active with { Systems = systemsCount, Roms = identified.Count, Bytes = bytes });
    }

    private void UpdateScanEnabled() => scan.Enabled = activeCollectionValue is not null && cts == null;

    private static string CollectionTip(SavedCollection c) => c.Comment.Length > 0 ? c.Root + "\r\n\r\n" + c.Comment : c.Root;

    private void BuildCollectionsMenu()
    {
        collectionsMenu.DropDownItems.Clear();
        bool busy = cts != null;
        var create = new ToolStripMenuItem("Crear colección…") { Enabled = !busy };
        create.Click += (_, _) => CreateCollection();
        var save = new ToolStripMenuItem("Guardar colección") { Enabled = !busy && activeCollection is not null };
        save.Click += (_, _) => SaveActiveCollection();
        var edit = new ToolStripMenuItem("Editar nombre y comentario…") { Enabled = !busy && activeCollection is not null };
        edit.Click += (_, _) => EditActiveCollection();
        var clone = new ToolStripMenuItem("Clonar colección…") { Enabled = !busy };
        clone.Click += (_, _) => OpenCloneDialog();
        var summary = new ToolStripMenuItem("Resumen de la colección…") { Enabled = !busy && activeCollection is not null };
        summary.Click += async (_, _) => await ShowSummaryAsync();
        collectionsMenu.DropDownItems.AddRange([create, save, edit, clone, summary, new ToolStripSeparator()]);

        var list = LoadCollections().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            collectionsMenu.DropDownItems.Add(new ToolStripMenuItem("(ninguna guardada)") { Enabled = false });
        foreach (var collection in list)
        {
            string stats = collection.Roms is { } roms
                ? $"{collection.Systems:N0} {Loc.T("sistemas")} · {roms:N0} ROMs · {FormatSize(collection.Bytes ?? 0)}"
                : Loc.T("sin analizar");
            var item = new ToolStripMenuItem($"{collection.Name}      {stats}") { Tag = "noloc", ToolTipText = CollectionTip(collection), Enabled = !busy };
            item.Checked = activeCollection is not null && activeCollection.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase);
            item.Click += (_, _) => OpenSavedCollection(collection);
            collectionsMenu.DropDownItems.Add(item);
        }

        collectionsMenu.DropDownItems.Add(new ToolStripSeparator());
        var delete = new ToolStripMenuItem("Eliminar colección") { Enabled = !busy && list.Count > 0 };
        foreach (var collection in list)
        {
            var item = new ToolStripMenuItem(collection.Name) { Tag = "noloc", ToolTipText = CollectionTip(collection) };
            item.Click += (_, _) => DeleteSavedCollection(collection);
            delete.DropDownItems.Add(item);
        }
        collectionsMenu.DropDownItems.Add(delete);
    }

    // Crea una colección con la carpeta que hay en «Ruta raíz» y la deja como colección cargada. Devuelve false si se cancela.
    private bool CreateCollection(bool askToSwitch = true)
    {
        var list = LoadCollections();
        string suggestion = "", chosenRoot = "", chosenComment = "";
        while (true)
        {
            // La carpeta se elige aquí, al crear la colección (y ya no se puede cambiar después).
            using var dialog = new CollectionDialog("Crear colección", suggestion, chosenRoot, chosenComment, "Crear", canChooseFolder: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            string name = dialog.CollectionName;
            string rootPath = dialog.Root;
            suggestion = name; chosenRoot = rootPath; chosenComment = dialog.Comment;
            if (list.FirstOrDefault(c => SameRoot(c.Root, rootPath)) is { } sameFolder)
            {
                QuietMessage.Show(this, "Crear colección", $"Esa carpeta ya pertenece a la colección «{sameFolder.Name}».");
                continue;
            }
            if (list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                QuietMessage.Show(this, "Crear colección", $"Ya existe una colección llamada «{name}». Elige otro nombre.");
                continue;
            }
            var created = new SavedCollection(name, rootPath, dialog.Comment);
            list.Add(created);
            StoreCollections(list);
            trace.Add("collection_created", $"name={name}; root={rootPath}");
            status.Text = $"Colección «{name}» creada.";
            // Al crearla no se carga sola: se pregunta. Si se analiza una carpeta sin colección (askToSwitch = false) sí se carga, porque hace falta para seguir.
            if (!askToSwitch || QuietMessage.Confirm(this, "Colección creada",
                $"Colección «{name}» creada.\r\n\r\n¿Quieres cambiar a ella ahora? Se descargará la colección cargada y el formulario quedará vacío; el primer «Listar colección» lo preparará todo.", "Cambiar"))
                SwitchToCollection(created);
            return true;
        }
    }

    // Para analizar una carpeta hace falta una colección: si ya hay una guardada con esa carpeta se usa; si no, se ofrece crearla.
    private bool EnsureActiveCollection()
    {
        // La carpeta de trabajo es siempre la de la colección cargada.
        if (activeCollection is { } current)
        {
            if (!SameRoot(current.Root, CurrentRoot())) root.Text = current.Root;
            return true;
        }
        QuietMessage.Show(this, "Colección", "No hay ninguna colección cargada.\r\n\r\nCarga una desde el menú Colecciones o crea una nueva.");
        status.Text = "Carga o crea una colección en el menú Colecciones.";
        return false;
    }

    // Guarda en la colección cargada la carpeta que hay ahora en «Ruta raíz».
    private void SaveActiveCollection()
    {
        if (activeCollection is not { } active) return;
        string rootPath = CurrentRoot();
        if (!ValidateRoot(rootPath, out string problem)) { QuietMessage.Show(this, "Guardar colección", problem); return; }
        var updated = active with { Root = rootPath };
        ReplaceCollection(active, updated);
        trace.Add("collection_saved", $"name={updated.Name}; root={rootPath}");
        status.Text = $"Colección «{updated.Name}» guardada.";
    }

    private void EditActiveCollection()
    {
        if (activeCollection is not { } active) return;
        var list = LoadCollections();
        string suggestion = active.Name;
        while (true)
        {
            using var dialog = new CollectionDialog("Editar colección", suggestion, active.Root, active.Comment, "Guardar");
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string name = dialog.CollectionName;
            if (!name.Equals(active.Name, StringComparison.OrdinalIgnoreCase) && list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                QuietMessage.Show(this, "Editar colección", $"Ya existe una colección llamada «{name}». Elige otro nombre.");
                suggestion = name;
                continue;
            }
            var updated = active with { Name = name, Comment = dialog.Comment };
            ReplaceCollection(active, updated);
            trace.Add("collection_edited", $"name={name}");
            status.Text = $"Colección «{name}» actualizada.";
            return;
        }
    }

    private void ReplaceCollection(SavedCollection old, SavedCollection updated)
    {
        var list = LoadCollections();
        int index = list.FindIndex(c => c.Name.Equals(old.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = updated; else list.Add(updated);
        StoreCollections(list);
        activeCollection = updated;
    }

    // Cargar una colección: se descarga la anterior, el formulario queda vacío y el primer «Listar colección» lo prepara todo.
    private void OpenSavedCollection(SavedCollection collection)
    {
        if (cts != null) return;
        if (activeCollection is { } already && already.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase))
        {
            status.Text = $"La colección «{collection.Name}» ya está cargada.";
            return;
        }
        if (!Directory.Exists(collection.Root))
        {
            QuietMessage.Show(this, "Recuperar colección", $"La carpeta de la colección «{collection.Name}» ya no existe:\r\n\r\n{collection.Root}");
            return;
        }
        trace.Add("collection_opened", $"name={collection.Name}; root={collection.Root}");
        SwitchToCollection(collection);
    }

    // Deja la colección indicada como la cargada. Si ya se había analizado, se recupera su análisis guardado; si no, empieza de cero y el primer
    // «Listar colección» lo prepara todo.
    private void SwitchToCollection(SavedCollection collection)
    {
        UnloadCollection();
        activeCollection = collection;
        root.Text = collection.Root;
        lastRootPath = collection.Root;
        SaveOptions();
        var cached = CollectionCache.TryLoad(collection.Root);
        if (cached is null || cached.Rows.Count == 0)
        {
            status.Text = $"Colección «{collection.Name}» cargada. Pulsa «Listar colección» para prepararla.";
            return;
        }
        // Análisis guardado: se muestra al instante. «Re-listar colección» lo actualiza si algo ha cambiado fuera del programa.
        loadingFromCache = true;
        try
        {
            all = cached.Rows; accessories = cached.Accessories; systemFileCounts = cached.SystemFileCounts;
            gamelistCount = cached.Gamelists; mediaBytes = cached.MediaBytes; sizesCalculating = false;
            cachePath = Path.Combine(CollectionCache.FolderOf(collection.Root), "database.csv");
            loadedRoot = collection.Root;
            RefreshSystemsList();
            UpdateCollectionCounters();
            UpdateScanButton();
        }
        finally { loadingFromCache = false; }
        _ = RefreshGapsAsync();
        status.Text = $"Colección «{collection.Name}» cargada desde su caché ({cached.SavedUtc.ToLocalTime():g}). Pulsa «Re-listar colección» para actualizarla.";
        trace.Add("collection_cache_loaded", $"name={collection.Name}; roms={cached.Rows.Count}");
    }

    private bool loadingFromCache;

    // Guarda el análisis de la colección cargada para recuperarlo al volver a ella (en segundo plano, sin parar la ventana).
    private void SaveAnalysisCache()
    {
        if (loadingFromCache || activeCollection is not { } active || all.Count == 0) return;
        var snapshot = new CachedAnalysis(all.ToList(), accessories.ToList(), new Dictionary<string, int>(systemFileCounts), gamelistCount, mediaBytes, DateTime.UtcNow);
        string rootPath = active.Root;
        _ = Task.Run(() => { try { CollectionCache.Save(rootPath, snapshot); } catch (Exception ex) { trace.Add("analysis_cache_error", ex.Message); } });
    }

    // «Descarga» la colección cargada: vacía el formulario y la memoria y borra los temporales. Su análisis ya está guardado en su propia caché
    // (collections\<huella>), así que no se pierde. No toca nada de la propia colección ni lo descargado de ScreenScraper (idiomas, perfiles de curado…).
    private void UnloadCollection()
    {
        StopVideoPlayback();
        ResetRomMedia();
        previewVersion++;
        pendingVideoRow = null;
        preview.Image?.Dispose();
        preview.Image = null;
        all = new(); current = new(); accessories = new();
        systemFileCounts = new(StringComparer.OrdinalIgnoreCase);
        gamelistCount = 0; cachePath = "—"; mediaBytes = 0; sizesCalculating = false;
        selected.Clear();
        gaps = null; loadedRoot = null;
        categorizeAfterScan = null; scrapeAfterScan = null; skipUnclassifiedOffer = false;
        MiximageFileIndex.Clear(); VideoFileIndex.Clear();
        changing = true;
        try { systems.Items.Clear(); categories.Items.Clear(); roms.Items.Clear(); }
        finally { changing = false; }
        search.Clear(); details.Clear(); description.Clear();
        systemSummary.Text = "Selecciona un sistema tras analizar.";
        miximageGroup.Text = "Imagen de la ROM enfocada";
        UpdateSelection();
        UpdateCollectionCounters();
        UpdateGapLabels();
        UpdateScanButton();
        // Temporales propios de la aplicación (la caché del análisis de cada colección se conserva).
        foreach (string folder in new[] { Path.Combine(AppPaths.Temp, "expand"), Path.Combine(AppPaths.Temp, "store"), Path.Combine(SsMediaCache.Root, "_lote") })
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
        trace.Add("collection_unloaded", "");
    }
    private void DeleteSavedCollection(SavedCollection collection)
    {
        if (!QuietMessage.Confirm(this, "Eliminar colección",
            $"Se quitará «{collection.Name}» de la lista de colecciones guardadas.\r\n\r\nNo se borra ningún archivo de la colección.", "Eliminar")) return;
        var list = LoadCollections();
        list.RemoveAll(c => c.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase));
        StoreCollections(list);
        CollectionCache.Delete(collection.Root); // su caché de análisis también se va
        if (activeCollection is not null && activeCollection.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase)) { activeCollection = null; UnloadCollection(); }
        trace.Add("collection_removed", $"name={collection.Name}");
        status.Text = $"Colección «{collection.Name}» quitada de la lista.";
    }
}

public sealed partial class RomCleanerForm
{
    private async Task ShowSummaryAsync()
    {
        if (all.Count == 0) { QuietMessage.Show(this, "Resumen de la colección", "Analiza la colección primero: el resumen se hace con los datos del último análisis."); return; }
        var rows = all.Where(r => r.Status == RomStatus.Ok && IsVisibleSystem(r.System)).ToList();
        string rootPath = root.Text.Trim().Trim('"');
        var gapMap = gaps ?? await Task.Run(() => MediaGaps.Compute(rootPath, rows));
        var perSystem = rows.GroupBy(r => r.System, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SummarySystem(g.Key, g.Count(), g.Sum(r => r.Size), gapMap.GetValueOrDefault(g.Key)))
            .ToList();
        var total = perSystem.Aggregate(new GapCount(), (a, p) => a + p.Gaps);
        // Categoría principal: lo que hay antes de « / » (p. ej. «Deportes / Fútbol» cuenta como «Deportes»).
        var categories = rows.Where(r => !NoCategory(r))
            .GroupBy(r => r.Genre.Split(" / ")[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Category: g.Key, Roms: g.Count())).OrderByDescending(c => c.Roms).ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ToList();
        int noCategory = rows.Count(NoCategory);
        if (noCategory > 0) categories.Add((Loc.T("Sin categoría"), noCategory));
        var summary = new CollectionSummary(rootPath, perSystem.Count, rows.Count, all.Count(r => r.Status == RomStatus.Ambiguous), systemFileCounts.Values.Sum(), gamelistCount,
            rows.Sum(r => r.Size), mediaBytes, total, perSystem, categories);
        trace.Add("summary_opened", $"systems={summary.Systems}; roms={summary.Roms}");
        using var dialog = new CollectionSummaryDialog(summary);
        dialog.ShowDialog(this);
    }
}
