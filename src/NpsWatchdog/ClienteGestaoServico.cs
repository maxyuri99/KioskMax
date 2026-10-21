using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NpsShared.Configuration;
using NpsShared.Utilities;

namespace NpsWatchdog;

public sealed record RespostaServico(bool FalhaRede, int Status, bool Sucesso, string Mensagem, JsonElement Dados)
{
    public bool TemDados => Dados.ValueKind == JsonValueKind.Object;
}

public sealed class ClienteGestaoServico : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
                var dados = raiz.TryGetProperty("dados", out var d) ? d.Clone() : default;
                return new RespostaServico(false, status, sucesso && resposta.IsSuccessStatusCode, mensagem, dados);
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

    public void Dispose() => _http.Dispose();
}
