using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Serilog;

namespace KioskController;

internal sealed record LeituraCapturada(string IdLeitura, string Texto, DateTimeOffset Em);

internal sealed class LeitoraKiosk : IAsyncDisposable
{
    private static readonly Regex Formato = new("^[0-9A-Za-z*.,-]{4,60}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan JanelaDeRepeticao = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PrazoDeEnvio = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(4);

    private readonly object _travaVigia = new();
    private SynchronizationContext? _contextoVigia;
    private Thread? _threadVigia;
    private readonly Func<LeituraCapturada, CancellationToken, Task<RespostaGestao>> _enviar;
    private readonly Action<string, string> _aoEvento;
    private readonly Channel<LeituraCapturada> _fila = Channel.CreateBounded<LeituraCapturada>(new BoundedChannelOptions(200)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });
    private readonly CancellationTokenSource _cts = new();
    private VigiaAreaTransferencia? _vigia;
    private Task? _envio;
    private string _ultimoTexto = string.Empty;
    private DateTime _ultimoEm = DateTime.MinValue;
    private int _entregues;
    private int _descartadas;

    public bool Ativa { get; private set; }
    public int Pendentes => _fila.Reader.Count;
    public int Entregues => _entregues;
    public int Descartadas => _descartadas;
    public DateTimeOffset? UltimaEm { get; private set; }

    public LeitoraKiosk(Func<LeituraCapturada, CancellationToken, Task<RespostaGestao>> enviar, Action<string, string> aoEvento)
    {
        _enviar = enviar;
        _aoEvento = aoEvento;
    }

    public static bool TextoValido(string texto) => Formato.IsMatch(texto);

    public void Iniciar()
    {
        _envio ??= Task.Run(() => EnviarAsync(_cts.Token));
    }

    public void Definir(bool ativa)
    {
        if (ativa == Ativa)
            return;

        Ativa = ativa;

        if (ativa)
            LigarVigia();
        else
            DesligarVigia();
    }

    private void LigarVigia()
    {
        lock (_travaVigia)
        {
            if (_threadVigia is not null)
                return;

            using var pronto = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                _contextoVigia = SynchronizationContext.Current;

                try
                {
                    _vigia = new VigiaAreaTransferencia(AoCopiar);
                    Log.Information("Captura da leitora ligada");
                    _aoEvento("LEITORA_LIGADA", "Captura da área de transferência ligada");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Falha ao ligar a captura da leitora");
                    _aoEvento("ERRO_LEITORA", ex.Message);
                    Ativa = false;
                    _vigia = null;
                }

                pronto.Set();

                if (_vigia is not null)
                    Application.Run();
            })
            {
                IsBackground = true,
                Name = "LeitoraKiosk"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            pronto.Wait(TimeSpan.FromSeconds(5));

            if (_vigia is null)
                return;

            _threadVigia = thread;
        }
    }

    private void DesligarVigia()
    {
        lock (_travaVigia)
        {
            var contexto = _contextoVigia;
            if (_threadVigia is null || contexto is null)
                return;

            contexto.Post(_ =>
            {
                _vigia?.Dispose();
                _vigia = null;
                Application.ExitThread();
            }, null);
            _threadVigia = null;
            _contextoVigia = null;
            Log.Information("Captura da leitora desligada");
            _aoEvento("LEITORA_DESLIGADA", "Captura da área de transferência desligada");
        }
    }

    private void AoCopiar(string bruto)
    {
        if (!Ativa)
            return;

        var texto = bruto.Trim();
        if (!TextoValido(texto))
            return;

        var agora = DateTime.UtcNow;
        if (texto == _ultimoTexto && agora - _ultimoEm < JanelaDeRepeticao)
            return;

        _ultimoTexto = texto;
        _ultimoEm = agora;
        UltimaEm = DateTimeOffset.Now;
        _fila.Writer.TryWrite(new LeituraCapturada(Guid.NewGuid().ToString(), texto, DateTimeOffset.Now));
    }

    private async Task EnviarAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var leitura in _fila.Reader.ReadAllAsync(ct))
            {
                await EnviarLeituraAsync(leitura, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task EnviarLeituraAsync(LeituraCapturada leitura, CancellationToken ct)
    {
        var espera = TimeSpan.FromMilliseconds(500);

        while (true)
        {
            var resposta = await _enviar(leitura, ct);

            if (!resposta.PrecisaRecuar)
            {
                var entregue = resposta.Sucesso && resposta.TemDados
                    && resposta.Dados.TryGetProperty("entregue", out var e) && e.ValueKind == JsonValueKind.True;

                if (entregue)
                {
                    Interlocked.Increment(ref _entregues);
                    return;
                }

                Interlocked.Increment(ref _descartadas);
                if (!resposta.Sucesso)
                {
                    Log.Warning("Leitura recusada pelo Gestão: {Status} {Mensagem}", resposta.Status, resposta.Mensagem);
                    _aoEvento("LEITURA_RECUSADA", resposta.Mensagem);
                }

                return;
            }

            if (DateTimeOffset.Now - leitura.Em > PrazoDeEnvio)
            {
                Interlocked.Increment(ref _descartadas);
                _aoEvento("LEITURA_NAO_ENVIADA", $"Leitura {leitura.Texto} não chegou ao Gestão em 30 s");
                return;
            }

            await Task.Delay(espera, ct);
            espera = TimeSpan.FromTicks(Math.Min(espera.Ticks * 2, EsperaMaxima.Ticks));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Definir(false);
        _fila.Writer.TryComplete();
        _cts.Cancel();

        if (_envio is not null)
        {
            try { await _envio.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }

        _cts.Dispose();
    }
}
