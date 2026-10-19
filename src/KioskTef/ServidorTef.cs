using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskTef;

internal sealed class ServidorTef
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly GerenciadorSitef _gerenciador;
    private readonly ImpressoraComprovante _impressora;
    private readonly Func<ConfiguracaoImpressao> _config;
    private readonly string _versao;
    private readonly Func<int> _tempoLimite;
    private readonly Action<string> _aoRegistrar;
    private readonly Action<string, string> _aoEvento;
    private readonly Action<string, string> _aoUltima;
    private readonly Func<IReadOnlyList<string>> _origensPermitidas;
    private readonly Func<bool> _origensObrigatorio;
    private readonly Func<Func<string, Task<(int Status, JsonObject Corpo)>>?> _atestador;
    private readonly Func<Func<JsonObject>?> _informarCaixa;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _origensAvisadas = new();
    private WebApplication? _app;
    private int _avisoSemListaEmitido;

    public bool Executando => _app is not null;

    public ServidorTef(GerenciadorSitef gerenciador, ImpressoraComprovante impressora, Func<ConfiguracaoImpressao> config,
        string versao, Func<int> tempoLimite, Action<string> aoRegistrar, Action<string, string> aoEvento, Action<string, string> aoUltima,
        Func<IReadOnlyList<string>> origensPermitidas, Func<bool> origensObrigatorio,
        Func<Func<string, Task<(int Status, JsonObject Corpo)>>?> atestador, Func<Func<JsonObject>?> informarCaixa)
    {
        _gerenciador = gerenciador;
        _impressora = impressora;
        _config = config;
        _versao = versao;
        _tempoLimite = tempoLimite;
        _aoRegistrar = aoRegistrar;
        _aoEvento = aoEvento;
        _aoUltima = aoUltima;
        _origensPermitidas = origensPermitidas;
        _origensObrigatorio = origensObrigatorio;
        _atestador = atestador;
        _informarCaixa = informarCaixa;
    }

    private sealed class SemLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public async Task IniciarAsync(int porta)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, SemLifetime>();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(15);
            o.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
            o.Limits.MinRequestBodyDataRate = null;
            o.Limits.MaxRequestBodySize = TamanhoMaximoCorpo;
            o.ListenLocalhost(porta);
        });

        var app = builder.Build();
        app.Run(Tratar);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            try { await app.DisposeAsync(); } catch { }
            throw;
        }

        _app = app;
    }

    public async Task PararAsync()
    {
        var app = _app;
        _app = null;

        if (app is null)
            return;

        try { await app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { await app.DisposeAsync(); } catch { }
    }

    private const long TamanhoMaximoCorpo = 1024 * 1024;
    private const int LinhasMaximasCupom = 2000;

    private static string NormalizarOrigem(string origem) => origem.Trim().TrimEnd('/').ToLowerInvariant();

    private static bool HostEhLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    private static bool OrigemEhLocal(string origem)
    {
        if (!Uri.TryCreate(origem, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return HostEhLoopback(uri.Host) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CorrespondeEntrada(string origem, string entrada)
    {
        var e = NormalizarOrigem(entrada);

        if (!e.Contains('*'))
            return origem == e;

        var separador = e.IndexOf("://", StringComparison.Ordinal);
        if (separador < 0)
            return false;

        var esquema = e[..(separador + 3)];
        var restoEntrada = e[(separador + 3)..];
        if (!restoEntrada.StartsWith("*.", StringComparison.Ordinal) || restoEntrada.IndexOf('*', 1) >= 0)
            return false;

        if (!origem.StartsWith(esquema, StringComparison.Ordinal))
            return false;

        var hostOrigem = origem[esquema.Length..];
        var sufixo = restoEntrada[1..];

        if (!sufixo.Contains(':'))
        {
            var porta = hostOrigem.LastIndexOf(':');
            if (porta >= 0 && !hostOrigem.EndsWith(']'))
                hostOrigem = hostOrigem[..porta];
        }

        return hostOrigem.Length > sufixo.Length && hostOrigem.EndsWith(sufixo, StringComparison.Ordinal);
    }

    private bool OrigemPermitida(string origem, IReadOnlyList<string> lista)
    {
        var o = NormalizarOrigem(origem);

        if (OrigemEhLocal(o))
            return true;

        foreach (var entrada in lista)
        {
            if (!string.IsNullOrWhiteSpace(entrada) && CorrespondeEntrada(o, entrada))
                return true;
        }

        return false;
    }

    private static bool OrigemLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is null || System.Net.IPAddress.IsLoopback(ip);
    }

    private static bool HostValido(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        return string.IsNullOrEmpty(host) || HostEhLoopback(host.ToLowerInvariant());
    }

    private bool ValidarOrigem(HttpContext ctx)
    {
        if (!HostValido(ctx))
            return false;

        var origem = ctx.Request.Headers["Origin"].ToString();
        var lista = _origensPermitidas();

        if (string.IsNullOrEmpty(origem))
            return OrigemLoopback(ctx);

        if (lista.Count == 0)
        {
            if (Interlocked.Exchange(ref _avisoSemListaEmitido, 1) == 0)
                _aoEvento("TEF_SEGURANCA", "Nenhuma origem permitida configurada; aceitando qualquer origem como antes");

            return true;
        }

        if (OrigemPermitida(origem, lista))
            return true;

        if (_origensObrigatorio())
            return false;

        if (_origensAvisadas.Count < 50 && _origensAvisadas.TryAdd(NormalizarOrigem(origem), 0))
            _aoEvento("TEF_SEGURANCA", "Origem fora da lista aceita em modo aviso: " + NormalizarOrigem(origem));

        return true;
    }

    private static void Cors(HttpContext ctx)
    {
        var origem = ctx.Request.Headers["Origin"].ToString();
        var h = ctx.Response.Headers;
        h["Vary"] = "Origin";

        if (string.IsNullOrEmpty(origem))
            return;

        h["Access-Control-Allow-Origin"] = origem;
        h["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

        var pedidos = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
        h["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(pedidos) ? "Content-Type" : pedidos;

        if (string.Equals(ctx.Request.Headers["Access-Control-Request-Private-Network"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
            h["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task Responder(HttpContext ctx, int status, string json)
    {
        var bytes = Utf8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private static Task Responder(HttpContext ctx, int status, JsonNode obj)
    {
        return Responder(ctx, status, obj.ToJsonString(TextoDelphi.OpcoesJson));
    }

    private static Task ResponderErro(HttpContext ctx, int status, string mensagem, string? situacao = null)
    {
        var obj = new JsonObject { ["erro"] = mensagem };

        if (situacao is not null)
            obj["situacao"] = situacao;

        return Responder(ctx, status, obj);
    }

    private async Task Tratar(HttpContext ctx)
    {
        try
        {
            if (!ValidarOrigem(ctx))
            {
                var recusada = ctx.Request.Headers["Origin"].ToString();
                _aoEvento("TEF_SEGURANCA", "Origem recusada: " + (recusada == "" ? "(sem origem)" : recusada));
                ctx.Response.Headers["Vary"] = "Origin";
                await ResponderErro(ctx, 403, "Origem não permitida.");
                return;
            }

            Cors(ctx);

            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            var caminho = (ctx.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();

            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                switch (caminho)
                {
                    case "/tef/teste":
                        await Teste(ctx);
                        return;
                    case "/tef/pagar":
                        await Pagar(ctx);
                        return;
                    case "/tef/consultar":
                        await Consultar(ctx);
                        return;
                    case "/tef/imprimir":
                        await Imprimir(ctx);
                        return;
                    case "/impressao/cupom":
                        await ImprimirCupom(ctx);
                        return;
                    case "/kiosk/atestar":
                        await Atestar(ctx);
                        return;
                }
            }

            if (HttpMethods.IsGet(ctx.Request.Method) && caminho == "/kiosk/caixa")
            {
                var informar = _informarCaixa();
                if (informar is null)
                    await Responder(ctx, 409, new JsonObject { ["erro"] = "Este KioskMax não está ligado ao Gestão.", ["codigo"] = "KIOSK_SEM_GESTAO" });
                else
                    await Responder(ctx, 200, informar());
                return;
            }

            await ResponderErro(ctx, 404, "Rota não encontrada.");
        }
        catch (Exception e)
        {
            try
            {
                if (!ctx.Response.HasStarted)
                    await ResponderErro(ctx, 500, e.Message);
            }
            catch
            {
            }
        }
    }

    private static async Task<string> LerCorpo(HttpContext ctx)
    {
        using var leitor = new StreamReader(ctx.Request.Body, Utf8);
        return await leitor.ReadToEndAsync();
    }

    private async Task Atestar(HttpContext ctx)
    {
        var atestador = _atestador();

        if (atestador is null)
        {
            await Responder(ctx, 409, new JsonObject { ["erro"] = "Este KioskMax não está ligado ao Gestão.", ["codigo"] = "KIOSK_SEM_GESTAO" });
            return;
        }

        var corpo = await LerCorpo(ctx);
        var (status, resposta) = await atestador(corpo);
        await Responder(ctx, status, resposta);
    }

    private Task Teste(HttpContext ctx)
    {
        return Responder(ctx, 200, new JsonObject { ["Retorno"] = "Sucesso", ["versao"] = _versao });
    }

    private async Task Pagar(HttpContext ctx)
    {
        var corpo = await LerCorpo(ctx);
        var req = TextoDelphi.InterpretarObjeto(corpo);
        var ident = TextoDelphi.ValorTexto(req, "identificacao");

        _aoEvento("TEF_PAGAR", "ident " + ident + " oper " + TextoDelphi.ValorTexto(req, "operacao") + " valor " + TextoDelphi.ValorTexto(req, "valorTransacao"));

        try
        {
            var resultado = await _gerenciador.ExecutarAsync(corpo, _tempoLimite());
            var resp = resultado.Texto;
            var obj = TextoDelphi.InterpretarObjeto(resp);

            if (obj is not null)
            {
                var retorno = TextoDelphi.ValorTexto(obj, "retorno");
                _aoEvento(resultado.Repetida ? "TEF_REPETIDA" : "TEF_RESPOSTA", "ident " + ident + " retorno " + (retorno == "" ? "erro" : retorno));
                _aoUltima(ident, SituacaoTef.Concluida);
                await Responder(ctx, 200, resp);
            }
            else
            {
                _aoUltima(ident, SituacaoTef.Indefinida);
                _aoEvento("TEF_ERRO", SituacaoTef.Indefinida + ": formato desconhecido");
                await ResponderErro(ctx, 502, "O SiTef respondeu em um formato desconhecido: " + resp, SituacaoTef.Indefinida);
            }
        }
        catch (ExcecaoTef e)
        {
            var recusada = e.Situacao is SituacaoTef.Ocupado or SituacaoTef.EmAndamento or SituacaoTef.JaEnviada;
            int status = recusada ? 409 : e.Situacao == SituacaoTef.NaoEnviada ? 503 : 504;

            if (!recusada)
                _aoUltima(ident, e.Situacao);

            _aoEvento("TEF_ERRO", e.Situacao + ": " + e.Message);
            await ResponderErro(ctx, status, e.Message, e.Situacao);
        }
        catch (Exception e)
        {
            _aoRegistrar(e.Message);
            _aoUltima(ident, SituacaoTef.Indefinida);
            _aoEvento("TEF_ERRO", SituacaoTef.Indefinida + ": " + e.Message);
            await ResponderErro(ctx, 500, e.Message, SituacaoTef.Indefinida);
        }
    }

    private async Task Consultar(HttpContext ctx)
    {
        var corpo = await LerCorpo(ctx);
        var req = TextoDelphi.InterpretarObjeto(corpo);
        var ident = TextoDelphi.ValorTexto(req, "identificacao");
        var oper = TextoDelphi.ValorTexto(req, "operacao");

        if (ident == "")
        {
            await ResponderErro(ctx, 400, "Informe a identificação da transação.");
            return;
        }

        JsonObject resultado;

        try
        {
            resultado = _gerenciador.Consultar(ident, oper);
        }
        catch (Exception e)
        {
            await ResponderErro(ctx, 500, e.Message);
            return;
        }

        await Responder(ctx, 200, resultado);
    }

    private async Task Imprimir(HttpContext ctx)
    {
        var corpo = await LerCorpo(ctx);
        var req = TextoDelphi.InterpretarObjeto(corpo);
        var erro = "";

        if (req is null || req["comprovantes"] is not JsonArray comprovantes)
            erro = "Nenhum comprovante foi recebido para impressão.";
        else
        {
            try
            {
                await Task.Run(() => _impressora.ImprimirComprovantes(comprovantes));
                var msg = "Comprovante TEF enviado para a impressora " + _config().Impressora + ".";
                _aoRegistrar(msg);
                _aoEvento("TEF_IMPRESSAO", msg);
            }
            catch (Exception e)
            {
                erro = e.Message;
            }
        }

        if (erro == "")
            await Responder(ctx, 200, new JsonObject { ["ok"] = true });
        else
        {
            var msg = "Erro ao imprimir comprovante TEF: " + erro;
            _aoRegistrar(msg);
            _aoEvento("TEF_IMPRESSAO", msg);
            await ResponderErro(ctx, 500, erro);
        }
    }

    private async Task ImprimirCupom(HttpContext ctx)
    {
        var corpo = await LerCorpo(ctx);
        var req = TextoDelphi.InterpretarObjeto(corpo);
        var erro = "";
        var linhas = new List<string>();
        byte[]? qrCode = null;

        if (req is null || req["linhas"] is not JsonArray vetor || vetor.Count == 0 || vetor.Count > LinhasMaximasCupom)
            erro = "Nenhum cupom foi recebido para impressão.";
        else
        {
            foreach (var item in vetor)
                linhas.Add(TextoDelphi.ValorBruto(item));

            var png = req["qrCodePng"] is JsonValue v ? TextoDelphi.ValorBruto(v) : "";
            if (png != "")
            {
                try
                {
                    qrCode = Convert.FromBase64String(png);
                }
                catch (FormatException)
                {
                    erro = "A imagem do QR Code do cupom é inválida.";
                }
            }
        }

        if (erro == "")
        {
            try
            {
                await Task.Run(() => _impressora.ImprimirCupom(linhas, qrCode));
                var msg = "Cupom enviado para a impressora " + _config().Impressora + ".";
                _aoRegistrar(msg);
                _aoEvento("IMPRESSAO_CUPOM", msg);
            }
            catch (Exception e)
            {
                erro = e.Message;
            }
        }

        if (erro == "")
            await Responder(ctx, 200, new JsonObject { ["ok"] = true });
        else
        {
            var msg = "Erro ao imprimir cupom: " + erro;
            _aoRegistrar(msg);
            _aoEvento("IMPRESSAO_CUPOM", msg);
            await ResponderErro(ctx, 500, erro);
        }
    }
}
