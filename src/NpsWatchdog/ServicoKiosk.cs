using Microsoft.Extensions.Hosting;
using Serilog;

namespace NpsWatchdog;

public sealed class ServicoKiosk : BackgroundService
{
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Information("Serviço do KioskMax iniciado");
        using var gestao = new ClienteGestaoServico();
        using var remoto = new AcessoRemoto(gestao);

        try
        {
            await Task.Delay(EsperaInicial, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await remoto.ExecutarAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Erro no ciclo do serviço do KioskMax");
                }

                await Task.Delay(Intervalo, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        Log.Information("Serviço do KioskMax encerrado");
    }
}
