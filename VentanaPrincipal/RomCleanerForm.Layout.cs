using System.Net;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ESDERomCleaner;

public sealed partial class RomCleanerForm : LocalizedForm
{
    private Label? welcomeLabel; private TableLayoutPanel? welcomeShell; private Control[] welcomeParts = []; private Control? welcomeFooter;

    // Menús que solo se muestran con una colección cargada.
    private ToolStripMenuItem[] collectionMenus = [];

    private void ApplyWelcome()
    {
        bool loaded = activeCollectionValue is not null;
        foreach (var m in collectionMenus) m.Visible = loaded;
        createGamelists.Enabled = loaded; showAccessory.Enabled = loaded; // opciones que actúan sobre la colección
        if (welcomeLabel is null || welcomeShell is null) return;
        foreach (var c in welcomeParts) c.Visible = loaded;
        if (welcomeFooter is not null) welcomeFooter.Visible = loaded;
        welcomeShell.RowStyles[4].Height = loaded ? 144 : 0;
        welcomeLabel.Visible = !loaded;
    }

    private void BuildLayout()
    {
        AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9F);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 5 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent,100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute,144));
        var menu=new ClickThroughMenuStrip { Dock=DockStyle.Fill, AutoSize=true }; var tm=new ToolStripMenuItem("Trazabilidad"); tm.DropDownItems.Add("Ver registro de trazas…",null,(_,_)=>ShowTrace()); var optionsMenu = new ToolStripMenuItem("Opciones"); optionsMenu.DropDownItems.Add(createGamelists); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(backupOption); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(showVideos); optionsMenu.DropDownItems.Add(videoSound); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(showAccessory); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(dataFolderMenu); optionsMenu.DropDownItems.Add(tempFolderMenu); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(imageMenu); optionsMenu.DropDownItems.Add(languageMenu); optionsMenu.DropDownItems.Add(regionOrderMenu); menu.Items.Add(optionsMenu); menu.Items.Add(collectionsMenu); var romsMenu=new ToolStripMenuItem("Añadir ROMs"); romsMenu.DropDownItems.Add(addRoms); romsMenu.DropDownItems.Add(datManager); menu.Items.Add(romsMenu); var curationMenu=new ToolStripMenuItem("Curado"); curationMenu.Click += (_, _) => curationRules.PerformClick(); /* con una sola opción, el menú abre directamente las reglas de curado */ menu.Items.Add(curationMenu); var scrapeMenu=new ToolStripMenuItem("Scrapeo"); scrapeMenu.Click += (_, _) => bulkMedia.PerformClick(); /* con una sola opción, el menú abre directamente la descarga masiva */ menu.Items.Add(scrapeMenu); var raMenu=new ToolStripMenuItem("RetroAchievements") { Tag = "noloc" }; raMenu.DropDownItems.Add(raScan); raMenu.DropDownItems.Add(raOnlyWith); raMenu.DropDownItems.Add(raCustom); menu.Items.Add(raMenu); var credentialsMenu=new ToolStripMenuItem("Credenciales"); credentialsMenu.DropDownItems.Add(screenScraperCredentials); credentialsMenu.DropDownItems.Add(screenScraperSignUp); credentialsMenu.DropDownItems.Add(new ToolStripSeparator()); credentialsMenu.DropDownItems.Add(raCredentials); menu.Items.Add(credentialsMenu); collectionMenus = [romsMenu, scrapeMenu, raMenu, curationMenu]; menu.Items.Add(tm); menu.Items.Add(helpMenu); MainMenuStrip=menu; shell.Controls.Add(menu,0,0);
        var rootLine = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 42, Padding = new Padding(10, 4, 10, 4), ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        rootLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Dock = DockStyle.None;
        root.Width = 430;
        root.Anchor = AnchorStyles.Left;
        root.Margin = Padding.Empty;
        browse.AutoSize = false;
        browse.Width = 86;
        browse.Height = 30;
        browse.Dock = DockStyle.None;
        browse.Margin = Padding.Empty;
        var pathLine = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = Padding.Empty, Margin = Padding.Empty };
        pathLine.Controls.Add(root);
        pathLine.Controls.Add(browse);
        cloneButton.AutoSize = false;
        cloneButton.Size = new Size(92, 30);
        cloneButton.Margin = new Padding(6, 0, 0, 0);
        // La carpeta de la colección ya no se escribe ni se examina aquí: se elige al crear la colección. «root» queda oculto y guarda la ruta de la colección cargada.
        var actionLine = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        actionLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29)); // la primera columna del formulario: el botón va centrado en ella
        actionLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 71));
        actionLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scan.AutoSize = false;
        scan.Size = new Size(142, 30);
        scan.Margin = new Padding(0, 0, 8, 0);
        cancel.AutoSize = false;
        cancel.Size = new Size(142, 30);
        cancel.MinimumSize = new Size(142, 30);
        cancel.MaximumSize = new Size(142, 30);
        cancel.Margin = Padding.Empty;
        scan.Dock = DockStyle.None;
        scan.Anchor = AnchorStyles.None;
        scan.Margin = Padding.Empty;
        cancel.Dock = DockStyle.None;
        cancel.Anchor = AnchorStyles.None;
        cancel.Margin = Padding.Empty;
        // «Cancelar» ocupa el sitio de «Listar colección» mientras hay una acción en curso.
        cancel.Visible = false;
        cancel.EnabledChanged += (_, _) => { cancel.Visible = cancel.Enabled; scan.Visible = !cancel.Enabled; };
        actionLine.Controls.Add(scan, 0, 0);
        actionLine.Controls.Add(cancel, 0, 0);

        systems.Width = 180;
        systems.Margin = Padding.Empty;

        rootLine.Controls.Add(actionLine, 0, 2);
        rootLine.SetColumnSpan(actionLine, 2);
        shell.Controls.Add(rootLine, 0, 1);

        var headerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 88, Padding = new Padding(8, 1, 8, 1), ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var searchCaption = new Label { Text = "Buscar ROM:", AutoSize = false, Width = 80, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        search.AutoSize = false;
        searchInSystem.AutoSize = false; searchInAll.AutoSize = false;
        searchArea.Controls.Add(searchCaption);
        searchArea.Controls.Add(search);
        searchArea.Controls.Add(searchInSystem);
        searchArea.Controls.Add(searchInAll);
        void LayoutSearchArea()
        {
            int height = searchArea.ClientSize.Height;
            int y = Math.Max(0, (height - 24) / 2);
            // Los dos botones de ámbito van uno encima del otro, a la derecha del cuadro de búsqueda.
            int Measure(RadioButton r) => Math.Clamp(TextRenderer.MeasureText(r.Text, r.Font).Width + 24, 60, 130);
            int radioWidth = Math.Max(Measure(searchInSystem), Measure(searchInAll));
            const int searchX = 86;
            int searchWidth = Math.Max(70, searchArea.ClientSize.Width - searchX - radioWidth - 12);
            searchCaption.SetBounds(0, y, 80, 24);
            search.SetBounds(searchX, y, searchWidth, 24);
            int radioY = Math.Max(0, (height - 34) / 2);
            searchInSystem.SetBounds(searchX + searchWidth + 8, radioY, radioWidth, 17);
            searchInAll.SetBounds(searchX + searchWidth + 8, radioY + 17, radioWidth, 17);
        }
        searchArea.Resize += (_, _) => LayoutSearchArea();
        searchInSystem.TextChanged += (_, _) => LayoutSearchArea();
        headerGrid.Controls.Add(searchArea, 1, 1);
        var systemArea = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        systemArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        systemArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        systemArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        systemArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        systemSummary.Dock = DockStyle.Fill;
        systemArea.Controls.Add(systemSummary, 0, 0);
        systemArea.Controls.Add(systemGaps, 0, 1);
        var systemSelector = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var systemCaption = new Label { Text = "Sistema:", AutoSize = false, Width = 54, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        systems.Dock = DockStyle.None;
        systems.Margin = Padding.Empty;
        systemSelector.Controls.Add(systemIcon);
        systemSelector.Controls.Add(systems);
        systemSelector.Controls.Add(removeSystemButton);
        systemSelector.ContextMenuStrip = systemMenu; systemIcon.ContextMenuStrip = systemMenu; systemSummary.ContextMenuStrip = systemMenu; systemGaps.ContextMenuStrip = systemMenu;
        void LayoutSystemSelector()
        {
            int y = Math.Max(0, (systemSelector.ClientSize.Height - 54) / 2);
            // El desplegable mide un 90 % de lo que cabe y el conjunto icono + desplegable + botón de eliminar se centra: el hueco a la izquierda del icono es igual al de la derecha, con cualquier tamaño de ventana.
            const int buttonGap = 28; // separado del desplegable: es un botón de borrado y no debe pulsarse sin querer
            int button = removeSystemButton.Width + buttonGap;
            int comboWidth = Math.Max(90, (int)((systemSelector.ClientSize.Width - 54 - 20 - 16 - button) * 0.9));
            int margin = Math.Max(0, (systemSelector.ClientSize.Width - (54 + 20 + comboWidth + button)) / 2);
            systemIcon.SetBounds(margin, Math.Max(0, y - 2), 54, 54); // un poco más arriba: la imagen deja más aire por abajo que por arriba
            systems.SetBounds(margin + 74, y + 15, comboWidth, 24); // centrado verticalmente respecto al icono
            removeSystemButton.SetBounds(margin + 74 + comboWidth + buttonGap, y + 14, removeSystemButton.Width, 26);
        }
        systemSelector.Resize += (_, _) => LayoutSystemSelector();
        systemArea.Controls.Add(systemSelector, 0, 2);
        headerGrid.Controls.Add(systemArea, 0, 0);
        headerGrid.SetRowSpan(systemArea, 2);

        clearSelection.AutoSize = false;
        clearSelection.TextAlign = ContentAlignment.MiddleCenter;
        var selectionBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var sb = new Panel { BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        selectionLabel.Dock = DockStyle.Fill;
        selectionLabel.AutoSize = false;
        selectionLabel.TextAlign = ContentAlignment.MiddleCenter;
        selectionLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        selectionLabel.BackColor = SystemColors.Window;
        selectionLabel.ForeColor = SystemColors.ControlText;
        selectionLabel.Text = "ROMs seleccionadas: 0\r\nTamaño: 0 B"; // dos líneas: se lee bien con cualquier ancho de ventana
        sb.Controls.Add(selectionLabel);
        selectionLabel.BringToFront();
        selectionLabel.Cursor = Cursors.Hand;
        selectionLabel.Click += (_, _) => ShowSelectedRoms(); // abre la lista de las ROMs seleccionadas
        headerGrid.Controls.Add(selectionBar, 2, 1);
                var w=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(8,4,8,4),ColumnCount=3,RowCount=1,Margin=Padding.Empty};w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,29));w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,37));w.RowStyles.Add(new RowStyle(SizeType.Percent,100));categories.Dock=roms.Dock=details.Dock=description.Dock=preview.Dock=DockStyle.Fill;w.Controls.Add(Group("Categorías",categories),0,0);
        var rp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Padding.Empty, Margin = Padding.Empty };
        rp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rp.Controls.Add(roms, 0, 0);
        var act = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 0, 0, 0), Margin = Padding.Empty };
        act.Controls.Add(selectAll);
        act.Controls.Add(deselectAll);
        act.Controls.Add(excludeButton);
        rp.Controls.Add(act, 0, 1);
        w.Controls.Add(Group("ROMs encontradas", rp), 1, 0);        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var mediaSurface = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SystemColors.Control };
        var mediaFrame = new Panel { BackColor = SystemColors.Control, Margin = Padding.Empty, Padding = Padding.Empty };
        preview.Dock = videoView.Dock = DockStyle.None;
        preview.Margin = videoView.Margin = Padding.Empty;
        mediaFrame.Controls.Add(preview);
        mediaFrame.Controls.Add(videoView);
        mediaSurface.Controls.Add(mediaFrame);
        void FitMediaContent()
        {
            int inset = Math.Clamp(Math.Min(mediaFrame.ClientSize.Width, mediaFrame.ClientSize.Height) / 80, 1, 5);
            int contentWidth = Math.Max(1, mediaFrame.ClientSize.Width - inset * 2);
            int contentHeight = Math.Max(1, mediaFrame.ClientSize.Height - inset * 2);
            preview.SetBounds(inset, inset, contentWidth, contentHeight);
            videoView.SetBounds(inset, inset, Math.Max(1, contentWidth - 3), contentHeight);
        }
        void FitMediaFrameTo4By3()
        {
            int availableWidth = mediaSurface.ClientSize.Width;
            int availableHeight = mediaSurface.ClientSize.Height;
            if (availableWidth <= 0 || availableHeight <= 0) return;
            int frameWidth = Math.Min(availableWidth, (int)Math.Round(availableHeight * (4.0 / 3.0)));
            int frameHeight = (int)Math.Round(frameWidth * (3.0 / 4.0));
            mediaFrame.SetBounds((availableWidth - frameWidth) / 2, (availableHeight - frameHeight) / 2, frameWidth, frameHeight);
            FitMediaContent();
        }
        mediaSurface.Resize += (_, _) => FitMediaFrameTo4By3();
        mediaFrame.Resize += (_, _) => FitMediaContent();
        var mediaLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        mediaLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mediaLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mediaLayout.Controls.Add(mediaSurface, 0, 0);
        miximageGroup.Controls.Add(mediaLayout);
        // Insignia de RetroAchievements en la franja del título del panel, a la derecha: no quita sitio al medio (solo si la ROM tiene logros).
        miximageGroup.Controls.Add(raBadge); raBadge.BringToFront();
        // Barra inferior del panel derecho: resumen de lo seleccionado, «Vaciar selección» y «Eliminar Seleccionadas» (cada palabra en una fila).
        var operationBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        operationBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sb.Dock = DockStyle.Fill;
        sb.Margin = new Padding(0, 2, 6, 2);
        clearSelection.AutoSize = false;
        clearSelection.Dock = DockStyle.Fill;
        clearSelection.Margin = new Padding(0, 2, 6, 2);
        deleteSelected.Dock = DockStyle.Fill;
        deleteSelected.Margin = new Padding(0, 2, 0, 2);
        deleteSelected.TextAlign = ContentAlignment.MiddleCenter;
        // Botón destacado pero discreto: fondo rojizo suave, borde rojo y texto rojo oscuro en negrita. Desactivado vuelve al aspecto gris normal. No cambia de tamaño.
        deleteSelected.FlatStyle = FlatStyle.Flat; deleteSelected.UseVisualStyleBackColor = false; deleteSelected.Font = new Font(deleteSelected.Font, FontStyle.Bold);
        deleteSelected.FlatAppearance.BorderSize = 1;
        deleteSelected.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 221, 221); deleteSelected.FlatAppearance.MouseDownBackColor = Color.FromArgb(250, 200, 200);
        void PaintDelete()
        {
            bool on = deleteSelected.Enabled;
            deleteSelected.BackColor = on ? Color.FromArgb(255, 236, 236) : SystemColors.Control;
            deleteSelected.ForeColor = on ? Color.FromArgb(160, 32, 32) : SystemColors.GrayText;
            deleteSelected.FlatAppearance.BorderColor = on ? Color.FromArgb(200, 70, 70) : Color.FromArgb(200, 200, 200);
        }
        deleteSelected.EnabledChanged += (_, _) => PaintDelete();
        PaintDelete();
        operationBar.Controls.Add(sb, 0, 0);
        operationBar.Controls.Add(clearSelection, 1, 0);
        operationBar.Controls.Add(deleteSelected, 2, 0);
        right.Controls.Add(miximageGroup, 0, 0);
        right.Controls.Add(Group("Descripción", description), 0, 1);
        right.Controls.Add(Group("Datos", details), 0, 2);
        right.Controls.Add(operationBar, 0, 3);
        w.Controls.Add(right, 2, 0);
        // Panel «Sistema»: resumen, huecos, selector y categorías del sistema, de arriba abajo en la columna izquierda. La búsqueda y la selección quedan sobre los otros dos paneles.
        headerGrid.Controls.Clear();
        var oldCategories = w.GetControlFromPosition(0, 0)!;
        var romsGroup = w.GetControlFromPosition(1, 0)!;
        var rightPanel = w.GetControlFromPosition(2, 0)!;
        w.Controls.Clear();
        systemArea.Controls.Clear();
        var systemPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty };
        systemPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        systemPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        systemPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        systemPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        systemPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        systemPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var categoriesCaption = new Label { Text = "Categorías", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Margin = Padding.Empty };
        categories.Dock = DockStyle.Fill;
        systemPanel.Controls.Add(systemSelector, 0, 0);
        systemPanel.Controls.Add(systemSummary, 0, 1);
        systemPanel.Controls.Add(systemGaps, 0, 2);
        systemPanel.Controls.Add(categoriesCaption, 0, 3);
        systemPanel.Controls.Add(categories, 0, 4);
        oldCategories.Dispose();
        w.RowCount = 2;
        w.RowStyles.Clear();
        w.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        w.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var systemGroup = Group("Sistema", systemPanel);
        w.Controls.Add(systemGroup, 0, 1);
        // Los tres paneles empiezan a la misma altura y llegan hasta arriba. La búsqueda sube a la fila de «Analizar» y «Resumen», alineada con el panel de ROMs.
        w.Controls.Add(romsGroup, 1, 1);
        w.Controls.Add(rightPanel, 2, 1);
        var searchHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        searchArea.Dock = DockStyle.None;
        searchHost.Controls.Add(searchArea);
        void AlignSearch()
        {
            if (!searchHost.IsHandleCreated || !romsGroup.IsHandleCreated) return;
            int offset = Math.Max(0, romsGroup.PointToScreen(Point.Empty).X - searchHost.PointToScreen(Point.Empty).X);
            searchArea.SetBounds(offset, 0, Math.Max(0, Math.Min(romsGroup.Width, searchHost.ClientSize.Width - offset)), searchHost.ClientSize.Height); // mismo ancho que la columna central
        }
        searchHost.Resize += (_, _) => AlignSearch();
        w.Resize += (_, _) => AlignSearch();
        Shown += (_, _) => AlignSearch();
        actionLine.Controls.Add(searchHost, 1, 0);
        // Sin colección cargada solo se ve el menú y un mensaje centrado; al cargar o crear una colección aparece todo.
        var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        welcomeLabel = new Label { Text = "ESDEROM CLEANER\r\n\r\nSELECCIONE O CREE UNA NUEVA COLECCIÓN", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = SystemColors.GrayText };
        body.Controls.Add(w); body.Controls.Add(welcomeLabel);
        welcomeShell = shell; welcomeParts = new Control[] { rootLine, w };
        shell.Controls.Add(body, 0, 3);
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
        var f = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 144, Padding = new Padding(10, 2, 10, 2), ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        liveCounters.AutoSize = true;
        liveCounters.Dock = DockStyle.None;
        liveCounters.TextAlign = ContentAlignment.MiddleCenter;
        liveCounters.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        extensionButton.Size = new Size(32, 24);
        extensionButton.Margin = new Padding(8, 0, 0, 0);
        extensionButton.Anchor = AnchorStyles.None;
        extensionButton.AccessibleName = "Listado de extensiones";
        var footerStats = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.None, Padding = Padding.Empty, Margin = Padding.Empty };
        footerStats.Controls.Add(liveCounters);
        footerStats.Controls.Add(extensionButton);
        f.Controls.Add(headline, 0, 0);
        f.Controls.Add(footerStats, 0, 1);
        f.Controls.Add(collectionGaps, 0, 2);
        // La barra de progreso va en la última fila del formulario: ocupa el 96 % del ancho (un 2 % libre a cada lado) y es fina (8 px).
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
        f.Controls.Add(status, 0, 3);
        f.Controls.Add(progressHost, 0, 4);
        shell.Controls.Add(f, 0, 4); Controls.Add(shell); var cpuStrip = new CpuUsageStrip(withAccounts: true); Controls.Add(cpuStrip); // la barra de CPU va debajo del todo (se añade después para que el panel principal ocupe el resto)
        // El autoescalado por fuente de esta ventana la dejaba más alta que en los otros formularios (22 px): se fija esa altura al mostrarla.
        Shown += (_, _) => { cpuStrip.AutoSize = false; cpuStrip.Height = cpuStrip.LogicalToDeviceUnits(22); };
        // Cabecera con el chip de la aplicación, el nombre y, debajo, la colección cargada, su ruta y la versión. Va encima del menú.
        mainBanner = new RaHeader("ES-DE ROM Cleaner", "", 72, 48, logo: false);
        mainBanner.Subtitle.Tag = "noloc";
        try { using var chip = new Icon(Icon, 48, 48); mainBanner.Icon.Image = chip.ToBitmap(); } catch { }
        Controls.Add(mainBanner);
        Height += mainBanner.Height; MinimumSize = new Size(MinimumSize.Width, MinimumSize.Height + mainBanner.Height);
        Loc.LanguageChanged += UpdateMainHeader;
        UpdateMainHeader();
        welcomeFooter = f; ApplyWelcome();
    }
    private static GroupBox Group(string title, Control control) { var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 4) }; g.Controls.Add(control); return g; }
}
