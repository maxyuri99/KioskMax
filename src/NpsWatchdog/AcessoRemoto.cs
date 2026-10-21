using System.Security.Cryptography;
using System.Text.Json;
using NpsShared.Utilities;
using Serilog;

namespace NpsWatchdog;

public sealed class AcessoRemoto
{
    private const string CaracteresSenha = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private static readonly TimeSpan IntervaloInforme = TimeSpan.FromHours(6);

    private readonly ClienteGestaoServico _gestao;
    private readonly Baixador _baixador;
    private string _ultimoMotivo = string.Empty;
    private string _ultimoInformado = string.Empty;
    private DateTime _informadoEm = DateTime.MinValue;
    private string _ultimoErro = string.Empty;

    public AcessoRemoto(ClienteGestaoServico gestao, Baixador baixador)
    {
        _gestao = gestao;
        _baixador = baixador;
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
        if (!LeituraJson.Booleano(ordem, "instalar"))
        {
            Anotar("Acesso remoto não liberado para este kiosk no Gestão");
            return;
        }

        var versao = LeituraJson.Texto(ordem, "versao");
        var url = LeituraJson.Texto(ordem, "url");
        var sha256 = LeituraJson.Texto(ordem, "sha256").ToLowerInvariant();
        var servidor = LeituraJson.Texto(ordem, "servidor");
        var chavePublica = LeituraJson.Texto(ordem, "chavePublica");
        var temSenha = LeituraJson.Booleano(ordem, "temSenha");

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
                var nome = $"rustdesk-{versao}.exe";
                var instalador = await _baixador.BaixarAsync(nome, url, sha256, ct);
                await RustDesk.InstalarAsync(instalador, versao, ct);
                Log.Information("RustDesk {Versao} instalado", versao);
                Baixador.Limpar("rustdesk-*", new[] { nome });
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

    private static async Task ConfigurarAsync(string servidor, string chavePublica, CancellationToken ct)
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
}

public static class LeituraJson
{
    public static string Texto(JsonElement elemento, string nome) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    public static bool Booleano(JsonElement elemento, string nome) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.True;

    public static int Inteiro(JsonElement elemento, string nome, int padrao) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.Number && valor.TryGetInt32(out var numero)
            ? numero
            : padrao;
}
