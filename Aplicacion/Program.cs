namespace ESDERomCleaner
{
    internal static class Program
    {
        private static int reporting;

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Red de seguridad: un error inesperado en cualquier evento o tarea se anota en la trazabilidad y se avisa,
            // en lugar de cerrar la aplicación con el cuadro de excepción de .NET.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Report("interfaz", e.Exception, show: true);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Report("proceso", e.ExceptionObject as Exception, show: false);
            TaskScheduler.UnobservedTaskException += (_, e) => { Report("tarea", e.Exception, show: false); e.SetObserved(); };

            Application.Run(new RomCleanerForm());
        }

        private static void Report(string origin, Exception? exception, bool show)
        {
            try
            {
                new TraceLog().Add("unhandled_exception", $"origin={origin}; {exception}");
                // Solo un aviso a la vez, para que una cadena de errores no apile cuadros de diálogo.
                if (!show || Interlocked.Exchange(ref reporting, 1) == 1) return;
                try
                {
                    QuietMessage.Show(null, "Error inesperado",
                        "Ha ocurrido un error inesperado y la acción en curso puede no haberse completado.\r\n\r\n" +
                        (exception?.Message ?? "Error desconocido") +
                        "\r\n\r\nSe ha anotado en Trazabilidad › Ver registro de trazas. La aplicación sigue funcionando; si algo no se ve bien, vuelve a analizar la colección.");
                }
                finally { Interlocked.Exchange(ref reporting, 0); }
            }
            catch { /* el informe de errores nunca debe provocar otro error */ }
        }
    }
}
