using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KioskTef;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using NpsShared.Utilities;
using Serilog;

namespace KioskController;

public enum EstadoGestao
{
    Desativado,
    Conectando,
    Online,
    Offline,
    Aguardando,
    Bloqueado,
    Recusado
}

public sealed class AgenteKiosk : IAsyncDisposable
{
    private const int MaxEventosPorSinal = 20;
    private const int MaxEventosFila = 200;
    private static readonly int[] Recuos = { 5, 10, 20, 30 };
    private static readonly TimeSpan RetencaoComandosExecutados = TimeSpan.FromHours(6);

    private readonly CancellationTokenSource _cts = new();
    private readonly ClienteGestao _cliente = new();
    private readonly SemaphoreSlim _acordar = new(0);
    private readonly SemaphoreSlim _travaAplicacao = new(1, 1);
    private readonly object _travaEventos = new();
    private readonly List<JsonObject> _eventos = new();
    private readonly string _versaoApp;
    private Task? _tarefa;
    private string? _token;
    private bool _aplicadoNaSessao;
    private bool _tefIniciado;
    private int _indiceRecuo;
    private readonly Dictionary<int, (DateTime Em, bool Sucesso, string Mensagem)> _comandosExecutados = new();
    private DateTime _impressorasEm = DateTime.MinValue;
    private IReadOnlyList<string> _impressoras = Array.Empty<string>();
    private DateTime _ultimaReinicializacaoTela = DateTime.MinValue;
    private readonly LeitoraKiosk _leitora;

    public GerenciadorTela Tela { get; } = new();
    public ServicoTef Tef { get; }

    public EstadoGestao Estado { get; private set; } = EstadoGestao.Desativado;
    public string Situacao { get; private set; } = string.Empty;
    public string MensagemEstado { get; private set; } = string.Empty;
    public DateTimeOffset? UltimoSinalEm { get; private set; }
    public int CdKiosk { get; private set; }
    public string NomeKiosk { get; private set; } = string.Empty;
    public string DescricaoCaixa { get; private set; } = string.Empty;
    public string NomeTela { get; private set; } = string.Empty;
    public string VersaoApp => _versaoApp;

    public bool ControladoPeloGestao
    {
        get
        {
            var cfg = ConfigManager.LoadConfig();
            return !string.IsNullOrWhiteSpace(cfg.GestaoApiUrl)
                && string.Equals(cfg.UltimaSituacao, "ATIVO", StringComparison.OrdinalIgnoreCase);
        }
    }

    public event Action? EstadoAlterado;
    public event Action? ReiniciarSolicitado;

    public AgenteKiosk()
    {
        var assembly = typeof(AgenteKiosk).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _versaoApp = (info ?? assembly.GetName().Version?.ToString(3) ?? "2.0.0").Split('+')[0];

        var versaoTef = assembly.GetName().Version?.ToString(4) ?? "2.0.0.0";
        Tef = new ServicoTef(versaoTef, mensagem => Log.Information("[TEF] {Mensagem}", mensagem));
        Tef.Evento += AoEventoTef;
        Tef.OcupadoAlterado += SinalTefOcupado.Marcar;
        SinalTefOcupado.Marcar(false);

        Tef.Atestador = AtestarAsync;
        Tef.InformarCaixa = InformarCaixa;
        _leitora = new LeitoraKiosk(
            (leitura, ct) => _cliente.LeituraAsync(leitura.IdLeitura, leitura.Texto, leitura.Em, ct), Enfileirar);

        Tela.Evento += (tipo, detalhe) => Enfileirar(tipo, detalhe);
        Tela.SaiuInesperadamente += AoTelaSairInesperadamente;
        SenhaLocal.Registro += (evento, acao) => Enfileirar(evento, acao);
    }

    public void Iniciar()
    {
        _leitora.Iniciar();
        _tarefa = Task.Run(() => ExecutarAsync(_cts.Token));
    }

    public void AcordarAgora()
    {
        _aplicadoNaSessao = false;
        try { _acordar.Release(); } catch { }
    }

    public void EsquecerToken()
    {
        _token = null;
        KioskToken.Apagar();
    }

    private async Task ExecutarAsync(CancellationToken ct)
    {
        try
        {
            Enfileirar("AGENTE_INICIADO", $"Versão {_versaoApp}");
            await IniciarTefAsync();
            await IniciarTelaInicialAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha na inicialização do agente");
            Enfileirar("ERRO_AGENTE", ex.Message);
        }

        while (!ct.IsCancellationRequested)
        {
            var espera = 5;
            try
            {
                espera = await CicloAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro no ciclo do agente");
                Enfileirar("ERRO_AGENTE", ex.Message);
                espera = 5;
            }

            try
            {
                await _acordar.WaitAsync(TimeSpan.FromSeconds(espera), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task IniciarTefAsync()
    {
        var cfg = ConfigManager.LoadConfig();
        if (!cfg.TefAtivo)
        {
            Log.Information("TEF desativado na configuração local");
            return;
        }

        try
        {
            AtualizarConfigTef(cfg);
            await Tef.IniciarAsync();
            _tefIniciado = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao iniciar o serviço TEF");
            Enfileirar("ERRO_TEF", ex.Message);
        }
    }

    private async Task IniciarTelaInicialAsync()
    {
        var cfg = ConfigManager.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.GestaoApiUrl))
        {
            if (cfg.RouterEnabled)
            {
                try { Tela.IniciarRoteador(); } catch (Exception ex) { Log.Error(ex, "Failed to auto-start WindowRouter"); }
            }

            if (cfg.AutoRecover && !Tela.EstaRodando)
            {
                await Task.Delay(1000);
                await Tela.IniciarAsync(false);
            }
            return;
        }

        if (string.IsNullOrEmpty(cfg.UltimaSituacao))
        {
            Log.Information("Aguardando o primeiro contato com o Gestão para abrir a tela");
            return;
        }

        if (cfg.GestaoExibir)
        {
            await Tela.IniciarAsync(cfg.RouterEnabled);
        }
    }

    private async Task<int> CicloAsync(CancellationToken ct)
    {
        var cfg = ConfigManager.LoadConfig();
        var url = cfg.GestaoApiUrl?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(url))
        {
            _leitora.Definir(false);
            DefinirEstado(EstadoGestao.Desativado, string.Empty, cfg.UltimaSituacao);
            return 5;
        }

        _cliente.UrlBase = url;
        _cliente.Identificador = cfg.KioskIdentificador;

        _token ??= KioskToken.Ler();

        if (string.IsNullOrEmpty(_token))
        {
            var registro = await RegistrarAsync(ct);
            if (registro >= 0) return registro;
        }

        _cliente.Token = _token;

        var payload = await MontarSinalAsync(cfg);
        var resposta = await _cliente.SinalAsync(payload, ct);

        if (resposta.PrecisaRecuar)
        {
            RestaurarEventos(payload);
            DefinirEstado(EstadoGestao.Offline, "Sem comunicação com o Gestão", cfg.UltimaSituacao);
            return ProximoRecuo();
        }

        if (resposta.Status == 401)
        {
            RestaurarEventos(payload);
            Log.Warning("Token do kiosk recusado pelo Gestão; registrando novamente");
            EsquecerToken();
            _aplicadoNaSessao = false;
            return 1;
        }

        if (resposta.Status == 403)
        {
            RestaurarEventos(payload);
            _indiceRecuo = 0;
            DefinirEstado(EstadoGestao.Bloqueado, "Kiosk bloqueado no Gestão", "BLOQUEADO");
            await AplicarSituacaoNaoAtivaAsync("BLOQUEADO", cfg.KioskContexto?.NmKiosk ?? Environment.MachineName);
            return 5;
        }

        if (!resposta.Sucesso || !resposta.TemDados)
        {
            RestaurarEventos(payload);
            Log.Warning("Resposta inesperada do sinal: {Status} {Erro} {Mensagem}", resposta.Status, resposta.Erro, resposta.Mensagem);
            DefinirEstado(EstadoGestao.Offline, $"Resposta inesperada ({resposta.Status})", cfg.UltimaSituacao);
            return ProximoRecuo();
        }

        _indiceRecuo = 0;
        UltimoSinalEm = DateTimeOffset.Now;

        var dados = resposta.Dados;
        var situacao = LerTexto(dados, "deSituacao");
        var intervalo = dados.TryGetProperty("intervaloSinal", out var iv) && iv.ValueKind == JsonValueKind.Number
            ? Math.Clamp(iv.GetInt32(), 2, 60)
            : 5;

        if (dados.TryGetProperty("cdKiosk", out var cd) && cd.ValueKind == JsonValueKind.Number) CdKiosk = cd.GetInt32();
        NomeKiosk = LerTexto(dados, "nmKiosk");

        if (string.Equals(situacao, "ATIVO", StringComparison.OrdinalIgnoreCase))
        {
            AplicarSenhaLocal(LerTexto(dados, "senhaLocal"));
        }

        try
        {
            await AplicarAsync(dados, situacao);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao aplicar a configuração do Gestão");
            Enfileirar("ERRO_CONFIG", ex.Message);
        }

        if (string.Equals(situacao, "ATIVO", StringComparison.OrdinalIgnoreCase)
            && ConfigManager.LoadConfig() is { TefAtivo: false } && _tefIniciado)
        {
            await AplicarTefAsync(ConfigManager.LoadConfig());
        }

        try
        {
            await ExecutarComandosAsync(dados, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao executar comandos do Gestão");
            Enfileirar("ERRO_COMANDO", ex.Message);
        }

        return intervalo;
    }

    private async Task<int> RegistrarAsync(CancellationToken ct)
    {
        DefinirEstado(EstadoGestao.Conectando, "Registrando no Gestão", ConfigManager.LoadConfig().UltimaSituacao);
        var resposta = await _cliente.RegistrarAsync(Environment.MachineName, _versaoApp, ct);

        if (resposta.PrecisaRecuar)
        {
            DefinirEstado(EstadoGestao.Offline, "Sem comunicação com o Gestão", ConfigManager.LoadConfig().UltimaSituacao);
            return ProximoRecuo();
        }

        if (resposta.Status == 409)
        {
            Log.Warning("Registro recusado: identificador já registrado com outro token");
            DefinirEstado(EstadoGestao.Recusado, "Vínculo recusado: redefina no Gestão", ConfigManager.LoadConfig().UltimaSituacao);
            return 60;
        }

        if (!resposta.Sucesso || !resposta.TemDados
            || !resposta.Dados.TryGetProperty("token", out var tokenEl)
            || tokenEl.ValueKind != JsonValueKind.String)
        {
            Log.Warning("Registro falhou: {Status} {Erro} {Mensagem}", resposta.Status, resposta.Erro, resposta.Mensagem);
            DefinirEstado(EstadoGestao.Offline, $"Registro falhou ({resposta.Status})", ConfigManager.LoadConfig().UltimaSituacao);
            return ProximoRecuo();
        }

        _token = tokenEl.GetString();
        if (!string.IsNullOrEmpty(_token)) KioskToken.Salvar(_token);
        _aplicadoNaSessao = false;
        _indiceRecuo = 0;
        if (resposta.Dados.TryGetProperty("cdKiosk", out var cd) && cd.ValueKind == JsonValueKind.Number) CdKiosk = cd.GetInt32();
        Enfileirar("REGISTRADO", $"Registrado no Gestão ({LerTexto(resposta.Dados, "deSituacao")})");
        Log.Information("Kiosk registrado no Gestão");
        return -1;
    }

    private int ProximoRecuo()
    {
        var segundos = Recuos[Math.Min(_indiceRecuo, Recuos.Length - 1)];
        _indiceRecuo++;
        return Math.Max(1, (int)Math.Round(segundos * (0.8 + Random.Shared.NextDouble() * 0.4)));
    }

    private void DefinirEstado(EstadoGestao estado, string mensagem, string situacao)
    {
        var mudou = Estado != estado || MensagemEstado != mensagem || Situacao != situacao;
        Estado = estado;
        MensagemEstado = mensagem;
        Situacao = situacao;
        if (mudou) EstadoAlterado?.Invoke();
    }

    private static string LerTexto(JsonElement el, string nome)
    {
        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(nome, out var p)
            && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? LerInteiro(JsonElement el, string nome)
    {
        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(nome, out var p)
            && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var v)
            ? v
            : null;
    }

    private static bool LerBool(JsonElement el, string nome, bool padrao)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(nome, out var p)) return padrao;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.TryGetInt32(out var n) && n != 0,
            _ => padrao
        };
    }

    private void AtualizarConfigTef(NpsConfig cfg)
    {
        Tef.DefinirImpressao(ImpressaoEfetiva(cfg));
        Tef.DefinirOrigensPermitidas(OrigensTefPermitidas(cfg), cfg.TefOrigensObrigatorio);
    }

    private static IEnumerable<string> OrigensTefPermitidas(NpsConfig cfg)
    {
        foreach (var url in new[]
                 {
                     cfg.NpsUrl,
                     cfg.GestaoApiUrl,
                     "https://backoffice-upmais-gestao-web.6yhsxt.easypanel.host",
                     "https://nps-caixas-frente-caixa-teste.6yhsxt.easypanel.host"
                 })
        {
            if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                yield return uri.GetLeftPart(UriPartial.Authority);
        }

        foreach (var origem in cfg.TefOrigensPermitidas ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(origem))
                yield return origem.Trim();
        }
    }

    private ConfiguracaoImpressao ImpressaoEfetiva(NpsConfig cfg)
    {
        var gestao = !string.IsNullOrWhiteSpace(cfg.GestaoApiUrl)
            && string.Equals(cfg.UltimaSituacao, "ATIVO", StringComparison.OrdinalIgnoreCase)
            && cfg.TefGestao != null;

        if (!gestao) return ConfiguracaoImpressao.CarregarIni();

        var t = cfg.TefGestao!;
        return new ConfiguracaoImpressao
        {
            Impressora = string.IsNullOrWhiteSpace(t.Impressora) ? "IMPRESSORATEF" : t.Impressora,
            ModoDriver = string.Equals(t.Modo, "DRIVER", StringComparison.OrdinalIgnoreCase),
            ViaCliente = t.ViaCliente,
            ViaEstabelecimento = t.ViaEstabelecimento,
            CortarPapel = t.CortarPapel
        };
    }

    private async Task AplicarSituacaoNaoAtivaAsync(string situacao, string nome)
    {
        _leitora.Definir(false);
        await _travaAplicacao.WaitAsync();
        try
        {
            var tipo = situacao == "BLOQUEADO" ? PaginasLocais.Bloqueado : PaginasLocais.Aguardando;
            var cfgAnterior = ConfigManager.LoadConfig();
            var mudou = !_aplicadoNaSessao || cfgAnterior.UltimaSituacao != situacao || cfgAnterior.TelaLocal != tipo;
            if (!mudou && Tela.EstaRodando) return;

            var cfg = ConfigManager.Update(c =>
            {
                c.UltimaSituacao = situacao;
                c.UltimaVersaoConfig = 0;
                c.TelaLocal = tipo;
                c.GestaoExibir = true;
            });
            _aplicadoNaSessao = true;

            Situacao = situacao;
            DescricaoCaixa = string.Empty;
            NomeTela = string.Empty;

            if (_tefIniciado) AtualizarConfigTef(cfg);

            if (Tela.EstaRodando)
            {
                var resposta = await IpcClient.NavigateLocalAsync(tipo, nome, cfg.KioskIdentificador);
                if (!resposta.Success) Log.Warning("NavigateLocal falhou: {Mensagem}", resposta.Message);
            }
            else
            {
                await Tela.IniciarAsync(cfg.RouterEnabled);
            }

            Enfileirar("CONFIG_APLICADA", $"Situação {situacao}");
        }
        finally
        {
            _travaAplicacao.Release();
        }
    }

    private async Task AplicarAsync(JsonElement dados, string situacao)
    {
        var nome = LerTexto(dados, "nmKiosk");
        if (string.IsNullOrEmpty(nome)) nome = Environment.MachineName;

        if (!string.Equals(situacao, "ATIVO", StringComparison.OrdinalIgnoreCase))
        {
            var pendente = !string.Equals(situacao, "BLOQUEADO", StringComparison.OrdinalIgnoreCase);
            DefinirEstado(pendente ? EstadoGestao.Aguardando : EstadoGestao.Bloqueado,
                pendente ? "Aguardando aprovação no Gestão" : "Kiosk bloqueado no Gestão",
                pendente ? "PENDENTE" : "BLOQUEADO");
            await AplicarSituacaoNaoAtivaAsync(pendente ? "PENDENTE" : "BLOQUEADO", nome);
            return;
        }

        DefinirEstado(EstadoGestao.Online, string.Empty, "ATIVO");

        var config = dados.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var versao = LerInteiro(config, "nuVersaoConfig") ?? 0;

        var atual = ConfigManager.LoadConfig();
        var mudouSituacao = !string.Equals(atual.UltimaSituacao, "ATIVO", StringComparison.OrdinalIgnoreCase);
        var mudouVersao = versao != atual.UltimaVersaoConfig;
        if (_aplicadoNaSessao && !mudouSituacao && !mudouVersao)
        {
            return;
        }

        await _travaAplicacao.WaitAsync();
        try
        {
            var codPdv = LerInteiro(config, "codPdv") ?? 0;
            var codEmp = LerInteiro(config, "codEmp") ?? 0;
            var dsPdv = LerTexto(config, "dsPdv");
            var nmEmpresa = LerTexto(config, "nmEmpresa");
            var urlTela = LerTexto(config, "url").Trim();
            var exibir = LerBool(config, "exibir", true);
            var nuMonitor = LerInteiro(config, "nuMonitor");
            var sempreVisivel = LerBool(config, "sempreVisivel", true);
            var roteador = LerBool(config, "roteador", false);
            var capturaLeitora = LerBool(config, "capturaLeitora", false);

            var tef = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("tef", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            var tefAtivo = LerBool(tef, "ativo", true);
            var tefImpressao = new TefImpressaoConfig
            {
                Impressora = LerTexto(tef, "impressora"),
                Modo = LerTexto(tef, "modo") is { Length: > 0 } m ? m.ToUpperInvariant() : "RAW",
                ViaCliente = LerBool(tef, "viaCliente", true),
                ViaEstabelecimento = LerBool(tef, "viaEstabelecimento", true),
                CortarPapel = LerBool(tef, "cortarPapel", true)
            };

            var contexto = new KioskContexto
            {
                CdKiosk = CdKiosk,
                NmKiosk = nome,
                CodPdv = codPdv,
                CodEmp = codEmp,
                DsPdv = dsPdv,
                NmEmpresa = nmEmpresa,
                Versao = _versaoApp
            };

            var semTela = string.IsNullOrEmpty(urlTela);
            var contextoMudou = !ContextoIgual(atual.KioskContexto, contexto);
            var urlMudou = !semTela && !string.Equals(atual.NpsUrl, urlTela, StringComparison.Ordinal);
            var monitorMudou = (atual.MonitorPolicy == "Primary") != (nuMonitor == null)
                || (nuMonitor != null && atual.MonitorIndex != nuMonitor);

            var cfg = ConfigManager.Update(x =>
            {
                x.UltimaSituacao = "ATIVO";
                x.UltimaVersaoConfig = versao;
                x.KioskContexto = contexto;
                x.GestaoExibir = exibir;
                x.TopMost = sempreVisivel;
                x.RouterEnabled = roteador;
                x.TefAtivo = tefAtivo;
                x.TefGestao = tefImpressao;
                x.CapturaLeitora = capturaLeitora;
                x.MonitorPolicy = nuMonitor == null ? "Primary" : "Explicit";
                if (nuMonitor != null)
                {
                    x.MonitorIndex = nuMonitor.Value;
                    x.MonitorDeviceId = string.Empty;
                }

                if (semTela)
                {
                    x.TelaLocal = PaginasLocais.SemTela;
                }
                else
                {
                    x.TelaLocal = string.Empty;
                    x.NpsUrl = urlTela;
                }
            });
            _aplicadoNaSessao = true;
            _leitora.Definir(capturaLeitora && codPdv > 0);

            DescricaoCaixa = string.IsNullOrEmpty(dsPdv) ? (codPdv > 0 ? $"Caixa {codPdv}" : string.Empty) : $"{dsPdv} ({codPdv})";
            NomeTela = LerTexto(config, "nmTela");

            await AplicarTefAsync(cfg);
            await AplicarTelaAsync(cfg, exibir, semTela, urlMudou, contextoMudou, monitorMudou, nuMonitor);

            Enfileirar("CONFIG_APLICADA", $"Versão {versao}");
            Log.Information("Configuração do Gestão aplicada (versão {Versao})", versao);
            EstadoAlterado?.Invoke();
        }
        finally
        {
            _travaAplicacao.Release();
        }
    }

    private static bool ContextoIgual(KioskContexto? a, KioskContexto b)
    {
        return a != null
            && a.CdKiosk == b.CdKiosk
            && a.NmKiosk == b.NmKiosk
            && a.CodPdv == b.CodPdv
            && a.CodEmp == b.CodEmp
            && a.DsPdv == b.DsPdv
            && a.NmEmpresa == b.NmEmpresa
            && a.Versao == b.Versao;
    }

    private async Task AplicarTefAsync(NpsConfig cfg)
    {
        try
        {
            AtualizarConfigTef(cfg);

            if (cfg.TefAtivo && !_tefIniciado)
            {
                await Tef.IniciarAsync();
                _tefIniciado = true;
            }
            else if (!cfg.TefAtivo && _tefIniciado && !Tef.Estado.Ocupado)
            {
                await Tef.PararAsync();
                _tefIniciado = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao aplicar a configuração do TEF");
            Enfileirar("ERRO_TEF", ex.Message);
        }
    }

    private async Task AplicarTelaAsync(NpsConfig cfg, bool exibir, bool semTela, bool urlMudou, bool contextoMudou, bool monitorMudou, int? nuMonitor)
    {
        if (!exibir)
        {
            if (Tela.EstaRodando || Tela.RoteadorRodando) await Tela.PararAsync();
            return;
        }

        if (!Tela.EstaRodando)
        {
            await Tela.IniciarAsync(cfg.RouterEnabled);
            return;
        }

        if (contextoMudou && cfg.KioskContexto != null)
        {
            var json = JsonSerializer.Serialize(cfg.KioskContexto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var r = await IpcClient.SetKioskContextAsync(json);
            if (!r.Success) Log.Warning("SetKioskContext falhou: {Mensagem}", r.Message);
        }

        var status = await IpcClient.GetStatusAsync();
        var dadosStatus = status.Success ? status.Data : new Dictionary<string, string>();
        dadosStatus.TryGetValue("local", out var localAtual);
        dadosStatus.TryGetValue("url", out var urlBrowser);

        if (semTela)
        {
            if (localAtual != PaginasLocais.SemTela)
            {
                await IpcClient.NavigateLocalAsync(PaginasLocais.SemTela, cfg.KioskContexto?.NmKiosk ?? Environment.MachineName, cfg.KioskIdentificador);
            }
        }
        else if (!string.IsNullOrEmpty(localAtual) || urlMudou || !string.Equals(urlBrowser, cfg.NpsUrl, StringComparison.Ordinal))
        {
            var r = await IpcClient.SetUrlAsync(cfg.NpsUrl);
            if (!r.Success) Log.Warning("SetUrl falhou: {Mensagem}", r.Message);
        }

        var indiceDesejado = nuMonitor ?? IndicePrimario();
        var indiceAtual = dadosStatus.TryGetValue("monitorIndex", out var mi) && int.TryParse(mi, out var miv) ? miv : -1;
        if (indiceDesejado != indiceAtual)
        {
            var r = await IpcClient.SetMonitorAsync(indiceDesejado);
            if (!r.Success) Log.Warning("SetMonitor falhou: {Mensagem}", r.Message);
        }

        if (dadosStatus.TryGetValue("topMost", out var tm) && bool.TryParse(tm, out var topMostAtual) && topMostAtual != cfg.TopMost)
        {
            await IpcClient.ToggleTopMostAsync();
        }

        var roteadorRodando = Tela.RoteadorRodando;
        if (cfg.RouterEnabled && (!roteadorRodando || monitorMudou))
        {
            Tela.IniciarRoteador();
        }
        else if (!cfg.RouterEnabled && roteadorRodando)
        {
            Tela.PararRoteador();
        }
    }

    private static int IndicePrimario()
    {
        var telas = Screen.AllScreens;
        var primaria = Screen.PrimaryScreen;
        var indice = primaria == null ? 0 : Array.FindIndex(telas, s => s.DeviceName == primaria.DeviceName);
        return indice < 0 ? 0 : indice;
    }

    private void AoTelaSairInesperadamente(string motivo)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var cfg = ConfigManager.LoadConfig();
                if (string.IsNullOrWhiteSpace(cfg.GestaoApiUrl) || !cfg.AutoRecover || !cfg.GestaoExibir) return;

                await Task.Delay(3000);
                if (Tela.EstaRodando) return;

                _ultimaReinicializacaoTela = DateTime.UtcNow;
                Log.Warning("Reiniciando a tela após encerramento inesperado ({Motivo})", motivo);
                await Tela.IniciarAsync(cfg.RouterEnabled);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha ao reiniciar a tela");
            }
        });
    }

    private void AoEventoTef(string evento, string detalhe)
    {
        var nome = evento.StartsWith("TEF_", StringComparison.OrdinalIgnoreCase) ? evento : "TEF_" + evento;
        Enfileirar(nome, detalhe);
    }

    private void AplicarSenhaLocal(string hash)
    {
        var novo = SenhaLocal.FormatoValido(hash) ? hash : string.Empty;
        var atual = ConfigManager.LoadConfig().SenhaLocalHash ?? string.Empty;
        if (string.Equals(novo, atual, StringComparison.Ordinal)) return;
        ConfigManager.Update(c => c.SenhaLocalHash = novo);
        SenhaLocal.Bloquear();
        Enfileirar("SENHA_LOCAL_ATUALIZADA", string.IsNullOrEmpty(novo) ? "Senha padrão" : "Senha definida pelo Gestão");
        Log.Information("Senha local do kiosk atualizada pelo Gestão");
    }

    public void Enfileirar(string evento, string detalhe)
    {
        try
        {
            var item = new JsonObject
            {
                ["deEvento"] = evento,
                ["deDetalhe"] = detalhe.Length > 500 ? detalhe[..500] : detalhe,
                ["em"] = DateTimeOffset.Now.ToString("o")
            };

            lock (_travaEventos)
            {
                _eventos.Add(item);
                while (_eventos.Count > MaxEventosFila) _eventos.RemoveAt(0);
            }
        }
        catch { }
    }

    private JsonArray RetirarEventos()
    {
        var array = new JsonArray();
        lock (_travaEventos)
        {
            var quantidade = Math.Min(MaxEventosPorSinal, _eventos.Count);
            for (var i = 0; i < quantidade; i++) array.Add(_eventos[i]);
            _eventos.RemoveRange(0, quantidade);
        }
        return array;
    }

    private void RestaurarEventos(JsonObject payload)
    {
        try
        {
            if (payload["eventos"] is not JsonArray enviados || enviados.Count == 0) return;
            var itens = enviados.OfType<JsonObject>().ToList();
            foreach (var i in itens) enviados.Remove(i);

            lock (_travaEventos)
            {
                _eventos.InsertRange(0, itens);
                while (_eventos.Count > MaxEventosFila) _eventos.RemoveAt(0);
            }
        }
        catch { }
    }

    private async Task<JsonObject> MontarSinalAsync(NpsConfig cfg)
    {
        var tela = await MontarTelaAsync();
        var estadoTef = Tef.Estado;

        var monitores = new JsonArray();
        var telas = Screen.AllScreens;
        for (var i = 0; i < telas.Length; i++)
        {
            monitores.Add(new JsonObject
            {
                ["indice"] = i,
                ["nome"] = telas[i].DeviceName,
                ["largura"] = telas[i].Bounds.Width,
                ["altura"] = telas[i].Bounds.Height,
                ["principal"] = telas[i].Primary
            });
        }

        if (DateTime.UtcNow - _impressorasEm > TimeSpan.FromSeconds(60))
        {
            try { _impressoras = ServicoTef.ListarImpressoras(); } catch (Exception ex) { Log.Warning(ex, "Falha ao listar impressoras"); }
            _impressorasEm = DateTime.UtcNow;
        }

        var impressoras = new JsonArray();
        foreach (var nome in _impressoras) impressoras.Add(nome);

        return new JsonObject
        {
            ["deVersaoApp"] = _versaoApp,
            ["nmComputador"] = Environment.MachineName,
            ["nuVersaoConfig"] = cfg.UltimaVersaoConfig,
            ["tela"] = tela,
            ["tef"] = new JsonObject
            {
                ["ativo"] = cfg.TefAtivo && _tefIniciado,
                ["porta"] = estadoTef.Porta,
                ["servidor"] = estadoTef.Servidor,
                ["webConnection"] = estadoTef.WebConnection,
                ["ocupado"] = estadoTef.Ocupado,
                ["erro"] = estadoTef.Erro ?? string.Empty
            },
            ["roteador"] = new JsonObject { ["ativo"] = Tela.RoteadorRodando },
            ["leitora"] = new JsonObject
            {
                ["ativa"] = _leitora.Ativa,
                ["pendentes"] = _leitora.Pendentes,
                ["entregues"] = _leitora.Entregues,
                ["descartadas"] = _leitora.Descartadas,
                ["ultimaEm"] = _leitora.UltimaEm?.ToString("yyyy-MM-ddTHH:mm:sszzz")
            },
            ["monitores"] = monitores,
            ["impressoras"] = impressoras,
            ["eventos"] = RetirarEventos()
        };
    }

    private JsonObject InformarCaixa()
    {
        var cfg = ConfigManager.LoadConfig();
        var ativo = !string.IsNullOrWhiteSpace(cfg.GestaoApiUrl)
            && string.Equals(cfg.UltimaSituacao, "ATIVO", StringComparison.OrdinalIgnoreCase);
        return new JsonObject
        {
            ["cdKiosk"] = CdKiosk,
            ["nmKiosk"] = NomeKiosk,
            ["situacao"] = string.IsNullOrEmpty(cfg.UltimaSituacao) ? "SEM_GESTAO" : cfg.UltimaSituacao,
            ["codPdv"] = ativo ? cfg.KioskContexto?.CodPdv ?? 0 : 0,
            ["capturaLeitora"] = _leitora.Ativa
        };
    }

    private static (int Status, JsonObject Corpo) RecusaAtestado(string codigo, string mensagem, int status = 409)
    {
        return (status, new JsonObject { ["erro"] = mensagem, ["codigo"] = codigo });
    }

    private async Task<(int Status, JsonObject Corpo)> AtestarAsync(string corpo)
    {
        string desafio;
        int codPdv;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(corpo) ? "{}" : corpo);
            desafio = LerTexto(doc.RootElement, "desafio").Trim();
            codPdv = LerInteiro(doc.RootElement, "codPdv") ?? 0;
        }
        catch (JsonException)
        {
            return RecusaAtestado("CORPO_INVALIDO", "Pedido de atestado inválido.", 400);
        }

        if (desafio.Length == 0 || desafio.Length > 128 || codPdv <= 0)
            return RecusaAtestado("CORPO_INVALIDO", "Pedido de atestado inválido.", 400);

        var cfg = ConfigManager.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.GestaoApiUrl) || string.IsNullOrEmpty(_token))
            return RecusaAtestado("KIOSK_SEM_GESTAO", "Este KioskMax não está ligado ao Gestão.");

        if (string.Equals(cfg.UltimaSituacao, "BLOQUEADO", StringComparison.OrdinalIgnoreCase))
            return RecusaAtestado("KIOSK_BLOQUEADO", "Este KioskMax está bloqueado no Gestão.");

        if (!string.Equals(cfg.UltimaSituacao, "ATIVO", StringComparison.OrdinalIgnoreCase))
            return RecusaAtestado("KIOSK_PENDENTE", "Este KioskMax ainda não foi aprovado no Gestão.");

        var codPdvKiosk = cfg.KioskContexto?.CodPdv ?? 0;
        if (codPdvKiosk != codPdv)
        {
            var recusa = RecusaAtestado("KIOSK_OUTRO_CAIXA", codPdvKiosk > 0
                ? $"Este computador é do caixa {codPdvKiosk}."
                : "Este KioskMax não está vinculado a um caixa.");
            recusa.Corpo["codPdvKiosk"] = codPdvKiosk;
            return recusa;
        }

        var resposta = await _cliente.AtestarAsync(desafio, codPdv, _cts.Token);

        if (resposta.Sucesso)
            return (200, new JsonObject { ["cdKiosk"] = CdKiosk, ["nmKiosk"] = NomeKiosk, ["codPdv"] = codPdv });

        if (resposta.PrecisaRecuar)
            return RecusaAtestado("KIOSK_SEM_REDE", "O KioskMax não conseguiu falar com o Gestão.", 503);

        Enfileirar("ATESTADO_RECUSADO", resposta.Mensagem);
        return RecusaAtestado("KIOSK_RECUSADO", string.IsNullOrEmpty(resposta.Mensagem) ? "O Gestão recusou o atestado." : resposta.Mensagem);
    }

    private async Task<JsonObject> MontarTelaAsync()
    {
        var tela = new JsonObject
        {
            ["exibindo"] = false,
            ["url"] = "",
            ["monitor"] = null,
            ["carregada"] = false,
            ["pagina"] = null
        };

        if (!Tela.EstaRodando) return tela;

        tela["exibindo"] = true;
        var resposta = await IpcClient.GetStatusAsync();
        if (!resposta.Success) return tela;

        var d = resposta.Data;
        d.TryGetValue("local", out var local);
        d.TryGetValue("url", out var url);
        d.TryGetValue("currentUrl", out var urlAtual);

        tela["url"] = string.IsNullOrEmpty(local) ? (string.IsNullOrEmpty(urlAtual) ? url ?? "" : urlAtual) : "";
        if (!string.IsNullOrEmpty(local)) tela["local"] = local;

        if (d.TryGetValue("monitorIndex", out var mi) && int.TryParse(mi, out var miv) && miv >= 0)
        {
            tela["monitor"] = miv;
        }

        tela["carregada"] = d.TryGetValue("carregada", out var car) && bool.TryParse(car, out var carv) && carv;

        if (d.TryGetValue("paginaJson", out var pagina) && !string.IsNullOrWhiteSpace(pagina))
        {
            try { tela["pagina"] = JsonNode.Parse(pagina); } catch { }
        }

        return tela;
    }

    private void RegistrarComandoExecutado(int cd, bool sucesso, string mensagem)
    {
        var limite = DateTime.UtcNow - RetencaoComandosExecutados;
        foreach (var antigo in _comandosExecutados.Where(x => x.Value.Em < limite).Select(x => x.Key).ToList())
            _comandosExecutados.Remove(antigo);

        _comandosExecutados[cd] = (DateTime.UtcNow, sucesso, mensagem);
    }

    private async Task ExecutarComandosAsync(JsonElement dados, CancellationToken ct)
    {
        if (!dados.TryGetProperty("comandos", out var comandos) || comandos.ValueKind != JsonValueKind.Array) return;

        foreach (var comando in comandos.EnumerateArray())
        {
            var cd = LerInteiro(comando, "cdComando");
            var nome = LerTexto(comando, "deComando").ToUpperInvariant();
            if (cd == null || string.IsNullOrEmpty(nome)) continue;

            if (_comandosExecutados.TryGetValue(cd.Value, out var anterior))
            {
                Log.Information("Comando {Comando} ({Cd}) reentregue; nao sera executado de novo", nome, cd);
                await _cliente.ResultadoComandoAsync(cd.Value, anterior.Sucesso, anterior.Mensagem, ct);
                continue;
            }

            Enfileirar("COMANDO_RECEBIDO", $"{nome} ({cd})");
            Log.Information("Comando recebido: {Comando} ({Cd})", nome, cd);

            bool sucesso;
            string mensagem;
            try
            {
                (sucesso, mensagem) = await ExecutarComandoAsync(nome);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha ao executar o comando {Comando}", nome);
                sucesso = false;
                mensagem = ex.Message;
            }

            if (!sucesso) Enfileirar("ERRO_COMANDO", $"{nome}: {mensagem}");

            RegistrarComandoExecutado(cd.Value, sucesso, mensagem);

            var resultado = await _cliente.ResultadoComandoAsync(cd.Value, sucesso, mensagem, ct);
            if (resultado.PrecisaRecuar)
            {
                await Task.Delay(1000, ct);
                resultado = await _cliente.ResultadoComandoAsync(cd.Value, sucesso, mensagem, ct);
            }

            if (!resultado.Sucesso)
            {
                Log.Warning("Resultado do comando {Cd} não aceito: {Status} {Erro}", cd, resultado.Status, resultado.Erro);
            }

            if (nome == "REINICIAR_AGENTE" && sucesso)
            {
                ReiniciarSolicitado?.Invoke();
                return;
            }
        }
    }

    private async Task<(bool, string)> ExecutarComandoAsync(string nome)
    {
        var cfg = ConfigManager.LoadConfig();

        switch (nome)
        {
            case "RECARREGAR":
            {
                if (!Tela.EstaRodando) return (false, "A tela não está em execução");
                var r = await IpcClient.ReloadAsync();
                return (r.Success, r.Success ? "Página recarregada" : r.Message);
            }

            case "REINICIAR_TELA":
            {
                await Tela.PararAsync(false);
                var ok = await Tela.IniciarAsync(false);
                return (ok, ok ? "Tela reiniciada" : "Falha ao iniciar a tela");
            }

            case "LIMPAR_CACHE":
            {
                await Tela.PararAsync(false);
                var apagou = await Task.Run(GerenciadorTela.ApagarCacheWebView);
                var ok = await Tela.IniciarAsync(false);
                if (!ok) return (false, "Cache tratado, mas falhou ao abrir a tela");
                return (apagou, apagou ? "Cache apagado e tela reiniciada" : "Não foi possível apagar todo o cache; tela reiniciada");
            }

            case "REINICIAR_ROTEADOR":
            {
                if (!cfg.RouterEnabled) return (false, "O roteador está desativado na configuração");
                var ok = await Task.Run(() => Tela.IniciarRoteador());
                return (ok, ok ? "Roteador reiniciado" : "Falha ao iniciar o roteador");
            }

            case "REINICIAR_TEF":
            {
                if (!cfg.TefAtivo || !_tefIniciado) return (false, "O TEF está desativado");
                if (Tef.Estado.Ocupado) return (false, "Há uma transação TEF em andamento");
                var ok = await Tef.ReiniciarAsync();
                return (ok, ok ? "TEF reiniciado" : "Não foi possível reiniciar o TEF");
            }

            case "IMPRIMIR_TESTE_TEF":
            {
                await Task.Run(() => Tef.ImprimirTeste());
                return (true, "Teste de impressão enviado");
            }

            case "REINICIAR_AGENTE":
                if (Tef.Estado.Ocupado) return (false, "Transação TEF em andamento; o agente não foi reiniciado");
                return (true, "Reiniciando o agente");

            default:
                return (false, $"Comando desconhecido: {nome}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { if (_tarefa != null) await Task.WhenAny(_tarefa, Task.Delay(2000)); } catch { }
        try { await Tef.PararAsync(); } catch { }
        try { await Tef.DisposeAsync(); } catch { }
        try { await _leitora.DisposeAsync(); } catch { }
        _cliente.Dispose();
    }
}
