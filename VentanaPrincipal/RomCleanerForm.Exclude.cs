namespace ESDERomCleaner;

// «Excluir seleccionados»: edición de metadatos en masa para las ROMs marcadas. Pone nogamecount y nomultiscrape en true en su gamelist.xml: ES-DE las deja
// fuera de su contador de juegos y del multiscrapeo, y esta aplicación tampoco las cuenta ni las lista.
public sealed partial class RomCleanerForm
{
    private readonly Button excludeButton = new() { Text = "Excluir seleccionados", AutoSize = true, Enabled = false, ForeColor = Color.FromArgb(214, 100, 0) };

    // Quita filas de la colección cargada (las que acaban de excluirse): de la memoria, la selección, las listas, los contadores y la caché del análisis.
    private void RemoveRowsFromCollection(IReadOnlyCollection<RomRow> removed)
    {
        if (removed.Count == 0) return;
        var keys = new HashSet<string>(removed.Select(r => $"{r.System}|{r.Path}"), StringComparer.OrdinalIgnoreCase);
        all = all.Where(r => !keys.Contains($"{r.System}|{r.Path}")).ToList();
        foreach (var row in removed) selected.Remove(row.Path);
        RefreshSystemsList();
        UpdateCollectionCounters(); // también guarda la caché del análisis ya sin esas ROMs
        UpdateSelection();
        _ = RefreshGapsAsync();
    }

    private void WireExclude() => excludeButton.Click += async (_, _) => await ExcludeSelectedAsync();

    private async Task ExcludeSelectedAsync()
    {
        if (cts != null) return;
        if (selected.Count == 0)
        {
            QuietMessage.Show(this, "Excluir seleccionados", "No hay ROMs seleccionadas. Marca primero las ROMs que quieras excluir.");
            return;
        }
        var rows = all.Where(r => selected.Contains(r.Path) && r.Gamelist.Length > 0 && r.GamelistPath.Length > 0).ToList();
        int withoutEntry = selected.Count - rows.Count;
        if (rows.Count == 0)
        {
            QuietMessage.Show(this, "Excluir seleccionados", "Las ROMs seleccionadas no tienen entrada en ningún gamelist.xml, así que no se pueden marcar.");
            return;
        }
        string message = $"Se van a excluir {rows.Count:N0} ROMs del contador y del multiscrapeo de ES-DE (nogamecount y nomultiscrape en true en su gamelist.xml).\r\n\r\n"
            + "Dejarán de aparecer en esta lista y en los contadores. No se toca ningún archivo de ROM y se puede revertir editando el gamelist.xml."
            + (withoutEntry > 0 ? $"\r\n\r\n{withoutEntry:N0} de las seleccionadas no tienen entrada en un gamelist y se ignoran." : "");
        if (!QuietMessage.Confirm(this, "Excluir seleccionados", message, "Excluir")) return;

        trace.Add("exclude_started", $"roms={rows.Count}; systems={string.Join(",", rows.Select(r => r.System).Distinct(StringComparer.OrdinalIgnoreCase))}");
        status.Text = "Excluyendo las ROMs seleccionadas…";
        bool backup = createGamelistBackups;
        var errors = new List<string>();
        int updated = 0;
        var flags = new Dictionary<string, string> { ["nogamecount"] = "true", ["nomultiscrape"] = "true" };
        var doneRows = new List<RomRow>();
        await Task.Run(() =>
        {
            foreach (var group in rows.GroupBy(r => r.Gamelist, StringComparer.OrdinalIgnoreCase))
            {
                var relatives = group.Select(r => PathRules.Relative(r.GamelistPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                try
                {
                    int count = GamelistMetadataWriter.SetFields(group.Key, relatives, flags, backup, errors);
                    updated += count;
                    if (count == relatives.Count) doneRows.AddRange(group); // las que no se pudieron marcar se quedan en la lista
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(Path.GetDirectoryName(group.Key) ?? "")}: {ex.Message}"); }
            }
        });
        trace.Add("exclude_finished", $"updated={updated}; errors={errors.Count}");
        // Las ROMs excluidas se quitan de la colección cargada al momento, sin volver a analizar (el análisis completo puede detenerse a preguntar por otras cosas).
        RemoveRowsFromCollection(doneRows);
        status.Text = $"{updated:N0} ROMs excluidas del contador y del multiscrapeo." + (errors.Count > 0 ? $" Avisos: {errors.Count:N0}." : "");
        if (errors.Count > 0) QuietMessage.Show(this, "Excluir seleccionados", string.Join("\r\n", errors.Take(10)));
    }
}
