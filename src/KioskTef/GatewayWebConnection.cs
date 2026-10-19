using System.Net.WebSockets;
using System.Text;

namespace KioskTef;

internal sealed class GatewayWebConnection : IGatewayTef
{
    private readonly SemaphoreSlim _lockConexao = new(1, 1);
    private readonly object _lockSocket = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _tarefaReconexao;
    private volatile bool _parando;

    public GatewayWebConnection(string endereco, int intervaloReconexaoMs)
    {
        Endereco = endereco;
        IntervaloReconexaoMs = intervaloReconexaoMs;
    }

    public string Endereco { get; }
    public int IntervaloReconexaoMs { get; }
    public string Descricao => "Web Connection";
    public Action? AoAbrir { get; set; }
    public Action? AoFechar { get; set; }
    public Action<string>? AoFalhar { get; set; }
    public Func<string, bool>? AoReceber { get; set; }

    public bool Conectado => _socket is { State: WebSocketState.Open };

    public void Iniciar()
    {
        if (_tarefaReconexao is not null)
            return;

        _parando = false;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _tarefaReconexao = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!Conectado)
                        await ConectarAsync();
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(IntervaloReconexaoMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    public async Task PararAsync()
    {
        _parando = true;
        var cts = _cts;
        var tarefa = _tarefaReconexao;
        _cts = null;
        _tarefaReconexao = null;

        if (cts is not null)
        {
            cts.Cancel();

            if (tarefa is not null)
            {
                try { await tarefa; } catch { }
            }

            cts.Dispose();
        }

        await DesconectarAsync();
    }

    public async Task DesconectarAsync()
    {
        var ws = _socket;

        if (ws is null)
            return;

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(2000);
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch
        {
        }

        try { ws.Abort(); } catch { }
    }

    public async Task ConectarAsync()
    {
        await _lockConexao.WaitAsync();

        try
        {
            if (Conectado)
                return;

            var ws = new ClientWebSocket();

            try
            {
                using var cts = new CancellationTokenSource(5000);
                await ws.ConnectAsync(new Uri(Endereco), cts.Token);
            }
            catch
            {
                ws.Dispose();
                throw;
            }

            lock (_lockSocket)
            {
                _socket = ws;
            }

            try { AoAbrir?.Invoke(); } catch { }
            _ = Task.Run(() => LoopReceberAsync(ws));
        }
        finally
        {
            _lockConexao.Release();
        }
    }

    public async Task EnviarAsync(string json)
    {
        var ws = _socket;

        if (ws is null || ws.State != WebSocketState.Open)
            throw new ExcecaoTef(SituacaoTef.NaoEnviada, "Sem comunicação com o Sankhya Web Connection. " +
                                                         "Verifique se ele está aberto nesta máquina.");

        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task LoopReceberAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;

                do
                {
                    r = await ws.ReceiveAsync(buffer, CancellationToken.None);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return;
                    }

                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);

                var texto = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);

                try { AoReceber?.Invoke(texto); } catch { }
            }
        }
        catch (Exception e)
        {
            if (!_parando)
            {
                try { AoFalhar?.Invoke(e.Message); } catch { }
            }
        }
        finally
        {
            var atual = false;

            lock (_lockSocket)
            {
                if (ReferenceEquals(_socket, ws))
                {
                    _socket = null;
                    atual = true;
                }
            }

            if (atual)
            {
                try { AoFechar?.Invoke(); } catch { }
            }

            try { ws.Dispose(); } catch { }
        }
    }
}
