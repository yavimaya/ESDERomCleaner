using System.Diagnostics;

namespace ESDERomCleaner;

// Gestión de la cuenta de ScreenScraper desde un único sitio: el menú Opciones de la ventana principal. Los formularios que
// necesitan credenciales no tienen botón propio: si faltan al abrirse, ofrecen registrarlas (o darse de alta) y siguen.
internal static class ScreenScraperAccount
{
    public const string SignUpUrl = "https://www.screenscraper.fr/membreinscription.php";

    public static void OpenSignUp(IWin32Window? owner)
    {
        try { Process.Start(new ProcessStartInfo(SignUpUrl) { UseShellExecute = true }); }
        catch (Exception ex) { QuietMessage.Show(owner, "Alta en ScreenScraper", $"No se pudo abrir el navegador ({ex.Message}).\r\n\r\nAbre esta dirección a mano:\r\n{SignUpUrl}"); }
    }

    // Abre el formulario de credenciales y, si se acepta, las guarda cifradas. Devuelve null si se cancela o no se pueden guardar.
    public static ScreenScraperCredentials? Edit(IWin32Window owner, TraceLog trace, ScreenScraperCredentials current)
    {
        using var dialog = new ScreenScraperCredentialsDialog(current);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Result is null) return null;
        try
        {
            ScreenScraperCredentialStore.Save(dialog.Result);
            trace.Add("screenscraper_credentials_saved", $"user={(dialog.Result.UserId.Length > 0 ? dialog.Result.UserId : "(anónimo)")}");
        }
        catch (Exception ex)
        {
            QuietMessage.Show(owner, "Credenciales de ScreenScraper", "No se pudieron guardar las credenciales: " + ex.Message);
            return null;
        }
        return dialog.Result;
    }

    // Al abrir un formulario que usa ScreenScraper: si no hay credenciales, pregunta si se quieren registrar ahora.
    // Devuelve las credenciales vigentes (las nuevas, o las que ya había si el usuario no quiere o no puede registrarlas).
    public static ScreenScraperCredentials Ensure(IWin32Window owner, TraceLog trace, ScreenScraperCredentials current)
    {
        while (!current.HasDeveloper)
        {
            using var prompt = new MissingCredentialsPrompt();
            var answer = prompt.ShowDialog(owner);
            if (answer == DialogResult.Retry) { OpenSignUp(owner); continue; }
            if (answer != DialogResult.Yes) break;
            current = Edit(owner, trace, current) ?? current;
            if (!current.HasDeveloper) break;
        }
        return current;
    }

    // Aviso previo, sin sonido: registrar credenciales, darse de alta en ScreenScraper o dejarlo para más tarde.
    private sealed class MissingCredentialsPrompt : Form
    {
        public MissingCredentialsPrompt()
        {
            Text = "ScreenScraper";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 170);
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            var text = new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(14, 14, 14, 0),
                Text = "No tienes credenciales de ScreenScraper guardadas, y sin ellas no se pueden consultar ni descargar medios.\r\n\r\n" +
                       "¿Quieres registrarlas ahora? Si todavía no tienes cuenta, puedes darte de alta en la web de ScreenScraper " +
                       "y volver aquí con tus datos. También las tienes siempre en Opciones › Credenciales de ScreenScraper."
            };
            var register = new Button { Text = "Registrar credenciales…", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(160, 28) };
            var signUp = new Button { Text = "Alta en ScreenScraper", DialogResult = DialogResult.Retry, AutoSize = true, MinimumSize = new Size(150, 28) };
            var later = new Button { Text = "Ahora no", DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new Size(90, 28) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 4) };
            buttons.Controls.Add(later);
            buttons.Controls.Add(signUp);
            buttons.Controls.Add(register);
            Controls.Add(text);
            Controls.Add(buttons);
            AcceptButton = register;
            CancelButton = later;
        }
    }
}
