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
        set { activeCollectionValue = value; CollectionTitle.Set(value?.Name, value?.Root); UpdateMainHeader(); UpdateScanEnabled(); ApplyWelcome(); }
    }

    private static List<SavedCollection> LoadCollections() => AppSettings.Get("Collections", new List<SavedCollection>());

    private static void StoreCollections(List<SavedCollection> list) => AppSettings.Set("Collections", list);

    // Ruta canónica: absoluta, sin barra final y resolviendo enlaces simbólicos o uniones, para que dos rutas distintas a la misma carpeta se detecten como iguales.
    private static string Canonical(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var target = Directory.Exists(full) ? new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true) : null;
            return (target?.FullName ?? full).TrimEnd('\\', '/');
        }
        catch { return path.TrimEnd('\\', '/'); }
    }

    private static bool SameRoot(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    // Dos colecciones no pueden compartir la carpeta con la estructura ES-DE (gamelists y medios): se mezclarían al analizar, scrapear o borrar.
    private static bool SharesEsDe(string a, string b) =>
        SameRoot(a, b) || string.Equals(Canonical(Path.Combine(a, "ES-DE")), Canonical(Path.Combine(b, "ES-DE")), StringComparison.OrdinalIgnoreCase);

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
        // Las colecciones guardadas van en un panel de ancho fijo: cabecera dorada y una tarjeta por colección (icono, nombre en negrita y cifras debajo),
        // con la cargada resaltada. Así se distinguen del resto del menú y miden todas lo mismo, sea cual sea el nombre.
        const int panelWidth = 400, cardHeight = 48, headerHeight = 24;
        var panel = new Panel { Width = panelWidth, BackColor = RaUi.Body, Padding = new Padding(8, 0, 8, 6) };
        var tips = new ToolTip { AutoPopDelay = 30000 };
        var cards = new List<Control>();
        foreach (var collection in list)
        {
            string stats = collection.Roms is { } roms
                ? $"{collection.Systems:N0} {Loc.T("sistemas")} · {roms:N0} ROMs · {FormatSize(collection.Bytes ?? 0)}"
                : Loc.T("sin analizar");
            bool active = activeCollection is not null && activeCollection.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase);
            var normal = active ? Color.FromArgb(255, 247, 222) : Color.White;
            var hover = active ? Color.FromArgb(255, 238, 196) : Color.FromArgb(238, 240, 245);
            var card = new Panel { Size = new Size(panelWidth - 16, cardHeight), BackColor = normal, Cursor = busy ? Cursors.Default : Cursors.Hand, Margin = Padding.Empty };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(active ? RaUi.Gold : RaUi.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                if (active) { using var bar = new SolidBrush(RaUi.Gold); e.Graphics.FillRectangle(bar, 0, 0, 5, card.Height); }
            };
            var icon = new PictureBox { Image = folderIcon ??= RaUi.FolderIcon(2), SizeMode = PictureBoxSizeMode.CenterImage, Location = new Point(12, 8), Size = new Size(32, 32), BackColor = Color.Transparent };
            var name = new Label { Text = collection.Name, Tag = "noloc", Location = new Point(52, 5), Size = new Size(card.Width - 60, 20), AutoEllipsis = true, Font = new Font(Font, FontStyle.Bold), ForeColor = busy ? RaUi.Muted : RaUi.Dark, BackColor = Color.Transparent };
            var detail = new Label { Text = stats, Tag = "noloc", Location = new Point(52, 25), Size = new Size(card.Width - 60, 18), AutoEllipsis = true, ForeColor = RaUi.Muted, BackColor = Color.Transparent };
            card.Controls.AddRange([icon, name, detail]);
            foreach (Control c in new Control[] { card, icon, name, detail })
            {
                tips.SetToolTip(c, CollectionTip(collection));
                c.MouseEnter += (_, _) => { if (!busy) card.BackColor = hover; };
                c.MouseLeave += (_, _) => { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) card.BackColor = normal; };
                c.Click += (_, _) => { if (busy) return; collectionsMenu.DropDown.Close(); OpenSavedCollection(collection); };
            }
            cards.Add(card);
        }
        var header = new Label
        {
            Text = Loc.T("Colecciones guardadas").ToUpperInvariant(), Tag = "noloc", Dock = DockStyle.Top, Height = headerHeight, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold), ForeColor = RaUi.Dark, BackColor = RaUi.Gold, Padding = new Padding(8, 0, 0, 0)
        };
        int top = headerHeight + 6;
        if (cards.Count == 0)
            cards.Add(new Label { Text = Loc.T("(ninguna guardada)"), Tag = "noloc", Size = new Size(panelWidth - 16, 24), ForeColor = RaUi.Muted, TextAlign = ContentAlignment.MiddleLeft });
        foreach (var card in cards) { card.Location = new Point(8, top); top += card.Height + 4; panel.Controls.Add(card); }
        panel.Height = top + 2;
        panel.Controls.Add(header);
        collectionsMenu.DropDownItems.Add(new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty });

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

    private bool AskSwitchToCreated(string name)
    {
        using var dialog = new CollectionCreatedDialog(name);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    // Subtítulo de la cabecera principal: la colección cargada con su ruta y la versión (o la invitación a crear o elegir una).
    private void UpdateMainHeader()
    {
        if (mainBanner is null) return;
        string version = AppVersion();
        string what = activeCollection is { } c ? $"{Loc.T("Colección")} «{c.Name}»  ·  {c.Root}" : Loc.T("Crea o selecciona una colección");
        mainBanner.Subtitle.Text = version.Length > 0 ? $"{what}  ·  {version}" : what;
    }

    private static Bitmap? folderIcon;

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
                RequirementDialog.Show(this, "Crear colección", "Carpeta ya en uso", $"Esa carpeta ya pertenece a la colección «{sameFolder.Name}».", ["Elige otra carpeta para la nueva colección."], ("Aceptar", DialogResult.OK));
                continue;
            }
            if (list.FirstOrDefault(c => SharesEsDe(c.Root, rootPath)) is { } sameStructure)
            {
                RequirementDialog.Show(this, "Crear colección", "Carpeta ya en uso", $"La estructura ES-DE de esa carpeta ya la usa la colección «{sameStructure.Name}».", ["Elige otra carpeta para la nueva colección."], ("Aceptar", DialogResult.OK));
                continue;
            }
            if (list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                RequirementDialog.Show(this, "Crear colección", "Nombre ya en uso", $"Ya existe una colección llamada «{name}».", ["Elige otro nombre para la nueva colección."], ("Aceptar", DialogResult.OK));
                continue;
            }
            var created = new SavedCollection(name, rootPath, dialog.Comment);
            list.Add(created);
            StoreCollections(list);
            trace.Add("collection_created", $"name={name}; root={rootPath}");
            status.Text = $"Colección «{name}» creada.";
            // Al crearla no se carga sola: se pregunta. Si se analiza una carpeta sin colección (askToSwitch = false) sí se carga, porque hace falta para seguir.
            if (!askToSwitch || AskSwitchToCreated(name))
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
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendRedrawMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Cambiar de colección enseña de golpe el formulario ya completo: mientras se prepara no se dibuja nada (WM_SETREDRAW) y al final se repinta todo a la vez,
    // en lugar de ir apareciendo los controles uno a uno.
    private void SwitchToCollection(SavedCollection collection)
    {
        if (!IsHandleCreated) { SwitchToCollectionCore(collection); return; }
        SuspendLayout();
        SendRedrawMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
        try { SwitchToCollectionCore(collection); }
        finally
        {
            ResumeLayout(true);
            SendRedrawMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
            Invalidate(true);
            Update();
        }
    }

    private void SwitchToCollectionCore(SavedCollection collection)
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
    // Siempre se quita de la lista y se borran sus datos de la aplicación (análisis y temporales). Opcionalmente se borran también la carpeta de ROMs y/o la de ES-DE.
    // El propio formulario de confirmación hace el borrado con su barra de progreso y avisa al terminar.
    private void DeleteSavedCollection(SavedCollection collection)
    {
        string romsFolder = Path.Combine(collection.Root, "Roms"), esdeFolder = Path.Combine(collection.Root, "ES-DE");
        string romsText = (collection.Roms is { } n ? $"{n:N0} ROMs · {FormatSize(collection.Bytes ?? 0)} · " : "") + romsFolder;

        // Salvaguardas: no se borra desde la raíz de una unidad, ni si otra colección guardada comparte o contiene esa carpeta, ni a través de enlaces simbólicos.
        string? Prepare(bool deleteRoms, bool deleteEsDe)
        {
            var folders = new List<string>();
            if (deleteRoms) folders.Add(romsFolder);
            if (deleteEsDe) folders.Add(esdeFolder);
            if (folders.Count > 0)
            {
                string full = Path.GetFullPath(collection.Root);
                if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)) return "La carpeta de la colección es la raíz de una unidad: por seguridad no se borra desde ahí.";
                if (LoadCollections().FirstOrDefault(c => !c.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase)
                    && (SameRoot(c.Root, collection.Root) || PathRules.Inside(Path.GetFullPath(c.Root), full) || PathRules.Inside(full, Path.GetFullPath(c.Root)))) is { } other)
                    return $"La carpeta se solapa con la de la colección «{other.Name}»: por seguridad no se borra.";
                if (folders.Any(f => Directory.Exists(f) && DeletionService.HasReparsePointInPath(f, collection.Root)))
                    return "La carpeta contiene enlaces simbólicos o uniones: por seguridad no se borra.";
            }
            // Se descarga primero si es la cargada, para que nada (vídeo, miniaturas) mantenga archivos abiertos.
            if (activeCollection is not null && activeCollection.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase)) { activeCollection = null; UnloadCollection(); }
            return null;
        }

        void Complete(bool deleteRoms, bool deleteEsDe, List<string> errors)
        {
            var list = LoadCollections();
            list.RemoveAll(c => c.Name.Equals(collection.Name, StringComparison.OrdinalIgnoreCase));
            StoreCollections(list);
            CollectionCache.Delete(collection.Root); // su caché de análisis (base de datos) también se va
            trace.Add("collection_removed", $"name={collection.Name}; roms_deleted={deleteRoms}; esde_deleted={deleteEsDe}; errors={errors.Count}");
            status.Text = deleteRoms || deleteEsDe ? $"Colección «{collection.Name}» eliminada, con las carpetas marcadas." : $"Colección «{collection.Name}» quitada de la lista.";
        }

        using var dialog = new DeleteCollectionDialog(collection, romsText, esdeFolder, Prepare, Complete);
        dialog.ShowDialog(this);
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
