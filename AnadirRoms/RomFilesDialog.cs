using System.IO.Compression;
using SharpCompress.Archives;

namespace ESDERomCleaner;

// Un archivo de dentro de un comprimido (o el propio archivo si va suelto) y en qué juegos de los DAT está su ROM.
internal sealed record RomFileEntry(string Name, long Size, uint? Crc, bool IsRom, IReadOnlyList<string> Games);

// Contenido de un comprimido frente a los DAT: cada archivo con su tamaño y CRC, y la mejor coincidencia de arcade con cuántas de sus ROMs están.
internal sealed record RomFilesReport(IReadOnlyList<RomFileEntry> Entries, string? BestGame, string? BestSource, int Matched, int Total)
{
    // Lee la cabecera del zip o 7z (sin descomprimir) y consulta los índices de DAT en memoria.
    public static async Task<RomFilesReport> InspectAsync(string path)
    {
        var index = await RomDatabase.GetIndexAsync();
        return await Task.Run(() =>
        {
            var raw = new List<(string Name, long Size, uint? Crc)>();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".zip")
                {
                    using var zip = ZipFile.OpenRead(path);
                    foreach (var e in zip.Entries.Where(e => e.Name.Length > 0)) raw.Add((e.FullName, e.Length, e.Crc32));
                }
                else if (ext is ".7z" or ".rar")
                {
                    using var archive = ArchiveFactory.OpenArchive(path);
                    foreach (var e in archive.Entries.Where(e => !e.IsDirectory)) raw.Add((e.Key ?? "", e.Size, (uint)e.Crc));
                }
                else raw.Add((Path.GetFileName(path), new FileInfo(path).Length, null));
            }
            catch (Exception ex) { raw.Add(("(no se pudo leer: " + ex.Message + ")", 0, null)); }

            // Mejor juego de arcade: el que reúne más ROMs distintas del archivo (a igualdad, el que menos de más lleva).
            var romKeys = raw.Where(r => r.Crc is not null && !GamelistGenerator.IsNotRom(r.Name) && r.Size > 0).Select(r => DatIndex.Pack(r.Crc!.Value, r.Size)).Distinct().ToList();
            var counts = new Dictionary<int, int>();
            foreach (ulong key in romKeys)
                if (index.ArcadeRoms.TryGetValue(key, out var ids)) foreach (int id in ids) counts[id] = counts.GetValueOrDefault(id) + 1;
            int bestId = counts.Count == 0 ? -1 : counts.OrderByDescending(p => p.Value).ThenBy(p => index.ArcadeGameRoms[p.Key] - p.Value).First().Key;

            var entries = new List<RomFileEntry>();
            foreach (var (name, size, crc) in raw)
            {
                bool isRom = crc is not null && !GamelistGenerator.IsNotRom(name) && size > 0;
                var games = new List<string>();
                if (isRom)
                {
                    ulong key = DatIndex.Pack(crc!.Value, size);
                    if (index.ArcadeRoms.TryGetValue(key, out var ids)) games.AddRange(ids.OrderBy(i => i == bestId ? 0 : 1).Select(i => index.ArcadeGames[i].Game));
                    if (index.Hash.TryGetValue((crc.Value, size), out var hits)) games.AddRange(hits.Select(h => h.Game));
                }
                entries.Add(new RomFileEntry(name, size, crc, isRom, games.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }
            return new RomFilesReport(entries, bestId >= 0 ? index.ArcadeGames[bestId].Game : null, bestId >= 0 ? index.ArcadeGames[bestId].Source.Label : null,
                bestId >= 0 ? counts[bestId] : 0, romKeys.Count);
        });
    }
}

// «Ver ficheros» de «Añadir ROMs»: el contenido de un comprimido con tamaño, CRC y en qué juego del DAT está cada ROM (verde), o que no está en ninguno (rojo).
internal sealed class RomFilesDialog : LocalizedForm
{
    public RomFilesDialog(string path, RomFilesReport report, Func<long, string> formatSize)
    {
        Text = "Ver ficheros";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(720, 480);

        string summary = report.BestGame is null
            ? $"{report.Entries.Count:N0} archivos · ninguna ROM está en los DAT"
            : $"{report.Entries.Count:N0} archivos · mejor coincidencia: {report.BestGame} ({report.BestSource}) · {report.Matched:N0} de {report.Total:N0} en el DAT";
        var banner = new RaHeader(Path.GetFileName(path), summary, 68, 40, logo: false);
        banner.Title.Tag = "noloc";
        banner.Icon.Image = RaUi.MagnifierIcon(2);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        RaUi.StyleGrid(grid, 26, 24);
        const DataGridViewAutoSizeColumnMode fill = DataGridViewAutoSizeColumnMode.Fill;
        var mono = new DataGridViewCellStyle { Font = new Font("Consolas", 9f) };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Fichero", AutoSizeMode = fill, FillWeight = 38, DefaultCellStyle = mono, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tamaño", AutoSizeMode = fill, FillWeight = 14, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CRC", AutoSizeMode = fill, FillWeight = 14, DefaultCellStyle = mono, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "En el DAT", AutoSizeMode = fill, FillWeight = 34, SortMode = DataGridViewColumnSortMode.Automatic });
        foreach (var e in report.Entries)
        {
            string where = !e.IsRom ? Loc.T("no es una ROM") : e.Games.Count == 0 ? Loc.T("no está en el DAT") : string.Join(", ", e.Games.Take(3)) + (e.Games.Count > 3 ? $" (+{e.Games.Count - 3})" : "");
            var row = grid.Rows[grid.Rows.Add(e.Name, e.Size > 0 ? formatSize(e.Size) : "—", e.Crc is { } c ? c.ToString("x8") : "—", where)];
            row.Cells[3].Style.ForeColor = !e.IsRom ? SystemColors.GrayText : e.Games.Count == 0 ? Color.Firebrick : Color.FromArgb(47, 122, 61);
        }
        grid.ClearSelection();

        var footer = RaUi.Footer();
        var note = new Label { Text = "Verde: la ROM está en un juego del DAT. Rojo: no está en ninguno.", Location = new Point(24, 16), Size = new Size(500, 22), AutoSize = false, ForeColor = SystemColors.GrayText };
        var close = new Button { Text = "Cerrar", DialogResult = DialogResult.Cancel, AutoSize = false, Size = new Size(110, 32) };
        RaUi.StyleButton(close, true);
        close.Location = new Point(720 - 24 - close.Width, 10);
        footer.Controls.AddRange([note, close]);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
        body.Controls.Add(grid);
        Controls.AddRange([body, footer, banner]);
        CancelButton = close; AcceptButton = close;
    }
}
