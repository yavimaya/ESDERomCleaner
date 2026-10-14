namespace ESDERomCleaner;

// Pregunta tras crear una colección: ¿cambiar a ella ahora? Con el formato de los formularios nuevos (cabecera oscura con la carpeta, cuerpo claro y pie con botones).
internal sealed class CollectionCreatedDialog : LocalizedForm
{
    public CollectionCreatedDialog(string name)
    {
        Text = "Colección creada";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(580, 68 + 112 + 52);

        var banner = new RaHeader("Colección creada", $"Colección «{name}» creada.", 68, 48, logo: false);
        banner.Icon.Image = RaUi.FolderIcon();
        var body = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 8), BackColor = RaUi.Body, TextAlign = ContentAlignment.TopLeft,
            Text = "¿Quieres cambiar a ella ahora? Se descargará la colección cargada y el formulario quedará vacío; el primer «Listar colección» lo preparará todo."
        };
        var footer = RaUi.Footer(52);
        var change = RaUi.MakeButton("Cambiar", true, 120);
        change.DialogResult = DialogResult.OK; change.Location = new Point(580 - 20 - 100 - 8 - 120, 10);
        var cancel = RaUi.MakeButton("Cancelar", false, 100);
        cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(580 - 20 - 100, 10);
        footer.Controls.AddRange([change, cancel]);
        AcceptButton = change; CancelButton = cancel;
        Controls.Add(body); Controls.Add(footer); Controls.Add(banner);
    }
}
