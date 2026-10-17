namespace ESDERomCleaner;

// Pausa las operaciones largas mientras el usuario decide si cancelar: CancelConfirm.Ask la cierra al abrir el aviso y la abre al cerrarlo.
// Las operaciones llaman a Checkpoint donde ya comprobaban la cancelación; si no hay pausa, cuesta una lectura de un campo.
internal static class PauseGate
{
    private static volatile int paused;

    public static void Pause() => Interlocked.Increment(ref paused);

    public static void Resume() { if (Interlocked.Decrement(ref paused) < 0) Interlocked.Exchange(ref paused, 0); }

    // Igual que Checkpoint pero sin bloquear el hilo: para el código asíncrono (descargas, consultas), que puede ejecutarse en el hilo de la interfaz.
    public static async Task WaitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (paused > 0) await Task.Delay(50, token);
    }

    // Lanza OperationCanceledException si se canceló y, mientras haya una pausa, espera (despertándose para atender la cancelación).
    public static void Checkpoint(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (paused > 0)
        {
            if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
        }
    }
}
