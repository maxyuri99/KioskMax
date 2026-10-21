using Serilog;

namespace KioskController;

internal sealed class InstanciaUnica : IDisposable
{
    private const string NomeTrava = @"Local\KioskMax_Controller";
    private const string NomeChamado = @"Local\KioskMax_Controller_Chamado";
    private const string NomeResposta = @"Local\KioskMax_Controller_Respondeu";
    private static readonly TimeSpan PrazoResposta = TimeSpan.FromSeconds(10);

    private readonly Mutex _trava;
    private readonly EventWaitHandle _chamado;
    private readonly EventWaitHandle _resposta;
    private RegisteredWaitHandle? _espera;

    public bool Primeira { get; }

    private InstanciaUnica(Mutex trava, bool primeira)
    {
        _trava = trava;
        Primeira = primeira;
        _chamado = new EventWaitHandle(false, EventResetMode.AutoReset, NomeChamado);
        _resposta = new EventWaitHandle(false, EventResetMode.AutoReset, NomeResposta);
    }

    public static InstanciaUnica Obter()
    {
        var trava = new Mutex(false, NomeTrava);
        bool primeira;
        try
        {
            primeira = trava.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            primeira = true;
        }
        return new InstanciaUnica(trava, primeira);
    }

    public bool OutraRespondeu()
    {
        _resposta.Reset();
        _chamado.Set();
        var respondeu = _resposta.WaitOne(PrazoResposta);
        Log.Information(respondeu
            ? "Outra instância do KioskMax já está aberta nesta sessão; esta vai encerrar"
            : "Outra instância do KioskMax não respondeu em {Segundos} s; esta vai assumir", PrazoResposta.TotalSeconds);
        return respondeu;
    }

    public void Assumir()
    {
        try
        {
            _trava.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
        }
    }

    public void AoSerChamada(Action acao)
    {
        _espera = ThreadPool.RegisterWaitForSingleObject(_chamado, (_, _) =>
        {
            _resposta.Set();
            try
            {
                acao();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao atender outra abertura do KioskMax");
            }
        }, null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _espera?.Unregister(null);
        try
        {
            if (Primeira) _trava.ReleaseMutex();
        }
        catch
        {
        }
        _trava.Dispose();
        _chamado.Dispose();
        _resposta.Dispose();
    }
}
