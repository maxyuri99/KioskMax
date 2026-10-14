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
    private WebApplication? _app;

    public bool Executando => _app is not null;

    public ServidorTef(GerenciadorSitef gerenciador, ImpressoraComprovante impressora, Func<ConfiguracaoImpressao> config,
        string versao, Func<int> tempoLimite, Action<string> aoRegistrar, Action<string, string> aoEvento, Action<string, string> aoUltima)
    {
        _gerenciador = gerenciador;
        _impressora = impressora;
        _config = config;
        _versao = versao;
        _tempoLimite = tempoLimite;
        _aoRegistrar = aoRegistrar;
        _aoEvento = aoEvento;
        _aoUltima = aoUltima;
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

    private static void Cors(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Access-Control-Allow-Origin"] = "*";
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
                }
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
            var resp = await _gerenciador.ExecutarAsync(corpo, _tempoLimite());
            var obj = TextoDelphi.InterpretarObjeto(resp);

            if (obj is not null)
            {
                var retorno = TextoDelphi.ValorTexto(obj, "retorno");
                _aoEvento("TEF_RESPOSTA", "ident " + ident + " retorno " + (retorno == "" ? "erro" : retorno));
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
            int status = e.Situacao == SituacaoTef.Ocupado ? 409 : e.Situacao == SituacaoTef.NaoEnviada ? 503 : 504;

            if (e.Situacao != SituacaoTef.Ocupado)
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
}
