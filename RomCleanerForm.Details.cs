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
    private void EditMetadata()
    {
        if (roms.SelectedItem is not RomItem item) return;
        try
        {
            if (!RomMetadataEditor.Show(this, item.Row, createGamelistBackups, all.Select(r => r.Genre).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())) return;

            trace.Add("metadata_saved", $"system={item.Row.System}; path={item.Row.GamelistPath}");
            RefreshMetadataAfterSave(item.Row);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudieron abrir los metadatos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshMetadataAfterSave(RomRow editedRow)
    {
        // Relee un único gamelist pequeño; no vuelve a recorrer ROMs ni otros sistemas.
        var entries = GamelistParser.Parse(editedRow.Gamelist, CancellationToken.None);
        var matches = entries
            .Where(entry => PathRules.Relative(entry.Path) == PathRules.Relative(editedRow.GamelistPath))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Los metadatos se guardaron, pero no se pudo actualizar la vista: se encontraron {matches.Length} entradas para esa ruta.");

        var entry = matches[0];
        // Si se acaba de marcar «Excluir del contador» o «del multi-scraper», la ROM deja de listarse: hace falta volver a analizar.
        if (entry.Excluded) { status.Text = "ROM excluida: se vuelve a analizar la colección…"; _ = ScanAsync(offerUnclassified: false); return; }
        var updatedRow = editedRow with
        {
            GameName = entry.Name,
            Genre = entry.Genre,
            Description = entry.Description
        };

        bool IsEditedRow(RomRow row) =>
            row.System.Equals(editedRow.System, StringComparison.OrdinalIgnoreCase) &&
            row.Path.Equals(editedRow.Path, StringComparison.OrdinalIgnoreCase) &&
            PathRules.Relative(row.GamelistPath) == PathRules.Relative(editedRow.GamelistPath);

        int rowIndex = all.FindIndex(IsEditedRow);
        if (rowIndex < 0)
            throw new InvalidOperationException("Los metadatos se guardaron, pero la ROM ya no está en la lista actual. Analiza la colección cuando quieras actualizarla.");

        all[rowIndex] = updatedRow;
        current = current.Select(row => IsEditedRow(row) ? updatedRow : row).ToList();

        string updatedCache = CollectionScanner.WriteCache(root.Text, all);
        cachePath = updatedCache;

        string? previousCategory = (categories.SelectedItem as CategoryItem)?.Genre;
        string desiredCategory = current.Any(row => Genre(row).Equals(previousCategory, StringComparison.OrdinalIgnoreCase))
            ? previousCategory!
            : Genre(updatedRow);

        changing = true;
        categories.BeginUpdate();
        try
        {
            categories.Items.Clear();
            foreach (var group in current.GroupBy(Genre, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                categories.Items.Add(new CategoryItem(group.Key, group.Count(row => row.Status == RomStatus.Ok)));

            int categoryIndex = categories.Items.Cast<CategoryItem>().ToList().FindIndex(item => item.Genre.Equals(desiredCategory, StringComparison.OrdinalIgnoreCase));
            if (categoryIndex < 0 && categories.Items.Count > 0) categoryIndex = 0;
            categories.SelectedIndex = categoryIndex;
        }
        finally
        {
            categories.EndUpdate();
            changing = false;
        }

        RefreshList();
        int focusedIndex = roms.Items.Cast<RomItem>().ToList().FindIndex(listItem => IsEditedRow(listItem.Row));
        if (focusedIndex >= 0) roms.SelectedIndex = focusedIndex;
        status.Text = $"Metadatos actualizados para {editedRow.System}; no se reescanearon archivos.";
        trace.Add("metadata_view_refreshed", $"system={editedRow.System}; path={editedRow.GamelistPath}; cache={updatedCache}");
    }
    private async void ShowDetails()
    {
        StopVideoPlayback();
        ResetRomMedia();
        if (roms.SelectedItem is not RomItem item)
        {
            pendingVideoRow = null;
            previewVersion++;
            preview.Image?.Dispose();
            preview.Image = null;
            miximageGroup.Text = "Imagen de la ROM enfocada";
            description.Text = "Selecciona una ROM para ver su descripción.";
            return;
        }

        RomRow row = item.Row;
        description.Text = string.IsNullOrWhiteSpace(row.Description) ? "Esta ROM no tiene una descripción en su gamelist.xml." : row.Description;
        details.Text = $"ROM: {row.RomName}\r\nSistema: {row.System}\r\nEstado: {row.Status}\r\nCategoría: {Genre(row)}\r\nTamaño: {FormatSize(row.Size)}\r\nRuta: {row.Path}\r\nGamelist: {row.Gamelist}\r\nPath XML: {row.GamelistPath}\r\n\r\nNombre: {row.GameName}\r\nCategoría en el gamelist: {row.Genre}";
        UpdateDataPanel();

        int version = ++previewVersion;
        pendingVideoRow = row;
        pendingVideoVersion = version;
        preview.Visible = true;
        preview.Image?.Dispose();
        preview.Image = null;
        miximageGroup.Text = $"Imagen de {row.GameName}";
        try
        {
            string? imagePath = await Task.Run(() => FindImage(root.Text, row, ImageSettings.Folder));
            if (version != previewVersion) return;
            if (!string.IsNullOrEmpty(imagePath))
            {
                using var image = Image.FromFile(imagePath);
                preview.Image = new Bitmap(image);
            }
            if (showVideos.Checked) ScheduleVideoPreview(row, version);
        }
        catch
        {
            if (version == previewVersion && showVideos.Checked) ScheduleVideoPreview(row, version);
        }
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string[]>> MiximageFileIndex = new(StringComparer.OrdinalIgnoreCase);
    private static string? FindImage(string rootPath, RomRow row, string folder)
    {
        string dir = Path.Combine(rootPath, "ES-DE", "downloaded_media", row.System, folder);
        if (!Directory.Exists(dir)) return null;
        string[] exts = [".png", ".jpg", ".jpeg", ".webp"];
        var stems = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Path)) { try { stems.Add(Directory.Exists(row.Path) ? new DirectoryInfo(row.Path).Name : Path.GetFileNameWithoutExtension(row.Path)); } catch { } }
        if (!string.IsNullOrWhiteSpace(row.GamelistPath)) stems.Add(Path.GetFileNameWithoutExtension(row.GamelistPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(row.RomName)) stems.Add(Path.GetFileNameWithoutExtension(row.RomName));
        if (!string.IsNullOrWhiteSpace(row.GameName)) stems.Add(row.GameName);
        foreach (var stem in stems) foreach (var ext in exts) { string p = Path.Combine(dir, stem + ext); if (File.Exists(p)) return p; }
        var files = MiximageFileIndex.GetOrAdd(dir, d => new Lazy<string[]>(() => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Where(p => exts.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray())).Value;
        foreach (var stem in stems)
        {
            string key = NormalizeMediaStem(stem);
            var hits = files.Where(p => NormalizeMediaStem(Path.GetFileNameWithoutExtension(p)) == key).ToArray();
            if (hits.Length == 1) return hits[0];
            var near = files.Where(p => OneEditAway(key, NormalizeMediaStem(Path.GetFileNameWithoutExtension(p)))).ToArray();
            if (near.Length == 1) return near[0];
        }
        return null;
    }
    private static string NormalizeMediaStem(string s)
    {
        string d=s.Normalize(System.Text.NormalizationForm.FormD).ToLowerInvariant();
        return new string(d.Where(c=>System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)!=System.Globalization.UnicodeCategory.NonSpacingMark&&char.IsLetterOrDigit(c)).ToArray());
    }
    private static bool OneEditAway(string a,string b)
    {
        if(a.Length==0||Math.Abs(a.Length-b.Length)>1)return false;int i=0,j=0,n=0;
        while(i<a.Length&&j<b.Length){if(a[i]==b[j]){i++;j++;continue;}if(++n>1)return false;if(a.Length>b.Length)i++;else if(b.Length>a.Length)j++;else{i++;j++;}}
        return n+((i<a.Length||j<b.Length)?1:0)<=1;
    }
    private void UpdateDataPanel()
    {
        string genre = (categories.SelectedItem as CategoryItem)?.Genre ?? "—";
        var rows = current.Where(r => Genre(r).Equals(genre, StringComparison.OrdinalIgnoreCase)).ToArray();
        int selectedCount = rows.Count(r => r.Path.Length > 0 && selected.Contains(r.Path));
        // Con una ROM enfocada, el nombre de su archivo va en la primera línea.
        string file = roms.SelectedItem is RomItem focused ? $"ARCHIVO: {focused.Row.RomName}\r\n" : "";
        details.Text = file + $"CATEGORÍA: {genre}\r\nROMs: {rows.Length:N0}\r\nESPACIO: {FormatSize(rows.Sum(r => r.Size))}\r\n\r\nSeleccionadas en esta categoría: {selectedCount:N0}\r\n\r\nEl foco de la categoría determina las ROMs mostradas a la derecha.";
    }
}
