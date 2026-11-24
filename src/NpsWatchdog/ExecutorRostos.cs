using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;

namespace NpsWatchdog;

public sealed class TarefaRosto
{
    public int CdRosto { get; init; }
    public int CdCamera { get; init; }
    public string DeOperacao { get; init; } = string.Empty;
    public string NmPessoa { get; init; } = string.Empty;
    public int? NuPid { get; init; }
    public string DeEndereco { get; init; } = string.Empty;
    public string NmUsuario { get; init; } = string.Empty;
    public string Senha { get; init; } = string.Empty;

    public override string ToString() => $"{CdRosto} ({DeOperacao} na câmera {CdCamera})";
}

public sealed record RostoCamera(int NuPid, string NmPessoa, string DeModelagem);

public sealed record DesfechoRosto(bool Sucesso, string Mensagem, int? NuPid, IReadOnlyList<RostoCamera>? Rostos);

public sealed class ExecutorRostos
{
    private const int IntervaloPadrao = 60;
    private static readonly TimeSpan EsperaSemVinculo = TimeSpan.FromMinutes(1);

    private readonly ClienteGestaoServico _gestao;
    private int _intervaloSegundos = IntervaloPadrao;
    private string _ultimoMotivo = string.Empty;

    public ExecutorRostos(ClienteGestaoServico gestao)
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
                Log.Error(ex, "Erro no ciclo dos rostos das câmeras");
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

        var resposta = await _gestao.ConsultarRostosAsync(ct);
        if (resposta.Status == 403 && string.Equals(resposta.Erro, "BLOQUEADO", StringComparison.OrdinalIgnoreCase))
        {
            Anotar("Rostos das câmeras aguardando a aprovação do kiosk no Gestão");
            return TimeSpan.FromSeconds(_intervaloSegundos);
        }

        if (resposta.FalhaRede || !resposta.Sucesso || !resposta.TemDados)
        {
            Anotar($"Gestão não respondeu à consulta dos rostos ({resposta.Status}): {resposta.Mensagem}");
            return TimeSpan.FromSeconds(_intervaloSegundos);
        }

        _intervaloSegundos = Math.Clamp(LeituraJson.Inteiro(resposta.Dados, "intervaloSegundos", IntervaloPadrao), 30, 3600);
        var tarefas = LerTarefas(resposta.Dados);
        Anotar(tarefas.Count == 0 ? "Nenhum pedido de rosto para este kiosk" : $"{tarefas.Count} pedido(s) de rosto para este kiosk");
        foreach (var tarefa in tarefas)
        {
            try
            {
                await ExecutarTarefaAsync(tarefa, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Falha no pedido de rosto {Tarefa}", tarefa.ToString());
            }
        }

        return TimeSpan.FromSeconds(_intervaloSegundos);
    }

    private async Task ExecutarTarefaAsync(TarefaRosto tarefa, CancellationToken ct)
    {
        DesfechoRosto desfecho;
        try
        {
            using var camera = new CameraRostosHikvision(tarefa);
            desfecho = tarefa.DeOperacao switch
            {
                "CADASTRAR" => await CadastrarAsync(camera, tarefa, ct),
                "REMOVER" => await RemoverAsync(camera, tarefa, ct),
                "LISTAR" => new DesfechoRosto(true, "Lista lida da câmera.", null, await camera.ListarAsync(ct)),
                _ => new DesfechoRosto(false, $"Operação de rosto desconhecida: {tarefa.DeOperacao}.", null, null)
            };
        }
        catch (FalhaCamera ex)
        {
            desfecho = new DesfechoRosto(false, ex.Message, null, null);
        }

        if (desfecho.Sucesso)
            Log.Information("Pedido de rosto {Tarefa}: {Mensagem}", tarefa.ToString(), desfecho.Mensagem);
        else
            Log.Warning("Pedido de rosto {Tarefa} falhou: {Mensagem}", tarefa.ToString(), desfecho.Mensagem);

        var corpo = new
        {
            sucesso = desfecho.Sucesso,
            mensagem = desfecho.Mensagem,
            nuPid = desfecho.NuPid,
            rostos = desfecho.Rostos?.Select(r => new { nuPid = r.NuPid, nmPessoa = r.NmPessoa, deModelagem = r.DeModelagem }).ToArray()
        };
        var resposta = await _gestao.InformarRostoAsync(tarefa.CdRosto, corpo, ct);
        if (!resposta.Sucesso && resposta.Status != 409)
            Log.Warning("O Gestão não registrou o resultado do pedido de rosto {Tarefa} ({Status}): {Erro} {Mensagem}",
                tarefa.ToString(), resposta.Status, resposta.Erro, resposta.Mensagem);
    }

    private async Task<DesfechoRosto> CadastrarAsync(CameraRostosHikvision camera, TarefaRosto tarefa, CancellationToken ct)
    {
        var foto = await _gestao.BaixarFotoRostoAsync(tarefa.CdRosto, ct);
        if (foto.Length == 0)
            return new DesfechoRosto(false, "O Gestão não entregou a foto do pedido.", null, null);

        var nuPid = await camera.CadastrarAsync(tarefa.NmPessoa, foto, ct);
        var rostos = await camera.ListarAsync(ct);
        var gravado = rostos.FirstOrDefault(r => r.NuPid == nuPid);
        for (var tentativa = 0; tentativa < 3 && gravado is not null && RostosHikvision.ModelagemEmAndamento(gravado.DeModelagem); tentativa++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            rostos = await camera.ListarAsync(ct);
            gravado = rostos.FirstOrDefault(r => r.NuPid == nuPid);
        }

        if (gravado is not null && RostosHikvision.ModelagemFalhou(gravado.DeModelagem))
        {
            await camera.ApagarAsync(nuPid, ct);
            rostos = await camera.ListarAsync(ct);
            return new DesfechoRosto(false, "A câmera não encontrou um rosto na foto. Tire outra foto de frente, com o rosto inteiro e boa luz.", null, rostos);
        }

        return new DesfechoRosto(true, $"Rosto gravado na câmera (PID {nuPid}).", nuPid, rostos);
    }

    private static async Task<DesfechoRosto> RemoverAsync(CameraRostosHikvision camera, TarefaRosto tarefa, CancellationToken ct)
    {
        if (tarefa.NuPid is not { } nuPid || nuPid <= 0)
            return new DesfechoRosto(false, "Pedido de remoção sem o PID do rosto.", null, null);

        var rostos = await camera.ListarAsync(ct);
        var atual = rostos.FirstOrDefault(r => r.NuPid == nuPid);
        if (atual is null)
            return new DesfechoRosto(true, "O rosto já não estava na câmera.", nuPid, rostos);

        if (!RostosHikvision.MesmoNome(atual.NmPessoa, tarefa.NmPessoa))
            return new DesfechoRosto(false, $"O PID {nuPid} agora é de \"{atual.NmPessoa}\" na câmera; nada foi apagado. Atualize a lista e tente de novo.", null, rostos);

        await camera.ApagarAsync(nuPid, ct);
        rostos = await camera.ListarAsync(ct);
        return new DesfechoRosto(true, "Rosto removido da câmera.", nuPid, rostos);
    }

    private static List<TarefaRosto> LerTarefas(JsonElement dados)
    {
        var tarefas = new List<TarefaRosto>();
        if (!dados.TryGetProperty("tarefas", out var lista) || lista.ValueKind != JsonValueKind.Array) return tarefas;

        foreach (var item in lista.EnumerateArray())
        {
            var cdRosto = LeituraJson.Inteiro(item, "cdRosto", 0);
            if (cdRosto <= 0) continue;

            int? nuPid = item.TryGetProperty("nuPid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var valor) ? valor : null;
            tarefas.Add(new TarefaRosto
            {
                CdRosto = cdRosto,
                CdCamera = LeituraJson.Inteiro(item, "cdCamera", 0),
                DeOperacao = LeituraJson.Texto(item, "deOperacao").ToUpperInvariant(),
                NmPessoa = LeituraJson.Texto(item, "nmPessoa"),
                NuPid = nuPid,
                DeEndereco = LeituraJson.Texto(item, "deEndereco"),
                NmUsuario = LeituraJson.Texto(item, "nmUsuario"),
                Senha = item.TryGetProperty("senha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty
            });
        }

        return tarefas;
    }

    private void Anotar(string motivo)
    {
        if (motivo == _ultimoMotivo) return;
        _ultimoMotivo = motivo;
        Log.Information("{Motivo}", motivo);
    }
}

public sealed class CameraRostosHikvision : IDisposable
{
    private static readonly TimeSpan TempoLimite = TimeSpan.FromSeconds(30);
    private const int RostosPorPagina = 50;
    private const int MaximoPaginas = 200;

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _endereco;
    private string? _fdid;

    public CameraRostosHikvision(TarefaRosto tarefa)
    {
        _endereco = tarefa.DeEndereco;
        _base = ContagemHikvision.EnderecoBase(tarefa.DeEndereco);

        var uri = new Uri(_base);
        var credenciais = new CredentialCache
        {
            { uri, "Digest", new NetworkCredential(tarefa.NmUsuario, tarefa.Senha) }
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
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    private async Task<string> BibliotecaAsync(CancellationToken ct)
    {
        if (_fdid is not null) return _fdid;
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, _base + "/ISAPI/Intelligent/FDLib");
        var (status, texto) = await EnviarAsync(requisicao, ct);
        Conferir(status, "ler a biblioteca de rostos");
        _fdid = RostosHikvision.PrimeiraBiblioteca(texto)
                ?? throw new FalhaCamera("A câmera não tem biblioteca de rostos. Crie a biblioteca na câmera antes de cadastrar rostos.");
        return _fdid;
    }

    public async Task<IReadOnlyList<RostoCamera>> ListarAsync(CancellationToken ct)
    {
        var fdid = await BibliotecaAsync(ct);
        var rostos = new Dictionary<int, RostoCamera>();
        var posicao = 1;
        var busca = Guid.NewGuid().ToString("N");
        for (var pagina = 0; pagina < MaximoPaginas; pagina++)
        {
            using var requisicao = new HttpRequestMessage(HttpMethod.Post, _base + "/ISAPI/Intelligent/FDLib/FDSearch")
            {
                Content = new StringContent(RostosHikvision.CorpoBusca(busca, posicao, RostosPorPagina, fdid), Encoding.UTF8, "application/xml")
            };
            var (status, texto) = await EnviarAsync(requisicao, ct);
            Conferir(status, "listar os rostos");
            var lidos = RostosHikvision.LerBusca(texto, out var total, out var maisResultados);
            foreach (var rosto in lidos) rostos[rosto.NuPid] = rosto;
            posicao += lidos.Count;
            if (lidos.Count == 0 || !maisResultados || (total > 0 && posicao > total)) break;
        }

        return rostos.Values.OrderBy(r => r.NuPid).ToList();
    }

    public async Task<int> CadastrarAsync(string nmPessoa, byte[] foto, CancellationToken ct)
    {
        var fdid = await BibliotecaAsync(ct);
        var limite = "limiteKioskMax" + Guid.NewGuid().ToString("N");
        using var requisicao = new HttpRequestMessage(HttpMethod.Post, $"{_base}/ISAPI/Intelligent/FDLib/{fdid}/picture")
        {
            Content = RostosHikvision.CorpoCadastro(limite, nmPessoa, foto)
        };
        var (status, texto) = await EnviarAsync(requisicao, ct);
        Conferir(status, "gravar o rosto");
        return RostosHikvision.LerPid(texto)
               ?? throw new FalhaCamera("A câmera recusou o rosto: " + RostosHikvision.MotivoRecusa(texto));
    }

    public async Task ApagarAsync(int nuPid, CancellationToken ct)
    {
        var fdid = await BibliotecaAsync(ct);
        using var requisicao = new HttpRequestMessage(HttpMethod.Delete, $"{_base}/ISAPI/Intelligent/FDLib/{fdid}/picture/{nuPid}");
        var (status, texto) = await EnviarAsync(requisicao, ct);
        Conferir(status, "apagar o rosto");
        if (!RostosHikvision.RespostaOk(texto))
            throw new FalhaCamera("A câmera não apagou o rosto: " + RostosHikvision.MotivoRecusa(texto));
    }

    private static void Conferir(HttpStatusCode status, string acao)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                throw new FalhaCamera("Usuário ou senha recusados pela câmera (401).");
            case HttpStatusCode.Forbidden:
                throw new FalhaCamera($"O usuário da câmera não tem permissão para {acao} (403). Cadastre no Gestão um usuário de rostos com permissão de administrador.");
            case HttpStatusCode.NotFound:
                throw new FalhaCamera($"A câmera não tem a função de rostos para {acao} (404).");
        }

        if ((int)status is < 200 or >= 300)
            throw new FalhaCamera($"A câmera recusou {acao} (HTTP {(int)status}).");
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
        catch (HttpRequestException)
        {
            throw new FalhaCamera($"Câmera não respondeu em {_endereco} (sem conexão).");
        }
    }

    public void Dispose() => _http.Dispose();
}

public static class RostosHikvision
{
    private const string Esquema = "version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"";

    public static string? PrimeiraBiblioteca(string xml)
    {
        var documento = Ler(xml, "a biblioteca de rostos");
        return documento.Descendants()
            .Where(e => e.Name.LocalName == "FDID")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
    }

    public static string CorpoBusca(string busca, int posicao, int quantidade, string fdid) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><FDSearchDescription>"
        + $"<searchID>{busca}</searchID><searchResultPosition>{posicao}</searchResultPosition>"
        + $"<maxResults>{quantidade}</maxResults><FDID>{SecurityElement.Escape(fdid)}</FDID></FDSearchDescription>";

    public static List<RostoCamera> LerBusca(string xml, out int total, out bool maisResultados)
    {
        var documento = Ler(xml, "a lista de rostos");
        var raiz = documento.Root;
        total = Numero(raiz, "totalMatches");
        var situacao = Texto(raiz, "responseStatusStrg");
        maisResultados = string.Equals(situacao, "MORE", StringComparison.OrdinalIgnoreCase);
        var rostos = new List<RostoCamera>();
        foreach (var item in documento.Descendants().Where(e => e.Name.LocalName == "MatchElement"))
        {
            if (!int.TryParse(Texto(item, "PID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0) continue;
            var modelagem = item.Elements().FirstOrDefault(e => e.Name.LocalName == "ModelingStatus");
            rostos.Add(new RostoCamera(pid, Texto(item, "name"), Texto(modelagem, "status")));
        }

        return rostos;
    }

    public static ByteArrayContent CorpoCadastro(string limite, string nmPessoa, byte[] foto)
    {
        var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><FaceAppendData {Esquema}><name>{SecurityElement.Escape(nmPessoa)}</name></FaceAppendData>";
        using var corpo = new MemoryStream();
        Escrever(corpo, $"--{limite}\r\nContent-Disposition: form-data; name=\"FaceAppendData\"\r\nContent-Type: application/xml\r\n\r\n{xml}\r\n");
        Escrever(corpo, $"--{limite}\r\nContent-Disposition: form-data; name=\"importImage\"; filename=\"rosto.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n");
        corpo.Write(foto);
        Escrever(corpo, $"\r\n--{limite}--\r\n");
        var conteudo = new ByteArrayContent(corpo.ToArray());
        conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={limite}");
        return conteudo;
    }

    public static int? LerPid(string xml)
    {
        XDocument documento;
        try
        {
            documento = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var texto = documento.Descendants().FirstOrDefault(e => e.Name.LocalName == "PID")?.Value.Trim();
        return int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }

    public static bool RespostaOk(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return true;
        try
        {
            var raiz = XDocument.Parse(xml).Root;
            var codigo = Texto(raiz, "statusCode");
            return codigo.Length == 0 || codigo == "1";
        }
        catch (System.Xml.XmlException)
        {
            return true;
        }
    }

    public static string MotivoRecusa(string xml)
    {
        try
        {
            var raiz = XDocument.Parse(xml).Root;
            var partes = new[] { Texto(raiz, "statusString"), Texto(raiz, "subStatusCode"), Texto(raiz, "errorMsg") }
                .Where(p => p.Length > 0)
                .Distinct()
                .ToArray();
            return partes.Length == 0 ? "resposta sem motivo." : string.Join(", ", partes) + ".";
        }
        catch (System.Xml.XmlException)
        {
            return "resposta que não foi entendida.";
        }
    }

    public static bool ModelagemFalhou(string modelagem) =>
        string.Equals(modelagem, "failed", StringComparison.OrdinalIgnoreCase);

    public static bool ModelagemEmAndamento(string modelagem) =>
        string.Equals(modelagem, "none", StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelagem, "modeling", StringComparison.OrdinalIgnoreCase)
        || modelagem.Length == 0;

    public static bool MesmoNome(string naCamera, string pedido) =>
        string.Equals(naCamera.Trim(), pedido.Trim(), StringComparison.Ordinal);

    private static XDocument Ler(string xml, string assunto)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            throw new FalhaCamera($"A câmera devolveu {assunto} num formato que não foi entendido.");
        }
    }

    private static string Texto(XElement? elemento, string nome) =>
        elemento?.Elements().FirstOrDefault(e => e.Name.LocalName == nome)?.Value.Trim() ?? string.Empty;

    private static int Numero(XElement? elemento, string nome) =>
        int.TryParse(Texto(elemento, nome), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero) ? numero : 0;

    private static void Escrever(Stream destino, string texto) => destino.Write(Encoding.UTF8.GetBytes(texto));
}
