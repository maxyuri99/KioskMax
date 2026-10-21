using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace NpsWatchdog;

public static class RustDesk
{
    public const string NomeServico = "RustDesk";

    private static readonly string PastaPrograma = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk");
    private static readonly string ArquivoConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "ServiceProfiles", "LocalService", "AppData", "Roaming", "RustDesk", "config", "RustDesk2.toml");

    public static string Executavel => Path.Combine(PastaPrograma, "rustdesk.exe");

    public static string? VersaoInstalada()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk");
            if (chave?.GetValue("DisplayVersion") is string versao && !string.IsNullOrWhiteSpace(versao) && File.Exists(Executavel))
                return versao.Trim();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Falha ao ler a versão do RustDesk no registro");
        }

        if (!File.Exists(Executavel)) return null;
        var produto = FileVersionInfo.GetVersionInfo(Executavel).ProductVersion ?? string.Empty;
        var limpa = produto.Split('+')[0].Trim();
        return string.IsNullOrEmpty(limpa) ? null : limpa;
    }

    public static bool ServicoRodando()
    {
        try
        {
            using var servico = new ServiceController(NomeServico);
            return servico.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public static Dictionary<string, string> LerOpcoes()
    {
        var opcoes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ArquivoConfig)) return opcoes;
            var naSecao = false;
            foreach (var bruta in File.ReadAllLines(ArquivoConfig))
            {
                var linha = bruta.Trim();
                if (linha.StartsWith('['))
                {
                    naSecao = string.Equals(linha, "[options]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!naSecao) continue;
                var igual = linha.IndexOf('=');
                if (igual <= 0) continue;
                var nome = linha[..igual].Trim();
                var valor = linha[(igual + 1)..].Trim().Trim('\'', '"');
                opcoes[nome] = valor;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao ler a configuração do RustDesk");
        }
        return opcoes;
    }

    public static async Task<(int Codigo, string Saida)> ExecutarAsync(IReadOnlyList<string> argumentos, TimeSpan limite, CancellationToken ct)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = Executavel,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = PastaPrograma
        };
        foreach (var argumento in argumentos) inicio.ArgumentList.Add(argumento);

        using var processo = Process.Start(inicio) ?? throw new InvalidOperationException("Não foi possível iniciar o RustDesk");
        var leitura = processo.StandardOutput.ReadToEndAsync(ct);
        _ = processo.StandardError.ReadToEndAsync(ct);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(limite);
        try
        {
            await processo.WaitForExitAsync(prazo.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { processo.Kill(true); } catch { }
            throw new System.TimeoutException($"O RustDesk não respondeu em {limite.TotalSeconds:0} s");
        }
        var saida = await leitura;
        return (processo.ExitCode, saida.Trim());
    }

    public static async Task<string> LerIdAsync(CancellationToken ct)
    {
        var (_, saida) = await ExecutarAsync(new[] { "--get-id" }, TimeSpan.FromSeconds(30), ct);
        var id = saida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        return id.All(char.IsLetterOrDigit) ? id : string.Empty;
    }

    public static Task DefinirOpcaoAsync(string nome, string valor, CancellationToken ct) =>
        ExecutarAsync(new[] { "--option", nome, valor }, TimeSpan.FromSeconds(30), ct);

    public static async Task<bool> DefinirSenhaAsync(string senha, CancellationToken ct)
    {
        var (_, saida) = await ExecutarAsync(new[] { "--password", senha }, TimeSpan.FromSeconds(30), ct);
        return saida.Contains("Done", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task InstalarAsync(string instalador, string versao, CancellationToken ct)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = instalador,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(instalador) ?? AppContext.BaseDirectory
        };
        inicio.ArgumentList.Add("--silent-install");

        using var processo = Process.Start(inicio) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador do RustDesk");
        var limite = DateTime.UtcNow.AddMinutes(5);
        try
        {
            while (DateTime.UtcNow < limite)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (string.Equals(VersaoInstalada(), versao, StringComparison.Ordinal) && ServicoRodando())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return;
                }
            }
            throw new System.TimeoutException("A instalação do RustDesk não terminou em 5 minutos");
        }
        finally
        {
            try
            {
                if (!processo.HasExited) processo.Kill();
            }
            catch { }
        }
    }
}
