using System.Diagnostics;
using System.Net.Sockets;

namespace KioskTef;

public sealed class ServicoTef : IAsyncDisposable
{
    private readonly string _versao;
    private readonly Action<string>? _aoRegistrar;
    private readonly GerenciadorSitef _gerenciador = new();
    private readonly ImpressoraComprovante _impressora;
    private readonly SemaphoreSlim _lockCiclo = new(1, 1);
    private readonly object _lockEstado = new();
    private ServidorTef? _servidor;
    private ConfiguracaoImpressao _impressao;
    private int _porta = 8069;
    private string _erro = "";
    private string? _ultimaIdent;
    private string? _ultimaSituacao;
    private DateTimeOffset? _ultimaEm;
    private bool _iniciado;
    private volatile IReadOnlyList<string> _origensPermitidas = Array.Empty<string>();
    private volatile bool _origensObrigatorio;

    public event Action<string, string>? Evento;

    public event Action<bool>? OcupadoAlterado;

    public string PastaBase
    {
        get => _gerenciador.Pasta;
        set => _gerenciador.Pasta = value;
    }

    public string EnderecoWebConnection
    {
        get => _gerenciador.Endereco;
        set => _gerenciador.Endereco = value;
    }

    public int TempoLimitePagamentoMs { get; set; } = 600000;

    public Func<string, Task<(int Status, System.Text.Json.Nodes.JsonObject Corpo)>>? Atestador { get; set; }

    public Func<System.Text.Json.Nodes.JsonObject>? InformarCaixa { get; set; }

    public ConfiguracaoImpressao Impressao => _impressao;

    public ServicoTef(string versao, Action<string>? aoRegistrar = null)
    {
        _versao = versao;
        _aoRegistrar = aoRegistrar;
        _impressao = new ConfiguracaoImpressao();
        _impressora = new ImpressoraComprovante(() => _impressao);
        _gerenciador.AoRegistrar = Registrar;
        _gerenciador.AoMudarConexao = c => Disparar("TEF_WEBCONNECTION", c ? "conectado" : "desconectado");
        _gerenciador.AoEvento = Disparar;
        _gerenciador.AoMudarOcupado = o =>
        {
            try { OcupadoAlterado?.Invoke(o); } catch { }
        };
    }

    public EstadoTef Estado
    {
        get
        {
            lock (_lockEstado)
            {
                return new EstadoTef
                {
                    Servidor = _servidor is { Executando: true },
                    WebConnection = _gerenciador.Conectado,
                    Ocupado = _gerenciador.Ocupado,
                    Pendente = _gerenciador.Pendente,
                    Porta = _porta,
                    Erro = _erro,
                    UltimaIdentificacao = _ultimaIdent,
                    UltimaSituacao = _ultimaSituacao,
                    UltimaEm = _ultimaEm
                };
            }
        }
    }

    public bool TentarReservar() => _gerenciador.TentarReservar();

    public void LiberarReserva() => _gerenciador.LiberarReserva();

    public static IReadOnlyList<string> ListarImpressoras() => ImpressoraComprovante.ListarImpressoras();

    public void DefinirOrigensPermitidas(IEnumerable<string> origens, bool obrigatorio)
    {
        _origensObrigatorio = obrigatorio;
        _origensPermitidas = origens.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void DefinirImpressao(ConfiguracaoImpressao config)
    {
        _impressao = config;
    }

    public void DefinirTransporte(string? modo, string? pastaRequisicao, string? pastaResposta)
    {
        _gerenciador.Modo = string.IsNullOrWhiteSpace(modo) ? GerenciadorSitef.ModoWebConnection : modo.Trim();

        if (!string.IsNullOrWhiteSpace(pastaRequisicao))
            _gerenciador.PastaRequisicao = pastaRequisicao.Trim();

        if (!string.IsNullOrWhiteSpace(pastaResposta))
            _gerenciador.PastaResposta = pastaResposta.Trim();
    }

    public void ImprimirTeste()
    {
        try
        {
            _impressora.ImprimirTeste();
            var msg = "Teste enviado para a impressora " + _impressao.Impressora + ".";
            Registrar(msg);
            Disparar("TEF_IMPRESSAO", msg);
        }
        catch (Exception e)
        {
            var msg = "Erro no teste de impressão: " + e.Message;
            Registrar(msg);
            Disparar("TEF_IMPRESSAO", msg);
            throw;
        }
    }

    private void Registrar(string mensagem)
    {
        try { _aoRegistrar?.Invoke(mensagem); } catch { }
    }

    private void Disparar(string evento, string detalhe)
    {
        try { Evento?.Invoke(evento, detalhe); } catch { }
    }

    private void DefinirUltima(string ident, string situacao)
    {
        lock (_lockEstado)
        {
            _ultimaIdent = ident;
            _ultimaSituacao = situacao;
            _ultimaEm = DateTimeOffset.Now;
        }
    }

    public async Task IniciarAsync(int porta = 8069)
    {
        await _lockCiclo.WaitAsync();

        try
        {
            if (_iniciado)
                return;

            _iniciado = true;
            _porta = porta;
            _erro = "";

            _gerenciador.ConfigurarPasta(_gerenciador.Pasta);
            _gerenciador.Iniciar();

            await IniciarServidorAsync(porta, true);
        }
        finally
        {
            _lockCiclo.Release();
        }
    }

    private static bool PortaEmUso(Exception e)
    {
        for (var ex = e; ex is not null; ex = ex.InnerException!)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;

            if (ex.GetType().Name == "AddressInUseException")
                return true;

            if (ex.InnerException is null)
                break;
        }

        return false;
    }

    private async Task IniciarServidorAsync(int porta, bool tentarLiberar)
    {
        var servidor = new ServidorTef(_gerenciador, _impressora, () => _impressao, _versao, () => TempoLimitePagamentoMs,
            Registrar, Disparar, DefinirUltima, () => _origensPermitidas, () => _origensObrigatorio, () => Atestador, () => InformarCaixa);

        try
        {
            await servidor.IniciarAsync(porta);
            _servidor = servidor;
            Registrar("Servidor executando na porta " + porta);
        }
        catch (Exception e)
        {
            if (PortaEmUso(e))
            {
                if (tentarLiberar && EncerrarTefManagerDelphi())
                {
                    await Task.Delay(500);
                    await IniciarServidorAsync(porta, false);
                    return;
                }

                _erro = "A porta " + porta + " está em uso por outro programa.";
            }
            else
                _erro = e.Message;

            Registrar(_erro);
            Disparar("TEF_ERRO", "SERVIDOR: " + _erro);
        }
    }

    private bool EncerrarTefManagerDelphi()
    {
        var encerrou = false;

        foreach (var p in Process.GetProcessesByName("TEFManagerUP"))
        {
            try
            {
                if (p.Id == Environment.ProcessId)
                    continue;

                Registrar("Encerrando o TEFManagerUP.exe que ocupa a porta.");
                p.Kill();
                p.WaitForExit(3000);
                encerrou = true;
            }
            catch (Exception e)
            {
                Registrar("Não foi possível encerrar o TEFManagerUP.exe: " + e.Message);
            }
            finally
            {
                p.Dispose();
            }
        }

        return encerrou;
    }

    public async Task PararAsync()
    {
        await _lockCiclo.WaitAsync();

        try
        {
            await PararInternoAsync();
        }
        finally
        {
            _lockCiclo.Release();
        }
    }

    private async Task PararInternoAsync()
    {
        var servidor = _servidor;
        _servidor = null;

        if (servidor is not null)
            await servidor.PararAsync();

        await _gerenciador.PararAsync();
        _iniciado = false;
    }

    public async Task<bool> ReiniciarAsync()
    {
        if (!_gerenciador.TentarReservar())
            return false;

        try
        {
            await _lockCiclo.WaitAsync();
        }
        catch
        {
            _gerenciador.LiberarReserva();
            throw;
        }

        try
        {
            await PararInternoAsync();

            _iniciado = true;
            _erro = "";
            _gerenciador.Iniciar();
            await IniciarServidorAsync(_porta, true);
            return true;
        }
        finally
        {
            _lockCiclo.Release();
            _gerenciador.LiberarReserva();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await PararAsync(); } catch { }
    }
}
