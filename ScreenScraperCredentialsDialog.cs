namespace ESDERomCleaner;

internal sealed class ScreenScraperCredentialsDialog : Form
{
    private readonly TextBox devId = new() { Dock = DockStyle.Fill };
    private readonly TextBox devPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox softName = new() { Dock = DockStyle.Fill };
    private readonly TextBox userId = new() { Dock = DockStyle.Fill };
    private readonly TextBox userPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    public ScreenScraperCredentials? Result { get; private set; }

    public ScreenScraperCredentialsDialog(ScreenScraperCredentials current)
    {
        Text = "Credenciales de ScreenScraper";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 270);
        devId.Text = current.DevId; devPassword.Text = current.DevPassword;
        softName.Text = string.IsNullOrWhiteSpace(current.SoftName) ? "ESDERomCleaner" : current.SoftName;
        userId.Text = current.UserId; userPassword.Text = current.UserPassword;

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 190, ColumnCount = 2, RowCount = 5, Padding = new Padding(12, 12, 12, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(int index, string label, Control box)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index);
            grid.Controls.Add(box, 1, index);
        }
        Row(0, "ID de desarrollador (devid):", devId);
        Row(1, "Contraseña de desarrollador:", devPassword);
        Row(2, "Nombre del software:", softName);
        Row(3, "Usuario ScreenScraper (opcional):", userId);
        Row(4, "Contraseña de usuario:", userPassword);

        var note = new Label
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 4, 12, 0),
            Text = "Se guardan cifradas con tu usuario de Windows (DPAPI), solo en este equipo. El usuario opcional amplía tus cuotas y subproceso."
        };
        var ok = new Button { Text = "Guardar", DialogResult = DialogResult.OK, Location = new Point(300, 232), Size = new Size(80, 28) };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(386, 232), Size = new Size(80, 28) };
        ok.Click += (_, _) => Result = new ScreenScraperCredentials(devId.Text.Trim(), devPassword.Text, softName.Text.Trim(), userId.Text.Trim(), userPassword.Text);
        var signUp = new Button { Text = "Alta en ScreenScraper", Location = new Point(12, 232), Size = new Size(160, 28) };
        signUp.Click += (_, _) => ScreenScraperAccount.OpenSignUp(this);
        Controls.Add(signUp);
        Controls.Add(ok); Controls.Add(cancel); Controls.Add(note); Controls.Add(grid);
        AcceptButton = ok; CancelButton = cancel;
    }
}
