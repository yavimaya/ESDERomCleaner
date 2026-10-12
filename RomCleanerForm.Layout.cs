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
    private void BuildLayout()
    {
        AutoScaleMode = AutoScaleMode.Font; Font = new Font("Segoe UI", 9F);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 5 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent,100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute,68));
        var menu=new MenuStrip { Dock=DockStyle.Fill, AutoSize=true }; var tm=new ToolStripMenuItem("Trazabilidad"); tm.DropDownItems.Add("Ver registro de trazas…",null,(_,_)=>ShowTrace()); var optionsMenu = new ToolStripMenuItem("Opciones"); optionsMenu.DropDownItems.Add(createGamelists); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(backupOption); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(showVideos); optionsMenu.DropDownItems.Add(videoSound); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(showAccessory); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(dataFolderMenu); optionsMenu.DropDownItems.Add(tempFolderMenu); optionsMenu.DropDownItems.Add(new ToolStripSeparator()); optionsMenu.DropDownItems.Add(languageMenu); menu.Items.Add(optionsMenu); var scrapeMenu=new ToolStripMenuItem("Scrapeo"); scrapeMenu.DropDownItems.Add(bulkMedia); scrapeMenu.DropDownItems.Add(screenScraperCredentials); scrapeMenu.DropDownItems.Add(screenScraperSignUp); menu.Items.Add(scrapeMenu); var curationMenu=new ToolStripMenuItem("Curado"); curationMenu.DropDownItems.Add(curationRules); menu.Items.Add(curationMenu); var romsMenu=new ToolStripMenuItem("Añadir ROMs"); romsMenu.DropDownItems.Add(addRoms); romsMenu.DropDownItems.Add(datManager); menu.Items.Add(romsMenu); menu.Items.Add(tm); MainMenuStrip=menu; shell.Controls.Add(menu,0,0);
        var rootLine = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 96, Padding = new Padding(10, 4, 10, 4), ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        rootLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rootLine.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rootLine.Controls.Add(new Label { Text = "Ruta raíz (contiene Roms y ES-DE):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        rootLine.SetColumnSpan(rootLine.GetControlFromPosition(0, 0)!, 2);
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
        pathLine.Controls.Add(cloneButton);
        rootLine.Controls.Add(pathLine, 0, 1);
        rootLine.SetColumnSpan(pathLine, 2);
        var actionLine = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        actionLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        actionLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        actionLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scan.AutoSize = false;
        scan.Size = new Size(142, 30);
        scan.Margin = new Padding(0, 0, 8, 0);
        cancel.AutoSize = false;
        cancel.Size = new Size(76, 30);
        cancel.MinimumSize = new Size(76, 30);
        cancel.MaximumSize = new Size(76, 30);
        cancel.Margin = Padding.Empty;
        scan.Dock = DockStyle.Fill;
        scan.Margin = new Padding(0, 0, 8, 0);
        cancel.Dock = DockStyle.None;
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        cancel.Margin = Padding.Empty;
        actionLine.Controls.Add(scan, 0, 0);
        actionLine.Controls.Add(cancel, 1, 0);

        systems.Width = 180;
        systems.Margin = Padding.Empty;

        rootLine.Controls.Add(actionLine, 0, 2);
        rootLine.SetColumnSpan(actionLine, 2);
        shell.Controls.Add(rootLine, 0, 1);

        var headerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 64, Padding = new Padding(8, 1, 8, 1), ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        headerGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var searchCaption = new Label { Text = "Buscar ROM:", AutoSize = false, Width = 80, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        search.AutoSize = false;
        regex.AutoSize = true;
        searchArea.Controls.Add(searchCaption);
        searchArea.Controls.Add(search);
        searchArea.Controls.Add(regex);
        void LayoutSearchArea()
        {
            int height = searchArea.ClientSize.Height;
            int y = Math.Max(0, (height - 24) / 2);
            int regexWidth = Math.Max(54, regex.PreferredSize.Width);
            const int searchX = 86;
            int searchWidth = Math.Max(70, searchArea.ClientSize.Width - searchX - regexWidth - 8);
            searchCaption.SetBounds(0, y, 80, 24);
            search.SetBounds(searchX, y, searchWidth, 24);
            regex.SetBounds(searchX + searchWidth + 6, y, regexWidth, 24);
        }
        searchArea.Resize += (_, _) => LayoutSearchArea();
        headerGrid.Controls.Add(searchArea, 1, 1);
        var systemArea = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        systemArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        systemArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        systemArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        systemSummary.Dock = DockStyle.Fill;
        systemArea.Controls.Add(systemSummary, 0, 0);
        var systemSelector = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        var systemCaption = new Label { Text = "Sistema:", AutoSize = false, Width = 54, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        systems.Dock = DockStyle.None;
        systems.Margin = Padding.Empty;
        systemSelector.Controls.Add(systemCaption);
        systemSelector.Controls.Add(systems);
        void LayoutSystemSelector()
        {
            int y = Math.Max(0, (systemSelector.ClientSize.Height - 24) / 2);
            int comboWidth = Math.Max(90, systemSelector.ClientSize.Width - 58);
            systemCaption.SetBounds(0, y, 54, 24);
            systems.SetBounds(58, y, comboWidth, 24);
        }
        systemSelector.Resize += (_, _) => LayoutSystemSelector();
        systemArea.Controls.Add(systemSelector, 0, 1);
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
        selectionLabel.Text = "ROMs seleccionadas: 0    |    Tamaño: 0 B";
        sb.Controls.Add(selectionLabel);
        selectionLabel.BringToFront();
        selectionBar.Controls.Add(sb);
        selectionBar.Controls.Add(clearSelection);
        void LayoutSelectionBar()
        {
            int height = Math.Min(34, selectionBar.ClientSize.Height);
            int y = 4;
            const int buttonWidth = 100;
            int counterWidth = Math.Max(0, selectionBar.ClientSize.Width - buttonWidth - 6);
            sb.SetBounds(0, y, counterWidth, height);
            clearSelection.SetBounds(counterWidth + 6, y, buttonWidth, height);
        }
        selectionBar.Resize += (_, _) => LayoutSelectionBar();
        headerGrid.Controls.Add(selectionBar, 2, 1);
        shell.Controls.Add(headerGrid, 0, 2);        var w=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(8,4,8,4),ColumnCount=3,RowCount=1,Margin=Padding.Empty};w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,29));w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));w.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,37));w.RowStyles.Add(new RowStyle(SizeType.Percent,100));categories.Dock=roms.Dock=details.Dock=description.Dock=preview.Dock=DockStyle.Fill;w.Controls.Add(Group("Categorías",categories),0,0);
        var rp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Padding.Empty, Margin = Padding.Empty };
        rp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rp.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rp.Controls.Add(roms, 0, 0);
        var act = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 0, 0, 0), Margin = Padding.Empty };
        act.Controls.Add(selectAll);
        act.Controls.Add(deselectAll);
        rp.Controls.Add(act, 0, 1);
        w.Controls.Add(Group("ROMs encontradas", rp), 1, 0);        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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
        var operationBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        operationBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progress.Dock = DockStyle.Fill;
        progress.Margin = new Padding(2, 7, 8, 5);
        deleteSelected.Dock = DockStyle.Fill;
        deleteSelected.Margin = new Padding(0, 2, 0, 2);
        operationBar.Controls.Add(progress, 0, 0);
        operationBar.Controls.Add(deleteSelected, 1, 0);
        right.Controls.Add(miximageGroup, 0, 0);
        right.Controls.Add(Group("Descripción", description), 0, 1);
        right.Controls.Add(Group("Datos", details), 0, 2);
        right.Controls.Add(operationBar, 0, 3);
        w.Controls.Add(right, 2, 0);
        shell.Controls.Add(w, 0, 3);
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
        var f = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 68, Padding = new Padding(10, 2, 10, 2), ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        f.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
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
        f.Controls.Add(footerStats, 0, 0);
        f.Controls.Add(status, 0, 1);
        shell.Controls.Add(f, 0, 4); Controls.Add(shell);
    }
    private static GroupBox Group(string title, Control control) { var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 4) }; g.Controls.Add(control); return g; }
}
