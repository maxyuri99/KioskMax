using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;

namespace NpsWatchdog;

public sealed class CameraContagem
{
    public int CdCamera { get; init; }
    public string NmCamera { get; init; } = string.Empty;
    public string DeEndereco { get; init; } = string.Empty;
    public int NuCanal { get; init; } = 1;
    public string NmUsuario { get; init; } = string.Empty;
    public string Senha { get; init; } = string.Empty;
    public DateOnly? DtDiaInicial { get; init; }

    public override string ToString() => $"{CdCamera} ({NmCamera})";
}

public sealed record HoraContagem(DateTime Hora, int QtEntradas, int QtSaidas, int QtReentradas);

public sealed class FalhaCamera : Exception
{
    public FalhaCamera(string mensagem) : base(mensagem)
    {
    }
}

public sealed class ColetorCameras
{
    private const int IntervaloPadrao = 600;
    private const int MaximoHorasPorEnvio = 744;
    private static readonly TimeSpan EsperaSemVinculo = TimeSpan.FromMinutes(1);

    private readonly ClienteGestaoServico _gestao;
    private readonly Dictionary<int, string> _ultimoErroCamera = new();
    private int _intervaloSegundos = IntervaloPadrao;
    private string _ultimoMotivo = string.Empty;

    public ColetorCameras(ClienteGestaoServico gestao)
    {
        _gestao = gestao;
    }

    public async Task ExecutarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan espera;
            try
            {
                espera = await RodadaAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro no ciclo da contagem de pessoas das câmeras");
                espera = TimeSpan.FromSeconds(_intervaloSegundos);
            }

            await Task.Delay(espera, ct);
        }
    }

    private async Task<TimeSpan> RodadaAsync(CancellationToken ct)
    {
        if (!_gestao.Configurado(out var motivo))
        {
            Anotar(motivo);
            return EsperaSemVinculo;
        }

        var resposta = await _gestao.ConsultarCamerasAsync(ct);
        if (resposta.Status == 403 && string.Equals(resposta.Erro, "BLOQUEADO", StringComparison.OrdinalIgnoreCase))
        {
            Anotar("Contagem de pessoas aguardando a aprovação do kiosk no Gestão");
            return TimeSpan.FromSeconds(_intervaloSegundos);
        }

        if (resposta.FalhaRede || !resposta.Sucesso || !resposta.TemDados)
        {
            Anotar($"Gestão não respondeu à consulta das câmeras ({resposta.Status}): {resposta.Mensagem}");
            return TimeSpan.FromSeconds(_intervaloSegundos);
        }

        _intervaloSegundos = Math.Clamp(LeituraJson.Inteiro(resposta.Dados, "intervaloSegundos", IntervaloPadrao), 60, 86400);
        var cameras = LerCameras(resposta.Dados);
        if (cameras.Count == 0)
        {
            Anotar("Nenhuma câmera de contagem de pessoas vinculada a este kiosk");
            return TimeSpan.FromSeconds(_intervaloSegundos);
        }

        Anotar($"{cameras.Count} câmera(s) de contagem de pessoas vinculada(s) a este kiosk");
        foreach (var camera in cameras)
        {
            try
            {
                await ColetarAsync(camera, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha na contagem de pessoas da câmera {Camera}", camera.ToString());
            }
        }

        return TimeSpan.FromSeconds(_intervaloSegundos);
    }

    private async Task ColetarAsync(CameraContagem camera, CancellationToken ct)
    {
        var agora = DateTime.Now;
        var modelo = string.Empty;
        var erro = string.Empty;
        IReadOnlyList<HoraContagem> horas = Array.Empty<HoraContagem>();

        try
        {
            using var leitor = new LeitorCameraHikvision(camera);
            modelo = await leitor.LerModeloAsync(ct);
            await leitor.ConferirRelogioAsync(agora, ct);
            horas =await leitor.LerContagemAsync(ContagemHikvision.Dias(camera.DtDiaInicial, DateOnly.FromDateTime(agora)), agora, ct);
        }
        catch (FalhaCamera ex)
        {
            erro = ex.Message;
        }

        AnotarCamera(camera, erro);

        var partes = horas.Chunk(MaximoHorasPorEnvio).ToList();
        if (partes.Count == 0) partes.Add(Array.Empty<HoraContagem>());

        foreach (var parte in partes)
        {
            var corpo = new
            {
                dsModelo = modelo,
                erro,
                horas = parte.Select(h => new
                {
                    dtHora = ContagemHikvision.FormatarHora(h.Hora),
                    qtEntradas = h.QtEntradas,
                    qtSaidas = h.QtSaidas,
                    qtReentradas = h.QtReentradas
                }).ToArray()
            };

            var resposta = await _gestao.EnviarContagensAsync(camera.CdCamera, corpo, ct);
            if (resposta.Status == 409)
            {
                Log.Warning("A câmera {Camera} não é deste kiosk no Gestão; contagem ignorada até o próximo ciclo", camera.ToString());
                return;
            }

            if (!resposta.Sucesso)
            {
                Log.Warning("O Gestão não registrou a contagem da câmera {Camera} ({Status}): {Erro} {Mensagem}",
                    camera.ToString(), resposta.Status, resposta.Erro, resposta.Mensagem);
                return;
            }
        }

        Log.Debug("Contagem da câmera {Camera} enviada: {Horas} hora(s)", camera.ToString(), horas.Count);
    }

    private static List<CameraContagem> LerCameras(JsonElement dados)
    {
        var cameras = new List<CameraContagem>();
        if (!dados.TryGetProperty("cameras", out var lista) || lista.ValueKind != JsonValueKind.Array) return cameras;

        foreach (var item in lista.EnumerateArray())
        {
            var cdCamera = LeituraJson.Inteiro(item, "cdCamera", 0);
            if (cdCamera <= 0) continue;

            cameras.Add(new CameraContagem
            {
                CdCamera = cdCamera,
                NmCamera = LeituraJson.Texto(item, "nmCamera"),
                DeEndereco = LeituraJson.Texto(item, "deEndereco"),
                NuCanal = Math.Max(1, LeituraJson.Inteiro(item, "nuCanal", 1)),
                NmUsuario = LeituraJson.Texto(item, "nmUsuario"),
                Senha = item.TryGetProperty("senha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty,
                DtDiaInicial = ContagemHikvision.LerDia(LeituraJson.Texto(item, "dtHoraInicial"))
            });
        }

        return cameras;
    }

    private void AnotarCamera(CameraContagem camera, string erro)
    {
        _ultimoErroCamera.TryGetValue(camera.CdCamera, out var anterior);
        if (erro == (anterior ?? string.Empty)) return;

        _ultimoErroCamera[camera.CdCamera] = erro;
        if (erro.Length > 0)
            Log.Warning("Câmera {Camera}: {Erro}", camera.ToString(), erro);
        else
            Log.Information("Câmera {Camera} voltou a responder", camera.ToString());
    }

    private void Anotar(string motivo)
    {
        if (motivo == _ultimoMotivo) return;
        _ultimoMotivo = motivo;
        Log.Information("{Motivo}", motivo);
    }
}

public sealed class LeitorCameraHikvision : IDisposable
{
    private static readonly TimeSpan TempoLimite = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _endereco;
    private readonly int _canal;

    public LeitorCameraHikvision(CameraContagem camera)
    {
        _endereco = camera.DeEndereco;
        _base = ContagemHikvision.EnderecoBase(camera.DeEndereco);
        _canal = camera.NuCanal;

        var uri = new Uri(_base);
        var credenciais = new CredentialCache
        {
            { uri, "Digest", new NetworkCredential(camera.NmUsuario, camera.Senha) }
        };
        var handler = new HttpClientHandler
        {
            Credentials = credenciais,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (requisicao, _, _, erros) =>
                erros == SslPolicyErrors.None
                || string.Equals(requisicao.RequestUri?.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
        };
        _http = new HttpClient(handler) { Timeout = TempoLimite };
    }

    public async Task<string> LerModeloAsync(CancellationToken ct)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, _base + "/ISAPI/System/deviceInfo");
        var (status, texto) = await EnviarAsync(requisicao, ct);
        if (status == HttpStatusCode.Unauthorized) throw new FalhaCamera("Usuário ou senha recusados pela câmera (401).");
        if (!IsSucesso(status)) return string.Empty;
        return ContagemHikvision.LerModelo(texto);
    }

    public async Task ConferirRelogioAsync(DateTime agora, CancellationToken ct)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, _base + "/ISAPI/System/time");
        var (status, texto) = await EnviarAsync(requisicao, ct);
        if (status == HttpStatusCode.Unauthorized) throw new FalhaCamera("Usuário ou senha recusados pela câmera (401).");
        if (!IsSucesso(status)) return;
        if (ContagemHikvision.LerRelogio(texto) is not { } relogio) return;
        if (ContagemHikvision.RelogioErrado(relogio, agora))
            throw new FalhaCamera(ContagemHikvision.MensagemRelogio(relogio, agora));
    }

    public async Task<IReadOnlyList<HoraContagem>> LerContagemAsync(IEnumerable<DateOnly> dias, DateTime agora, CancellationToken ct)
    {
        var horas = new SortedDictionary<DateTime, HoraContagem>();
        foreach (var dia in dias)
        {
            using var requisicao = new HttpRequestMessage(HttpMethod.Post, $"{_base}/ISAPI/System/Video/inputs/channels/{_canal}/counting/search")
            {
                Content = new StringContent(ContagemHikvision.CorpoBusca(dia), Encoding.UTF8, "application/xml")
            };

            var (status, texto) = await EnviarAsync(requisicao, ct);
            switch (status)
            {
                case HttpStatusCode.Unauthorized:
                    throw new FalhaCamera("Usuário ou senha recusados pela câmera (401).");
                case HttpStatusCode.Forbidden:
                    throw new FalhaCamera("A câmera não tem a contagem de pessoas ligada ou o usuário não tem permissão para ela (403).");
                case HttpStatusCode.NotFound:
                    throw new FalhaCamera("A câmera não tem a contagem de pessoas ligada (404).");
            }

            if (!IsSucesso(status))
                throw new FalhaCamera($"A câmera recusou a consulta da contagem de pessoas (HTTP {(int)status}).");

            foreach (var hora in ContagemHikvision.LerHoras(texto, agora))
                horas[hora.Hora] = hora;
        }

        return horas.Values.ToList();
    }

    private async Task<(HttpStatusCode Status, string Texto)> EnviarAsync(HttpRequestMessage requisicao, CancellationToken ct)
    {
        try
        {
            using var resposta = await _http.SendAsync(requisicao, ct);
            var texto = await resposta.Content.ReadAsStringAsync(ct);
            return (resposta.StatusCode, texto);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new FalhaCamera($"Câmera não respondeu em {_endereco} (tempo esgotado).");
        }
        catch (HttpRequestException ex)
        {
            var motivo = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "endereço não encontrado",
                HttpRequestError.SecureConnectionError => "falha na conexão segura",
                _ => "sem conexão"
            };
            throw new FalhaCamera($"Câmera não respondeu em {_endereco} ({motivo}).");
        }
    }

    private static bool IsSucesso(HttpStatusCode status) => (int)status is >= 200 and < 300;

    public void Dispose() => _http.Dispose();
}

public static class ContagemHikvision
{
    private const string FormatoHora = "yyyy-MM-dd'T'HH':00:00'";
    private static readonly TimeSpan DiferencaMaximaRelogio = TimeSpan.FromMinutes(10);

    public static string EnderecoBase(string deEndereco)
    {
        var endereco = (deEndereco ?? string.Empty).Trim().TrimEnd('/');
        if (!endereco.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !endereco.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endereco = "http://" + endereco;
        }

        if (!Uri.TryCreate(endereco, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new FalhaCamera($"Endereço da câmera inválido: \"{deEndereco}\".");

        return endereco;
    }

    public static DateOnly? LerDia(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto) || texto.Length < 10) return null;
        return DateOnly.TryParseExact(texto[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia)
            ? dia
            : null;
    }

    public static List<DateOnly> Dias(DateOnly? inicial, DateOnly hoje)
    {
        var dias = new List<DateOnly>();
        var dia = inicial is { } i && i <= hoje ? i : hoje;
        for (; dia <= hoje; dia = dia.AddDays(1)) dias.Add(dia);
        return dias;
    }

    public static string CorpoBusca(DateOnly dia)
    {
        var data = dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><countingStatisticsDescription>"
               + "<statisticType>enter,exit,duplicatePeople</statisticType><reportType>daily</reportType>"
               + $"<timeSpanList><timeSpan><startTime>{data}T00:00:00</startTime><endTime>{data}T23:59:59</endTime></timeSpan></timeSpanList>"
               + "</countingStatisticsDescription>";
    }

    public static string FormatarHora(DateTime hora) => hora.ToString(FormatoHora, CultureInfo.InvariantCulture);

    public static string LerModelo(string xml)
    {
        try
        {
            var raiz = XDocument.Parse(xml).Root;
            var modelo = raiz?.Elements().FirstOrDefault(e => e.Name.LocalName == "model")?.Value.Trim() ?? string.Empty;
            return modelo.Length > 100 ? modelo[..100] : modelo;
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    public static DateTime? LerRelogio(string xml)
    {
        try
        {
            var raiz = XDocument.Parse(xml).Root;
            var texto = raiz?.Elements().FirstOrDefault(e => e.Name.LocalName == "localTime")?.Value.Trim() ?? string.Empty;
            if (texto.Length < 19) return null;
            return DateTime.TryParseExact(texto[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var valor)
                ? valor
                : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static bool RelogioErrado(DateTime relogio, DateTime agora) => (relogio - agora).Duration() > DiferencaMaximaRelogio;

    public static string MensagemRelogio(DateTime relogio, DateTime agora) =>
        $"O relógio da câmera marca {relogio:dd/MM HH:mm}, mas aqui são {agora:dd/MM HH:mm}. " +
        "Acerte o fuso horário da câmera (Brasília, UTC-3) e a hora; enquanto isso a contagem não é enviada para não gravar horários errados.";

    public static List<HoraContagem> LerHoras(string xml, DateTime agora)
    {
        XDocument documento;
        try
        {
            documento = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            throw new FalhaCamera("A câmera devolveu uma resposta da contagem de pessoas que não foi entendida.");
        }

        var limite = new DateTime(agora.Year, agora.Month, agora.Day, agora.Hour, 0, 0);
        var horas = new List<HoraContagem>();
        foreach (var item in documento.Descendants().Where(e => e.Name.LocalName == "matchElement"))
        {
            var inicio = item.Elements().FirstOrDefault(e => e.Name.LocalName == "timeSpan")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "startTime")?.Value;
            if (LerHora(inicio) is not { } hora || hora > limite) continue;

            horas.Add(new HoraContagem(hora,
                Numero(item, "enterCount"),
                Numero(item, "exitCount"),
                Numero(item, "duplicatePeopleCount")));
        }

        return horas;
    }

    public static DateTime? LerHora(string? texto)
    {
        texto = texto?.Trim() ?? string.Empty;
        if (texto.Length < 19) return null;
        if (!DateTime.TryParseExact(texto[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var valor))
            return null;
        return new DateTime(valor.Year, valor.Month, valor.Day, valor.Hour, 0, 0);
    }

    private static int Numero(XElement item, string nome)
    {
        var texto = item.Elements().FirstOrDefault(e => e.Name.LocalName == nome)?.Value;
        return int.TryParse(texto?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero) && numero > 0 ? numero : 0;
    }
}
