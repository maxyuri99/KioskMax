using Serilog;

namespace KioskController;

internal static class SinalTefOcupado
{
    private const string Nome = @"Local\KioskMax_TEF_OCUPADO";
    private static readonly object Trava = new();
    private static EventWaitHandle? _sinal;

    public static void Marcar(bool ocupado)
    {
        lock (Trava)
        {
            try
            {
                _sinal ??= new EventWaitHandle(false, EventResetMode.ManualReset, Nome);

                if (ocupado)
                    _sinal.Set();
                else
                    _sinal.Reset();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao sinalizar o estado do TEF para outras instâncias");
            }
        }
    }

    public static bool OutraInstanciaOcupada()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Nome, out var sinal))
                return false;

            using (sinal)
                return sinal.WaitOne(0);
        }
        catch
        {
            return false;
        }
    }
}
