using Microsoft.Extensions.Hosting;
using NpsShared.Utilities;
using Serilog;

namespace NpsWatchdog;

public sealed class SupervisorAgente : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CarenciaLogon = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IntervaloMinimoAbertura = TimeSpan.FromMinutes(1);
    private const int AusenciasParaAbrir = 2;

    private uint? _sessao;
    private DateTime _sessaoVistaEm;
    private int _ausencias;
    private DateTime _ultimaAbertura = DateTime.MinValue;

    private static string ExecutavelAgente => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Controller", "KioskController.exe"));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Conferir();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Falha ao conferir o KioskController na sessão do operador");
                }

                await Task.Delay(Intervalo, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Conferir()
    {
        var sessao = SessaoUsuario.SessaoAtiva();
        if (sessao != _sessao)
        {
            _sessao = sessao;
            _sessaoVistaEm = DateTime.UtcNow;
            _ausencias = 0;
        }

        if (sessao == null || DateTime.UtcNow - _sessaoVistaEm < CarenciaLogon) return;

        if (SessaoUsuario.Rodando("KioskController", sessao.Value))
        {
            _ausencias = 0;
            return;
        }

        if (AtualizacaoKiosk.EmAndamento || SaidaSolicitada.NaSessao(sessao.Value) || !File.Exists(ExecutavelAgente)) return;

        _ausencias++;
        if (_ausencias < AusenciasParaAbrir || DateTime.UtcNow - _ultimaAbertura < IntervaloMinimoAbertura) return;

        _ausencias = 0;
        _ultimaAbertura = DateTime.UtcNow;
        Log.Warning("O KioskController não está aberto na sessão {Sessao} do operador; abrindo de novo", sessao.Value);
        SessaoUsuario.Abrir(ExecutavelAgente);
    }
}
