using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NpsShared.Configuration;
using NpsShared.Utilities;

namespace NpsWatchdog;

public sealed record RespostaServico(bool FalhaRede, int Status, bool Sucesso, string Mensagem, JsonElement Dados, string Erro = "")
{
    public bool TemDados => Dados.ValueKind == JsonValueKind.Object;
}

public sealed record ListaPrecos(int Status, string Etag, string Texto, int Linhas, string Mensagem, string Erro);

public sealed class ClienteGestaoServico : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _lista = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(3) };

    public bool Configurado(out string motivo)
    {
        var cfg = ConfigManager.LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.GestaoApiUrl))
        {
            motivo = "KioskMax sem Gestão configurado";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cfg.KioskIdentificador) || string.IsNullOrEmpty(KioskToken.Ler()))
        {
            motivo = "KioskMax ainda não registrado no Gestão";
            return false;
        }
        motivo = string.Empty;
        return true;
    }

    public Task<RespostaServico> ConsultarRemotoAsync(CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "/kiosk/dispositivo/remoto", null, ct);

    public Task<RespostaServico> InformarRemotoAsync(object corpo, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Post, "/kiosk/dispositivo/remoto", corpo, ct);

    public Task<RespostaServico> ConsultarAtualizacaoAsync(CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "/kiosk/dispositivo/atualizacao", null, ct);

    public Task<RespostaServico> InformarAtualizacaoAsync(object corpo, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Post, "/kiosk/dispositivo/atualizacao", corpo, ct);

    public Task<RespostaServico> ConsultarCamerasAsync(CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "/kiosk/dispositivo/cameras", null, ct);

    public Task<RespostaServico> EnviarContagensAsync(int cdCamera, object corpo, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Post, $"/kiosk/dispositivo/cameras/{cdCamera}/contagens", corpo, ct);

    public Task<RespostaServico> ConsultarPrecosAsync(CancellationToken ct) =>
        EnviarAsync(HttpMethod.Get, "/kiosk/dispositivo/precos", null, ct);

    public Task<RespostaServico> InformarPrecosAsync(int codEmp, object corpo, CancellationToken ct) =>
        EnviarAsync(HttpMethod.Post, $"/kiosk/dispositivo/precos/{codEmp}/resultado", corpo, ct);

    public async Task<ListaPrecos> BaixarListaPrecosAsync(string etag, CancellationToken ct)
    {
        var cfg = ConfigManager.LoadConfig();
        var token = KioskToken.Ler();
        var baseUrl = (cfg.GestaoApiUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token))
            return new ListaPrecos(0, string.Empty, string.Empty, -1, "Sem Gestão configurado", string.Empty);

        try
        {
            using var requisicao = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/kiosk/dispositivo/precos/lista");
            requisicao.Headers.Add("X-Kiosk-Identificador", cfg.KioskIdentificador);
            requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(etag)) requisicao.Headers.TryAddWithoutValidation("If-None-Match", "\"" + etag + "\"");

            using var resposta = await _lista.SendAsync(requisicao, ct);
            var status = (int)resposta.StatusCode;
            var novaEtag = (resposta.Headers.ETag?.Tag ?? string.Empty).Trim('"');
            var linhas = resposta.Headers.TryGetValues("X-Quantidade-Linhas", out var valores) && int.TryParse(valores.FirstOrDefault(), out var n) ? n : -1;
            var texto = await resposta.Content.ReadAsStringAsync(ct);
            if (status == 200 && resposta.Content.Headers.ContentType?.MediaType == "text/plain")
                return new ListaPrecos(status, novaEtag, texto, linhas, string.Empty, string.Empty);

            var mensagem = string.Empty;
            var erro = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto);
                var raiz = doc.RootElement;
                if (raiz.ValueKind == JsonValueKind.Object)
                {
                    mensagem = raiz.TryGetProperty("mensagem", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
                    erro = raiz.TryGetProperty("erro", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
                }
            }
            catch (JsonException)
            {
            }
            return new ListaPrecos(status, novaEtag, string.Empty, linhas, mensagem, erro);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ListaPrecos(0, string.Empty, string.Empty, -1, ex.Message, string.Empty);
        }
    }

    private async Task<RespostaServico> EnviarAsync(HttpMethod metodo, string caminho, object? corpo, CancellationToken ct)
    {
        var cfg = ConfigManager.LoadConfig();
        var token = KioskToken.Ler();
        var baseUrl = (cfg.GestaoApiUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token))
            return new RespostaServico(true, 0, false, "Sem Gestão configurado", default);

        try
        {
            using var requisicao = new HttpRequestMessage(metodo, baseUrl + caminho);
            requisicao.Headers.Add("X-Kiosk-Identificador", cfg.KioskIdentificador);
            requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (corpo != null) requisicao.Content = JsonContent.Create(corpo);

            using var resposta = await _http.SendAsync(requisicao, ct);
            var texto = await resposta.Content.ReadAsStringAsync(ct);
            var status = (int)resposta.StatusCode;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto);
                var raiz = doc.RootElement;
                var sucesso = raiz.TryGetProperty("sucesso", out var s) && s.ValueKind == JsonValueKind.True;
                var mensagem = raiz.TryGetProperty("mensagem", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : string.Empty;
                var erro = raiz.TryGetProperty("erro", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
                var dados = raiz.TryGetProperty("dados", out var d) ? d.Clone() : default;
                return new RespostaServico(false, status, sucesso && resposta.IsSuccessStatusCode, mensagem, dados, erro);
            }
            catch (JsonException)
            {
                return new RespostaServico(false, status, false, $"Resposta inválida do Gestão ({status})", default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RespostaServico(true, 0, false, ex.Message, default);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _lista.Dispose();
    }
}
