using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NpsShared.Utilities;
using Serilog;

namespace NpsWatchdog;

public sealed class AcessoRemoto : IDisposable
{
    private const string CaracteresSenha = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private static readonly TimeSpan IntervaloInforme = TimeSpan.FromHours(6);

    private readonly ClienteGestaoServico _gestao;
    private readonly HttpClient _download = new() { Timeout = TimeSpan.FromMinutes(15) };
    private string _ultimoMotivo = string.Empty;
    private string _ultimoInformado = string.Empty;
    private DateTime _informadoEm = DateTime.MinValue;
    private string _ultimoErro = string.Empty;

    public AcessoRemoto(ClienteGestaoServico gestao)
    {
        _gestao = gestao;
    }

    private static string PastaProtegida
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

    public async Task ExecutarAsync(CancellationToken ct)
    {
        if (!_gestao.Configurado(out var motivo))
        {
            Anotar(motivo);
            return;
        }

        var resposta = await _gestao.ConsultarRemotoAsync(ct);
        if (resposta.FalhaRede || !resposta.Sucesso || !resposta.TemDados)
        {
            Anotar($"Gestão não respondeu à consulta do acesso remoto ({resposta.Status}): {resposta.Mensagem}");
            return;
        }

        var ordem = resposta.Dados;
        if (!Booleano(ordem, "instalar"))
        {
            Anotar("Acesso remoto não liberado para este kiosk no Gestão");
            return;
        }

        var versao = Texto(ordem, "versao");
        var url = Texto(ordem, "url");
        var sha256 = Texto(ordem, "sha256").ToLowerInvariant();
        var servidor = Texto(ordem, "servidor");
        var chavePublica = Texto(ordem, "chavePublica");
        var temSenha = Booleano(ordem, "temSenha");

        try
        {
            var instalada = RustDesk.VersaoInstalada();
            if (!string.Equals(instalada, versao, StringComparison.Ordinal))
            {
                if (ArquivoTefOcupado.Ocupado())
                {
                    Anotar("Instalação do RustDesk adiada: TEF em andamento");
                    return;
                }

                Log.Information("Instalando o RustDesk {Versao} (instalado: {Instalada})", versao, instalada ?? "nenhum");
                var instalador = await BaixarAsync(versao, url, sha256, ct);
                await RustDesk.InstalarAsync(instalador, versao, ct);
                Log.Information("RustDesk {Versao} instalado", versao);
                LimparInstaladores(versao);
            }

            if (!RustDesk.ServicoRodando())
            {
                throw new InvalidOperationException("O serviço do RustDesk não está em execução");
            }

            await ConfigurarAsync(servidor, chavePublica, ct);

            string? senha = null;
            if (!temSenha || !RustDesk.TemSenhaPermanente())
            {
                senha = GerarSenha();
                if (!await RustDesk.DefinirSenhaAsync(senha, ct))
                    throw new InvalidOperationException("O RustDesk não aceitou a senha permanente");
                Log.Information("Senha permanente do RustDesk definida");
            }

            var id = await RustDesk.LerIdAsync(ct);
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("O RustDesk não informou o ID");

            await InformarAsync(id, RustDesk.VersaoInstalada() ?? versao, senha, ct);
            _ultimoErro = string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha no acesso remoto");
            var mensagem = ex.Message.Length > 450 ? ex.Message[..450] : ex.Message;
            if (mensagem != _ultimoErro)
            {
                var envio = await _gestao.InformarRemotoAsync(new { erro = mensagem }, ct);
                if (envio.Sucesso) _ultimoErro = mensagem;
            }
        }
    }

    private async Task ConfigurarAsync(string servidor, string chavePublica, CancellationToken ct)
    {
        var desejadas = new Dictionary<string, string>
        {
            ["custom-rendezvous-server"] = servidor,
            ["relay-server"] = servidor,
            ["key"] = chavePublica,
            ["approve-mode"] = "password",
            ["verification-method"] = "use-permanent-password"
        };

        var atuais = RustDesk.LerOpcoes();
        foreach (var (nome, valor) in desejadas)
        {
            if (atuais.TryGetValue(nome, out var atual) && atual == valor) continue;
            await RustDesk.DefinirOpcaoAsync(nome, valor, ct);
            Log.Information("Opção {Opcao} do RustDesk ajustada", nome);
        }
    }

    private async Task InformarAsync(string id, string versao, string? senha, CancellationToken ct)
    {
        var assinatura = id + "|" + versao;
        if (senha == null && assinatura == _ultimoInformado && DateTime.UtcNow - _informadoEm < IntervaloInforme) return;

        var corpo = senha == null
            ? (object)new { deIdRemoto = id, deVersao = versao }
            : new { deIdRemoto = id, deVersao = versao, senha };
        var resposta = await _gestao.InformarRemotoAsync(corpo, ct);
        if (!resposta.Sucesso)
            throw new InvalidOperationException($"O Gestão não registrou o acesso remoto ({resposta.Status}): {resposta.Mensagem}");

        _ultimoInformado = assinatura;
        _informadoEm = DateTime.UtcNow;
        Log.Information("Acesso remoto informado ao Gestão: ID {Id}, versão {Versao}", id, versao);
    }

    private async Task<string> BaixarAsync(string versao, string url, string sha256, CancellationToken ct)
    {
        var pasta = PastaProtegida;
        var destino = Path.Combine(pasta, $"rustdesk-{versao}.exe");
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
                Log.Warning("Download do RustDesk interrompido (tentativa {Tentativa}): {Erro}", tentativa, ex.Message);
                if (tentativa == 5) throw new InvalidOperationException("Não foi possível baixar o instalador do RustDesk: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10 * tentativa), ct);
            }
        }

        var obtido = await HashAsync(parcial, ct);
        if (obtido != sha256)
        {
            File.Delete(parcial);
            throw new InvalidOperationException("O instalador baixado não confere com o SHA-256 publicado no Gestão");
        }

        File.Move(parcial, destino, true);
        return destino;
    }

    private async Task BaixarParteAsync(string url, string parcial, CancellationToken ct)
    {
        var inicio = File.Exists(parcial) ? new FileInfo(parcial).Length : 0;
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, url);
        if (inicio > 0) requisicao.Headers.Range = new RangeHeaderValue(inicio, null);

        using var resposta = await _download.SendAsync(requisicao, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resposta.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return;
        }
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

    private static void LimparInstaladores(string versaoAtual)
    {
        try
        {
            foreach (var arquivo in Directory.EnumerateFiles(PastaProtegida, "rustdesk-*"))
            {
                if (Path.GetFileName(arquivo) != $"rustdesk-{versaoAtual}.exe") File.Delete(arquivo);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Falha ao limpar instaladores antigos do RustDesk");
        }
    }

    private static string GerarSenha()
    {
        Span<char> senha = stackalloc char[20];
        for (var i = 0; i < senha.Length; i++)
            senha[i] = CaracteresSenha[RandomNumberGenerator.GetInt32(CaracteresSenha.Length)];
        return new string(senha);
    }

    private void Anotar(string motivo)
    {
        if (motivo == _ultimoMotivo) return;
        _ultimoMotivo = motivo;
        Log.Information("{Motivo}", motivo);
    }

    private static string Texto(JsonElement elemento, string nome) =>
        elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static bool Booleano(JsonElement elemento, string nome) =>
        elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.True;

    public void Dispose() => _download.Dispose();
}
