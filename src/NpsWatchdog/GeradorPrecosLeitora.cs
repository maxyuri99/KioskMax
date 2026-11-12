using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace NpsWatchdog;

public sealed record LojaPrecos(int CodEmp, string DeArquivo, bool FlForcar);

public sealed class GeradorPrecosLeitora
{
    private const int IntervaloPadrao = 600;
    private static readonly TimeSpan Consulta = TimeSpan.FromMinutes(1);
    private static readonly Encoding Ansi = CriarAnsi();

    private readonly ClienteGestaoServico _gestao;
    private readonly Dictionary<int, DateTime> _ultimaGeracao = new();
    private string _etag = string.Empty;
    private string _conteudo = string.Empty;
    private int _linhas;
    private string _ultimoMotivo = string.Empty;

    public GeradorPrecosLeitora(ClienteGestaoServico gestao)
    {
        _gestao = gestao;
    }

    private static Encoding CriarAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    public async Task ExecutarAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RodadaAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro no ciclo dos preços das leitoras");
            }

            await Task.Delay(Consulta, ct);
        }
    }

    private async Task RodadaAsync(CancellationToken ct)
    {
        if (!_gestao.Configurado(out var motivo))
        {
            Anotar(motivo);
            return;
        }

        var resposta = await _gestao.ConsultarPrecosAsync(ct);
        if (resposta.Status == 404)
        {
            Anotar("Gestão ainda sem a rota dos preços das leitoras");
            return;
        }
        if (resposta.Status == 403)
        {
            Anotar("Preços das leitoras aguardando a aprovação do kiosk no Gestão");
            return;
        }
        if (resposta.FalhaRede || !resposta.Sucesso || !resposta.TemDados)
        {
            Anotar($"Gestão não respondeu à consulta dos preços das leitoras ({resposta.Status}): {resposta.Mensagem}");
            return;
        }

        var intervalo = TimeSpan.FromSeconds(Math.Clamp(LeituraJson.Inteiro(resposta.Dados, "intervaloSegundos", IntervaloPadrao), 60, 86400));
        var lojas = LerLojas(resposta.Dados);
        if (lojas.Count == 0)
        {
            Anotar("Este kiosk não gera os preços das leitoras de nenhuma loja");
            _ultimaGeracao.Clear();
            return;
        }

        var agora = DateTime.UtcNow;
        var devidas = lojas.Where(l => l.FlForcar || !_ultimaGeracao.TryGetValue(l.CodEmp, out var ultima) || agora - ultima >= intervalo).ToList();
        Anotar($"Preços das leitoras: {lojas.Count} loja(s) neste kiosk");
        if (devidas.Count == 0) return;

        if (!await BaixarAsync(devidas.Any(l => l.FlForcar), ct)) return;

        foreach (var loja in devidas)
        {
            await GerarAsync(loja, ct);
        }
    }

    private async Task<bool> BaixarAsync(bool forcar, CancellationToken ct)
    {
        var etag = forcar || string.IsNullOrEmpty(_conteudo) ? string.Empty : _etag;
        var lista = await _gestao.BaixarListaPrecosAsync(etag, ct);
        if (lista.Status == 304 && !string.IsNullOrEmpty(_conteudo)) return true;
        if (lista.Status == 200 && !string.IsNullOrEmpty(lista.Etag))
        {
            _conteudo = lista.Texto;
            _etag = lista.Etag;
            _linhas = lista.Linhas >= 0 ? lista.Linhas : ContarLinhas(_conteudo);
            return true;
        }
        if (lista.Status == 503 && string.Equals(lista.Erro, "LISTA_EM_PREPARO", StringComparison.OrdinalIgnoreCase))
        {
            Anotar("Lista de preços em preparo no Gestão; nova tentativa em 1 minuto");
            return false;
        }
        Anotar($"Gestão não entregou a lista de preços ({lista.Status}): {lista.Mensagem}");
        return false;
    }

    private static int ContarLinhas(string texto) => texto.Length == 0 ? 0 : texto.Count(c => c == '\n') + 1;

    private async Task GerarAsync(LojaPrecos loja, CancellationToken ct)
    {
        object corpo;
        try
        {
            Gravar(loja.DeArquivo, _conteudo);
            _ultimaGeracao[loja.CodEmp] = DateTime.UtcNow;
            corpo = new { deHash = Hash(_conteudo), qtLinhas = _linhas };
            Log.Information("PrecosLoja gerado para a empresa {CodEmp} em {Arquivo} ({Linhas} linhas)", loja.CodEmp, loja.DeArquivo, _linhas);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ultimaGeracao[loja.CodEmp] = DateTime.UtcNow;
            corpo = new { dsErro = $"Não foi possível gravar {loja.DeArquivo}: {ex.Message}" };
            Log.Warning(ex, "Falha ao gravar o PrecosLoja da empresa {CodEmp} em {Arquivo}", loja.CodEmp, loja.DeArquivo);
        }

        var resposta = await _gestao.InformarPrecosAsync(loja.CodEmp, corpo, ct);
        if (!resposta.Sucesso)
            Log.Warning("Gestão não registrou a geração dos preços da empresa {CodEmp} ({Status}): {Mensagem}", loja.CodEmp, resposta.Status, resposta.Mensagem);
    }

    public static void Gravar(string arquivo, string conteudo)
    {
        var pasta = Path.GetDirectoryName(arquivo);
        if (string.IsNullOrEmpty(pasta)) throw new ArgumentException("Caminho do arquivo sem pasta");
        Directory.CreateDirectory(pasta);
        var temporario = arquivo + ".tmp";
        File.WriteAllBytes(temporario, Ansi.GetBytes(conteudo));
        File.Move(temporario, arquivo, true);
    }

    public static string Hash(string conteudo) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo))).ToLowerInvariant();

    private static List<LojaPrecos> LerLojas(JsonElement dados)
    {
        var lojas = new List<LojaPrecos>();
        if (!dados.TryGetProperty("lojas", out var lista) || lista.ValueKind != JsonValueKind.Array) return lojas;
        foreach (var item in lista.EnumerateArray())
        {
            var codEmp = LeituraJson.Inteiro(item, "codEmp", 0);
            var arquivo = item.TryGetProperty("deArquivo", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? string.Empty : string.Empty;
            var forcar = item.TryGetProperty("flForcar", out var f) && f.ValueKind == JsonValueKind.True;
            if (codEmp > 0 && !string.IsNullOrWhiteSpace(arquivo)) lojas.Add(new LojaPrecos(codEmp, arquivo, forcar));
        }
        return lojas;
    }

    private void Anotar(string motivo)
    {
        if (motivo == _ultimoMotivo) return;
        _ultimoMotivo = motivo;
        Log.Information("{Motivo}", motivo);
    }
}
