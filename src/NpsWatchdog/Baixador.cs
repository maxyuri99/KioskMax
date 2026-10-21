using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Serilog;

namespace NpsWatchdog;

public sealed class Baixador : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string PastaProtegida
    {
        get
        {
            var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpMais", "KioskServico");
            var seguranca = new DirectorySecurity();
            seguranca.SetAccessRuleProtection(true, false);
            foreach (var conta in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                seguranca.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(conta, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            var info = new DirectoryInfo(pasta);
            if (!info.Exists) info.Create(seguranca);
            else info.SetAccessControl(seguranca);
            return pasta;
        }
    }

    public async Task<string> BaixarAsync(string nomeArquivo, string url, string sha256, CancellationToken ct)
    {
        var destino = Path.Combine(PastaProtegida, nomeArquivo);
        if (File.Exists(destino) && await HashAsync(destino, ct) == sha256) return destino;

        var parcial = destino + ".parte";
        for (var tentativa = 1; tentativa <= 5; tentativa++)
        {
            try
            {
                await BaixarParteAsync(url, parcial, ct);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Warning("Download de {Arquivo} interrompido (tentativa {Tentativa}): {Erro}", nomeArquivo, tentativa, ex.Message);
                if (tentativa == 5) throw new InvalidOperationException($"Não foi possível baixar {nomeArquivo}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(10 * tentativa), ct);
            }
        }

        if (await HashAsync(parcial, ct) != sha256)
        {
            File.Delete(parcial);
            throw new InvalidOperationException($"{nomeArquivo} baixado não confere com o SHA-256 publicado no Gestão");
        }

        File.Move(parcial, destino, true);
        return destino;
    }

    public static bool Existe(string nomeArquivo) => File.Exists(Path.Combine(PastaProtegida, nomeArquivo));

    public static void Limpar(string padrao, IReadOnlyCollection<string> manter)
    {
        try
        {
            foreach (var arquivo in Directory.EnumerateFiles(PastaProtegida, padrao))
            {
                if (!manter.Contains(Path.GetFileName(arquivo), StringComparer.OrdinalIgnoreCase)) File.Delete(arquivo);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Falha ao limpar instaladores antigos ({Padrao})", padrao);
        }
    }

    private async Task BaixarParteAsync(string url, string parcial, CancellationToken ct)
    {
        var inicio = File.Exists(parcial) ? new FileInfo(parcial).Length : 0;
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, url);
        if (inicio > 0) requisicao.Headers.Range = new RangeHeaderValue(inicio, null);

        using var resposta = await _http.SendAsync(requisicao, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resposta.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return;
        resposta.EnsureSuccessStatusCode();

        var continuar = inicio > 0 && resposta.StatusCode == HttpStatusCode.PartialContent;
        await using var origem = await resposta.Content.ReadAsStreamAsync(ct);
        await using var arquivo = new FileStream(parcial, continuar ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await origem.CopyToAsync(arquivo, ct);
    }

    private static async Task<string> HashAsync(string caminho, CancellationToken ct)
    {
        await using var arquivo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = await SHA256.HashDataAsync(arquivo, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}
