namespace ESDERomCleaner;

public sealed partial class RomCleanerForm
{
    // Sistemas que se abrirán marcados en la descarga masiva en cuanto termine el siguiente análisis.
    private List<string>? scrapeAfterScan;
    // Se activa con «Solo añadir»: el siguiente análisis no ofrece scrapear los sistemas sin categorización.
    private bool skipUnclassifiedOffer;

    private async Task OpenAddRomsAsync()
    {
        if (cts != null) return;
        if (!EnsureCollectionRoot()) return;
        StopVideoPlayback();
        trace.Add("add_roms_dialog_opened", root.Text);
        bool changed, scrapeRequested;
        List<string> added;
        using (var dialog = new AddRomsDialog(root.Text.Trim().Trim('"'), trace))
        {
            dialog.ShowDialog(this);
            changed = dialog.CollectionChanged;
            scrapeRequested = dialog.ScrapeRequested;
            added = dialog.AddedPaths.ToList();
        }
        if (!changed) return;

        // Las ROMs recién añadidas se incorporan a sus gamelists sin preguntar (se han añadido a propósito) y se vuelve a analizar.
        var addedSet = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
        await CreateGamelistsAsync(scanAfter: true, quiet: true, offerScrape: false, onlyPaths: addedSet, silent: true);

        // Si en el formulario se aceptó scrapearlas, se abre directamente la descarga masiva con esas ROMs (de uno o varios sistemas).
        if (!scrapeRequested) return;
        var rows = all.Where(r => r.Path.Length > 0 && addedSet.Contains(Path.GetFullPath(r.Path))).ToList();
        if (rows.Count == 0) { QuietMessage.Show(this, "Scrapear las ROMs añadidas", "Las ROMs añadidas no aparecen en la colección analizada, así que no se puede abrir el scrapeo."); return; }
        await OpenBulkMediaDialogAsync(onlyRows: rows);
    }

    // Ofrece crear los gamelist.xml que faltan a partir de las ROMs. Con «quiet» no avisa si no hay nada que crear
    // (se usa al analizar o al abrir la descarga masiva). Devuelve true si se creó alguno.
    // «onlyPaths»: solo se añaden esas ROMs (las recién añadidas). «silent»: sin preguntar, porque el usuario ya las ha añadido a propósito.
    private async Task<bool> CreateGamelistsAsync(bool scanAfter, bool quiet, string? reason = null, bool offerScrape = true, HashSet<string>? onlyPaths = null, bool silent = false)
    {
        if (cts != null) return false;
        if (!EnsureCollectionRoot()) return false;
        string rootPath = root.Text.Trim().Trim('"');
        status.Text = "Buscando ROMs sin gamelist…";
        List<GeneratedGamelist> plan;
        try { plan = await Task.Run(() => GamelistGenerator.Plan(rootPath, CancellationToken.None)); }
        catch (Exception ex) { QuietMessage.Show(this, "Crear gamelists", ex.Message); return false; }
        if (onlyPaths is not null)
            plan = plan.Select(p => p with { RomPaths = p.RomPaths.Where(r => onlyPaths.Contains(Path.GetFullPath(r))).ToList() }).Where(p => p.RomPaths.Count > 0).ToList();

        if (plan.Count == 0)
        {
            status.Text = "Todas las ROMs ya figuran en sus gamelists.";
            if (!quiet) QuietMessage.Show(this, "Añadir ROMs a los gamelists", "No hay nada que añadir: todas las ROMs de Roms\\<sistema> ya figuran en su gamelist.xml (o no hay ROMs).");
            return false;
        }

        // Con offerScrape se propone además categorizar y descargar los medios de esos sistemas justo después del análisis.
        GamelistPlanChoice choice;
        if (silent) choice = GamelistPlanChoice.AddOnly;
        else
            using (var dialog = new GamelistPlanDialog(plan, offerScrape))
            {
                dialog.ShowDialog(this);
                choice = dialog.Choice;
            }
        if (choice == GamelistPlanChoice.Cancel)
        {
            status.Text = "No se añadieron ROMs a los gamelists.";
            return false;
        }
        if (choice == GamelistPlanChoice.AddAndScrape) scrapeAfterScan = plan.Select(p => p.System).ToList();
        else skipUnclassifiedOffer = true; // «Solo añadir»: el análisis que sigue no vuelve a preguntar por scrapear.

        int created = 0;
        try
        {
            await Task.Run(() => { foreach (var item in plan) { GamelistGenerator.Write(rootPath, item, createGamelistBackups); created++; } });
        }
        catch (Exception ex)
        {
            trace.Add("gamelists_generate_error", ex.ToString());
            QuietMessage.Show(this, "No se pudieron crear todos los gamelists", $"Se crearon {created} de {plan.Count}.\r\n\r\n{ex.Message}");
        }
        trace.Add("gamelists_generated", $"root={rootPath}; systems={created}; roms={plan.Take(created).Sum(p => p.RomPaths.Count)}");
        status.Text = $"Creados {created:N0} gamelist.xml.";
        if (scanAfter && created > 0) { status.Text += " Analizando la colección…"; await ScanAsync(offerUnclassified: offerScrape); }
        return created > 0;
    }
}
