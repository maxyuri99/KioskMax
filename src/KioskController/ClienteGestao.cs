using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace KioskController;

public sealed class RespostaGestao
{
    public bool FalhaRede { get; init; }
    public int Status { get; init; }
    public bool Sucesso { get; init; }
    public string Mensagem { get; init; } = string.Empty;
    public string Erro { get; init; } = string.Empty;
    public JsonElement Dados { get; init; }

    public bool TemDados => Dados.ValueKind == JsonValueKind.Object;
    public bool PrecisaRecuar => FalhaRede || Status >= 500 || Status == 429;
}

public sealed class ClienteGestao : IDisposable
{
    private readonly HttpClient _http;

    public string UrlBase { get; set; } = string.Empty;
    public string Identificador { get; set; } = string.Empty;
    public string? Token { get; set; }

    public ClienteGestao()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public Task<RespostaGestao> RegistrarAsync(string nmComputador, string versaoApp, CancellationToken ct = default)
    {
        var corpo = new JsonObject
        {
            ["deIdentificador"] = Identificador,
            ["nmComputador"] = nmComputador,
            ["deVersaoApp"] = versaoApp
        };
        return PostarAsync("/kiosk/dispositivo/registrar", corpo, false, ct);
    }

    public Task<RespostaGestao> SinalAsync(JsonObject payload, CancellationToken ct = default)
    {
        return PostarAsync("/kiosk/dispositivo/sinal", payload, true, ct);
    }

    public Task<RespostaGestao> ResultadoComandoAsync(int cdComando, bool sucesso, string mensagem, CancellationToken ct = default)
    {
        var corpo = new JsonObject
        {
            ["sucesso"] = sucesso,
            ["mensagem"] = mensagem
        };
        return PostarAsync($"/kiosk/dispositivo/comandos/{cdComando}/resultado", corpo, true, ct);
    }

    private async Task<RespostaGestao> PostarAsync(string caminho, JsonNode corpo, bool autenticado, CancellationToken ct)
    {
        try
        {
            var url = UrlBase.Trim().TrimEnd('/') + caminho;
            using var requisicao = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json")
            };

            if (autenticado)
            {
                requisicao.Headers.TryAddWithoutValidation("X-Kiosk-Identificador", Identificador);
                if (!string.IsNullOrEmpty(Token))
                {
                    requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                }
            }

            using var resposta = await _http.SendAsync(requisicao, ct);
            var texto = await resposta.Content.ReadAsStringAsync(ct);
            return Interpretar((int)resposta.StatusCode, texto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning("Falha de comunicação com o Gestão ({Caminho}): {Mensagem}", caminho, ex.Message);
            return new RespostaGestao { FalhaRede = true, Mensagem = ex.Message };
        }
    }

    private static RespostaGestao Interpretar(int status, string texto)
    {
        try
        {
            using var doc = JsonDocument.Parse(texto);
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                return new RespostaGestao { Status = status };
            }

            var sucesso = raiz.TryGetProperty("sucesso", out var s) && s.ValueKind == JsonValueKind.True;
            var mensagem = raiz.TryGetProperty("mensagem", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            var erro = raiz.TryGetProperty("erro", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            var dados = raiz.TryGetProperty("dados", out var d) ? d.Clone() : default;

            return new RespostaGestao
            {
                Status = status,
                Sucesso = sucesso && status is >= 200 and < 300,
                Mensagem = mensagem,
                Erro = erro,
                Dados = dados
            };
        }
        catch (JsonException)
        {
            return new RespostaGestao { Status = status, Mensagem = texto.Length > 200 ? texto[..200] : texto };
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
