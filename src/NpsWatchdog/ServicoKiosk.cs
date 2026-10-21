using Microsoft.Extensions.Hosting;
using Serilog;

namespace NpsWatchdog;

public sealed class ServicoKiosk : BackgroundService
{
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Information("Serviço do KioskMax {Versao} iniciado", AtualizacaoKiosk.VersaoAtual);
        using var gestao = new ClienteGestaoServico();
        using var baixador = new Baixador();
        var remoto = new AcessoRemoto(gestao, baixador);
        var atualizacao = new AtualizacaoKiosk(gestao, baixador);

        try
        {
            await Task.Delay(EsperaInicial, ct);
            while (!ct.IsCancellationRequested)
            {
                await ExecutarAsync("acesso remoto", remoto.ExecutarAsync, ct);
                await ExecutarAsync("atualização do KioskMax", atualizacao.ExecutarAsync, ct);
                await Task.Delay(Intervalo, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        Log.Information("Serviço do KioskMax encerrado");
    }

    private static async Task ExecutarAsync(string nome, Func<CancellationToken, Task> tarefa, CancellationToken ct)
    {
        try
        {
            await tarefa(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro no ciclo do serviço do KioskMax ({Tarefa})", nome);
        }
    }
}
