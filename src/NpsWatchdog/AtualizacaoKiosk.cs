using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using NpsShared.Configuration;
using NpsShared.Utilities;
using Serilog;

namespace NpsWatchdog;

public sealed class AtualizacaoKiosk
{
    private static readonly TimeSpan PrazoConferencia = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PrazoInstalacao = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ValidadeRecusa = TimeSpan.FromHours(24);
    private static readonly string[] ProcessosDoAgente = { "KioskController", "KioskBrowser", "WindowRouter" };
    private const int PortaTef = 8069;

    private readonly ClienteGestaoServico _gestao;
    private readonly Baixador _baixador;
    private string _ultimoMotivo = string.Empty;
    private string _ultimoErro = string.Empty;
    private DateTime _erroEm = DateTime.MinValue;

    public AtualizacaoKiosk(ClienteGestaoServico gestao, Baixador baixador)
    {
        _gestao = gestao;
        _baixador = baixador;
    }

    public static string VersaoAtual
    {
        get
        {
            var info = typeof(AtualizacaoKiosk).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (info ?? "0.0.0").Split('+')[0];
        }
    }

    private static string ArquivoEstado => Path.Combine(Baixador.PastaProtegida, "atualizacao.json");
    private static string ArquivoRecusas => Path.Combine(Baixador.PastaProtegida, "recusadas.json");
    private static string NomeInstalador(string versao) => $"kioskmax-{versao}.exe";
    private static string ExecutavelAgente => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Controller", "KioskController.exe"));

    private sealed class Estado
    {
        public string De { get; set; } = string.Empty;
        public string Para { get; set; } = string.Empty;
        public bool Revertendo { get; set; }
        public DateTimeOffset IniciadaEm { get; set; }
    }

    public async Task ExecutarAsync(CancellationToken ct)
    {
        if (!_gestao.Configurado(out var motivo))
        {
            Anotar(motivo);
            return;
        }

        if (LerEstado() is { } pendente)
        {
            await ConcluirAsync(pendente, ct);
            return;
        }

        var resposta = await _gestao.ConsultarAtualizacaoAsync(ct);
        if (resposta.FalhaRede || !resposta.Sucesso || !resposta.TemDados)
        {
            Anotar($"Gestão não respondeu à consulta da atualização ({resposta.Status}): {resposta.Mensagem}");
            return;
        }

        var ordem = resposta.Dados;
        var versao = LeituraJson.Texto(ordem, "versao");
        if (!LeituraJson.Booleano(ordem, "atualizar") || string.IsNullOrEmpty(versao) || versao == VersaoAtual)
        {
            Anotar($"KioskMax {VersaoAtual} sem atualização pendente");
            return;
        }

        var imediato = LeituraJson.Booleano(ordem, "imediato");
        if (!imediato && Recusada(versao))
        {
            Anotar($"KioskMax {versao} recusado nas últimas 24 h; aguardando nova publicação ou o pedido do TI");
            return;
        }

        try
        {
            var instalador = await _baixador.BaixarAsync(NomeInstalador(versao), LeituraJson.Texto(ordem, "url"),
                LeituraJson.Texto(ordem, "sha256").ToLowerInvariant(), ct);

            if (ordem.TryGetProperty("anterior", out var anterior) && LeituraJson.Texto(anterior, "versao") == VersaoAtual)
            {
                try
                {
                    await _baixador.BaixarAsync(NomeInstalador(VersaoAtual), LeituraJson.Texto(anterior, "url"),
                        LeituraJson.Texto(anterior, "sha256").ToLowerInvariant(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log.Warning("Instalador de volta {Versao} não baixado: {Erro}", VersaoAtual, ex.Message);
                }
            }

            var inicio = LeituraJson.Inteiro(ordem, "horaInicio", 22);
            var fim = LeituraJson.Inteiro(ordem, "horaFim", 6);
            if (!imediato && !DentroDaJanela(DateTime.Now.Hour, inicio, fim))
            {
                Anotar($"KioskMax {versao} baixado; instala entre {inicio}h e {fim}h");
                return;
            }

            if (ArquivoTefOcupado.Ocupado())
            {
                Anotar($"Atualização para {versao} adiada: TEF em andamento");
                return;
            }

            await InstalarAsync(VersaoAtual, versao, instalador, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao preparar a atualização para {Versao}", versao);
            var assinatura = versao + "|" + ex.Message;
            if (assinatura != _ultimoErro || DateTime.UtcNow - _erroEm > TimeSpan.FromHours(1))
            {
                _ultimoErro = assinatura;
                _erroEm = DateTime.UtcNow;
                await InformarAsync("ERRO", VersaoAtual, versao, ex.Message, ct);
            }
        }
    }

    public static bool DentroDaJanela(int hora, int inicio, int fim) =>
        inicio < fim ? hora >= inicio && hora < fim : hora >= inicio || hora < fim;

    private async Task InstalarAsync(string de, string para, string instalador, bool revertendo, CancellationToken ct)
    {
        Log.Information("{Acao} o KioskMax de {De} para {Para}", revertendo ? "Revertendo" : "Atualizando", de, para);
        if (!revertendo) await InformarAsync("INICIADA", de, para, string.Empty, ct);

        GravarEstado(new Estado { De = de, Para = para, Revertendo = revertendo, IniciadaEm = DateTimeOffset.Now });
        SessaoUsuario.Encerrar(ProcessosDoAgente);

        var log = Path.Combine(Baixador.PastaProtegida, $"instalacao-{para}.log");
        var inicio = new ProcessStartInfo
        {
            FileName = instalador,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Baixador.PastaProtegida
        };
        foreach (var argumento in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/LOG=" + log })
            inicio.ArgumentList.Add(argumento);

        using var processo = Process.Start(inicio) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador do KioskMax");
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(PrazoInstalacao);
        await processo.WaitForExitAsync(prazo.Token);
        if (processo.ExitCode != 0)
            throw new InvalidOperationException($"O instalador do KioskMax {para} terminou com código {processo.ExitCode}");
    }

    private async Task ConcluirAsync(Estado estado, CancellationToken ct)
    {
        var atual = VersaoAtual;
        if (atual != estado.Para)
        {
            if (DateTimeOffset.Now - estado.IniciadaEm < PrazoInstalacao) return;
            Log.Warning("A instalação do KioskMax {Para} não terminou; continua a {Atual}", estado.Para, atual);
            ApagarEstado();
            if (!estado.Revertendo)
            {
                Recusar(estado.Para);
                await InformarAsync("ERRO", estado.De, estado.Para, "A instalação não terminou; o kiosk continua na versão anterior.", ct);
            }
            AbrirAgente();
            return;
        }

        var (saudavel, problema) = await ConferirAsync(ct);
        if (estado.Revertendo || saudavel)
        {
            ApagarEstado();
            if (!estado.Revertendo)
            {
                Log.Information("KioskMax atualizado de {De} para {Para}", estado.De, estado.Para);
                await InformarAsync("CONCLUIDA", estado.De, estado.Para, string.Empty, ct);
                Baixador.Limpar("kioskmax-*", new[] { NomeInstalador(estado.Para), NomeInstalador(estado.De) });
            }
            return;
        }

        Log.Error("KioskMax {Para} não ficou saudável: {Problema}", estado.Para, problema);
        Recusar(estado.Para);
        if (!Baixador.Existe(NomeInstalador(estado.De)))
        {
            ApagarEstado();
            await InformarAsync("ERRO", estado.De, estado.Para, problema + " Sem instalador da versão anterior para voltar.", ct);
            return;
        }

        await InformarAsync("REVERTIDA", estado.Para, estado.De, problema, ct);
        await InstalarAsync(estado.Para, estado.De, Path.Combine(Baixador.PastaProtegida, NomeInstalador(estado.De)), true, ct);
    }

    private static async Task<(bool, string)> ConferirAsync(CancellationToken ct)
    {
        var sessao = SessaoUsuario.SessaoAtiva();
        if (sessao == null) return (true, string.Empty);

        AbrirAgente();
        var tefAtivo = ConfigManager.LoadConfig().TefAtivo;
        var limite = DateTime.UtcNow + PrazoConferencia;
        var problema = string.Empty;
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            var agente = SessaoUsuario.Rodando("KioskController", sessao.Value);
            var tef = !tefAtivo || TefEscutando();
            if (agente && tef)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (SessaoUsuario.Rodando("KioskController", sessao.Value)) return (true, string.Empty);
            }
            problema = !agente ? "O KioskController não ficou aberto na sessão do operador." : $"O TEF não abriu a porta {PortaTef}.";
            if (!agente) AbrirAgente();
        }
        return (false, problema);
    }

    private static bool TefEscutando() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ponto => ponto.Port == PortaTef);

    private static void AbrirAgente()
    {
        try
        {
            var sessao = SessaoUsuario.SessaoAtiva();
            if (sessao == null || SessaoUsuario.Rodando("KioskController", sessao.Value)) return;
            if (File.Exists(ExecutavelAgente)) SessaoUsuario.Abrir(ExecutavelAgente);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao abrir o KioskController na sessão do operador");
        }
    }

    private async Task InformarAsync(string etapa, string de, string para, string mensagem, CancellationToken ct)
    {
        var texto = mensagem.Length > 400 ? mensagem[..400] : mensagem;
        var resposta = await _gestao.InformarAtualizacaoAsync(new { etapa, versaoAnterior = de, versao = para, mensagem = texto }, ct);
        if (!resposta.Sucesso)
            Log.Warning("O Gestão não registrou a etapa {Etapa} da atualização ({Status}): {Mensagem}", etapa, resposta.Status, resposta.Mensagem);
    }

    private static Estado? LerEstado()
    {
        try
        {
            return File.Exists(ArquivoEstado) ? JsonSerializer.Deserialize<Estado>(File.ReadAllText(ArquivoEstado)) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Estado da atualização ilegível; descartado");
            ApagarEstado();
            return null;
        }
    }

    private static void GravarEstado(Estado estado) => File.WriteAllText(ArquivoEstado, JsonSerializer.Serialize(estado));

    private static void ApagarEstado()
    {
        try
        {
            if (File.Exists(ArquivoEstado)) File.Delete(ArquivoEstado);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao apagar o estado da atualização");
        }
    }

    private static Dictionary<string, DateTimeOffset> LerRecusas()
    {
        try
        {
            return File.Exists(ArquivoRecusas)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(ArquivoRecusas)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private static bool Recusada(string versao) =>
        LerRecusas().TryGetValue(versao, out var quando) && DateTimeOffset.Now - quando < ValidadeRecusa;

    private static void Recusar(string versao)
    {
        try
        {
            var recusas = LerRecusas();
            recusas[versao] = DateTimeOffset.Now;
            File.WriteAllText(ArquivoRecusas, JsonSerializer.Serialize(recusas));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao registrar a versão recusada {Versao}", versao);
        }
    }

    private void Anotar(string motivo)
    {
        if (motivo == _ultimoMotivo) return;
        _ultimoMotivo = motivo;
        Log.Information("{Motivo}", motivo);
    }
}
