namespace ESDERomCleaner;

// Datos del plan de borrado, ya con los tamaños formateados.
internal sealed record DeleteConfirmData(int Roms, string RomSize, int Accessories, string AccessorySize, int Media, string MediaSize, int Gamelists, string TotalSize, bool Backups);

// Confirmación del borrado: deja claro que los archivos se borran de la colección y del almacenamiento, sin Papelera. Los botones son «BORRAR» y «Cancelar»
// (el segundo es el predeterminado, para que un Intro no borre nada).
internal sealed class DeleteConfirmDialog : LocalizedForm
{
    public DeleteConfirmDialog(DeleteConfirmData data)
    {
        Text = "Confirmar eliminación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(640, 388);
        var danger = Color.FromArgb(150, 30, 30);

        // Cabecera roja con el mensaje principal.
        var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 92), BackColor = danger };
        header.Controls.Add(new Label
        {
            Text = "Eliminación permanente", Location = new Point(24, 14), Size = new Size(592, 32), AutoSize = false,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "Se van a borrar de tu colección y del almacenamiento. No pasan por la Papelera y no se pueden recuperar.",
            Location = new Point(24, 50), Size = new Size(592, 36), AutoSize = false, ForeColor = Color.FromArgb(255, 226, 226), BackColor = Color.Transparent
        });

        // Cuatro tarjetas con lo que se va a borrar (sin tabla).
        Panel Tile(int index, string number, string name, string size)
        {
            var tile = new Panel { Location = new Point(24 + index * 150, 112), Size = new Size(140, 98), BackColor = Color.FromArgb(247, 240, 240) };
            tile.Controls.Add(new Label
            {
                Text = number, Tag = "noloc", Location = new Point(0, 8), Size = new Size(140, 38), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 22f, FontStyle.Bold), ForeColor = danger
            });
            tile.Controls.Add(new Label { Text = name, Location = new Point(0, 50), Size = new Size(140, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font, FontStyle.Bold) });
            tile.Controls.Add(new Label { Text = size, Tag = "noloc", Location = new Point(0, 71), Size = new Size(140, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText });
            return tile;
        }
        var tiles = new[]
        {
            Tile(0, data.Roms.ToString("N0"), "ROMs", data.RomSize),
            Tile(1, data.Accessories < 0 ? "—" : data.Accessories.ToString("N0"), "Accesorios", data.AccessorySize), // un número negativo = todavía sin calcular
            Tile(2, data.Media < 0 ? "—" : data.Media.ToString("N0"), "Media de ES-DE", data.MediaSize),
            Tile(3, data.Gamelists < 0 ? "—" : data.Gamelists.ToString("N0"), "Gamelists", "")
        };

        var total = new Label
        {
            Text = $"Espacio total que se libera: {data.TotalSize}", Location = new Point(24, 222), Size = new Size(592, 26), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold)
        };

        // Aviso sobre las copias de seguridad de los gamelist.xml: ámbar si no las hay, verde si las hay.
        var note = new Panel { Location = new Point(24, 258), Size = new Size(592, 52), BackColor = data.Backups ? Color.FromArgb(226, 240, 220) : Color.FromArgb(255, 243, 205) };
        note.Controls.Add(new Label
        {
            Text = data.Backups
                ? "Se crearán copias .bak_ de los gamelist.xml afectados antes de quitar las entradas."
                : "No se crearán copias de seguridad de los gamelist.xml: sus cambios también serán permanentes.",
            Location = new Point(12, 6), Size = new Size(568, 40), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft
        });

        var cancel = new Button { Text = "Cancelar", Size = new Size(120, 36), Location = new Point(496, 334), DialogResult = DialogResult.Cancel };
        var erase = new Button
        {
            Text = "BORRAR", Size = new Size(150, 36), Location = new Point(336, 334), DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat, BackColor = danger, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), UseVisualStyleBackColor = false
        };
        erase.FlatAppearance.BorderSize = 0;
        AcceptButton = cancel; CancelButton = cancel;
        Controls.Add(header);
        Controls.AddRange(tiles);
        Controls.AddRange([total, note, erase, cancel]);
        Shown += (_, _) => cancel.Focus();
    }
}

// Confirmación del clonado: avisa de que el destino quedará como un espejo exacto del origen y de que todo lo que sobre en él se borra sin posibilidad de recuperarlo.
// Los botones son «CLONAR» y «Cancelar» (el segundo es el predeterminado, para que un Intro no borre nada).
internal sealed class CloneConfirmDialog : LocalizedForm
{
    public CloneConfirmDialog(string source, string destination, bool destinationIsDriveRoot)
    {
        Text = "Confirmar clonación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(640, 398);
        var danger = Color.FromArgb(150, 30, 30);

        var header = new Panel { Location = new Point(0, 0), Size = new Size(640, 96), BackColor = danger };
        header.Controls.Add(new Label
        {
            Text = "Se borrará contenido del destino", Location = new Point(24, 14), Size = new Size(592, 32), AutoSize = false,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "El destino quedará como una copia exacta del origen: todo lo que no exista en el origen se eliminará de la carpeta de destino, sin Papelera y sin posibilidad de recuperarlo.",
            Location = new Point(24, 50), Size = new Size(592, 42), AutoSize = false, ForeColor = Color.FromArgb(255, 226, 226), BackColor = Color.Transparent
        });

        Panel Card(int top, Color back, string title, Color titleColor, string path)
        {
            var card = new Panel { Location = new Point(24, top), Size = new Size(592, 72), BackColor = back };
            card.Controls.Add(new Label { Text = title, Location = new Point(14, 8), Size = new Size(564, 22), AutoSize = false, Font = new Font(Font, FontStyle.Bold), ForeColor = titleColor });
            card.Controls.Add(new Label { Text = path, Tag = "noloc", Location = new Point(14, 34), Size = new Size(564, 30), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft });
            return card;
        }
        var origin = Card(114, Color.FromArgb(230, 242, 230), "ORIGEN: no se modifica", Color.FromArgb(30, 100, 40), source);
        var target = Card(196, Color.FromArgb(247, 235, 235), "DESTINO: se borra lo que no esté en el origen", danger, destination);

        var note = new Panel { Location = new Point(24, 278), Size = new Size(592, 54), BackColor = destinationIsDriveRoot ? Color.FromArgb(247, 214, 214) : Color.FromArgb(255, 243, 205) };
        note.Controls.Add(new Label
        {
            Text = destinationIsDriveRoot
                ? "ATENCIÓN: el destino es la raíz de una unidad. Se eliminarán de TODA la unidad los archivos y carpetas que no existan en el origen."
                : "Se copiarán los archivos nuevos o modificados del origen y se eliminarán del destino los archivos y carpetas que no existan en el origen.",
            Location = new Point(12, 6), Size = new Size(568, 42), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Font = destinationIsDriveRoot ? new Font(Font, FontStyle.Bold) : Font
        });

        var cancel = new Button { Text = "Cancelar", Size = new Size(120, 36), Location = new Point(496, 348), DialogResult = DialogResult.Cancel };
        var clone = new Button
        {
            Text = "CLONAR", Size = new Size(150, 36), Location = new Point(336, 348), DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat, BackColor = danger, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), UseVisualStyleBackColor = false
        };
        clone.FlatAppearance.BorderSize = 0;
        AcceptButton = cancel; CancelButton = cancel;
        Controls.AddRange([header, origin, target, note, clone, cancel]);
        Shown += (_, _) => cancel.Focus();
    }
}
