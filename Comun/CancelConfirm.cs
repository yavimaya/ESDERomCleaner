namespace ESDERomCleaner;

// Confirmación común al pulsar Cancelar/Detener/Cerrar mientras un formulario está ejecutando una operación.
internal static class CancelConfirm
{
    // true si el usuario confirma que quiere cancelar «operation» (texto en español, ya traducible).
    public static bool Ask(IWin32Window? owner, string operation)
    {
        using var dialog = new CancelOperationDialog(operation);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}

// Aviso con el mismo formato que el resto de formularios: cabecera oscura con la operación en curso, un mensaje y los dos botones.
// «Seguir» es el botón por defecto (y el de Esc), para que cancelar sea siempre una decisión explícita.
internal sealed class CancelOperationDialog : LocalizedForm
{
    public CancelOperationDialog(string operation)
    {
        Text = "Cancelar la operación";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        BackColor = RaUi.Body;
        ClientSize = new Size(520, 210);

        var banner = new RaHeader("¿Cancelar la operación?", Loc.T("Se está ejecutando: {0}", Loc.T(operation)), 68, 48, logo: false);
        banner.Subtitle.Tag = "noloc";
        banner.Icon.Image = RaUi.InfoIcon(3);

        var message = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(24, 20, 24, 0), ForeColor = RaUi.Dark,
            Text = "¿Seguro que quieres cancelarla? Lo ya hecho se conserva."
        };

        var footer = RaUi.Footer();
        var keep = RaUi.MakeButton("Seguir", true, 110); keep.DialogResult = DialogResult.Cancel;
        var cancel = RaUi.MakeButton("Sí, cancelar", false, 120); cancel.DialogResult = DialogResult.OK;
        keep.Location = new Point(520 - 24 - keep.Width, 10);
        cancel.Location = new Point(keep.Left - 10 - cancel.Width, 10);
        footer.Controls.AddRange([keep, cancel]);

        Controls.Add(message); Controls.Add(footer); Controls.Add(banner);
        AcceptButton = keep; CancelButton = keep;
        CollectionTitle.Attach(this);
    }
}
