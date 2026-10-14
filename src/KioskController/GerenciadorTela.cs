using System.Diagnostics;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Utilities;
using Serilog;

namespace KioskController;

public sealed class GerenciadorTela
{
    private readonly SemaphoreSlim _trava = new(1, 1);
    private Process? _processo;
    private bool _paradaSolicitada;

    public event Action<string, string>? Evento;
    public event Action<string>? SaiuInesperadamente;

    public bool EstaRodando => ExisteProcesso("KioskBrowser");

    public bool RoteadorRodando => ExisteProcesso("WindowRouter");

    private static bool ExisteProcesso(string nome)
    {
        try
        {
            var processos = Process.GetProcessesByName(nome);
            var existe = processos.Length > 0;
            foreach (var p in processos) p.Dispose();
            return existe;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IniciarAsync(bool comRoteador = true)
    {
        await _trava.WaitAsync();
        try
        {
            _paradaSolicitada = true;
            KioskProcessTakeover.KillAllWithImageName("KioskBrowser");
            KioskProcessTakeover.KillAllWithImageName("NpsDisplay");
            await Task.Delay(300);
            _processo = null;

            var caminho = ResolverCaminhoExe();
            Log.Information("Resolved KioskBrowser path: {Path}", caminho);

            if (!File.Exists(caminho))
            {
                Log.Error("Display executable not found: {Path}", caminho);
                Evento?.Invoke("ERRO_TELA", $"KioskBrowser.exe não encontrado em {caminho}");
                return false;
            }

            var processo = Process.Start(new ProcessStartInfo
            {
                FileName = caminho,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(caminho) ?? AppDomain.CurrentDomain.BaseDirectory
            });

            if (processo == null)
            {
                Evento?.Invoke("ERRO_TELA", "Falha ao iniciar o KioskBrowser");
                return false;
            }

            _processo = processo;
            _paradaSolicitada = false;
            Log.Information("Display process started: PID {PID}", processo.Id);

            processo.EnableRaisingEvents = true;
            processo.Exited += (s, e) => AoSair(processo);

            Evento?.Invoke("TELA_INICIADA", $"PID {processo.Id}");

            if (comRoteador)
            {
                IniciarRoteador();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start display");
            Evento?.Invoke("ERRO_TELA", ex.Message);
            return false;
        }
        finally
        {
            _trava.Release();
        }
    }

    private void AoSair(Process processo)
    {
        try
        {
            if (!ReferenceEquals(processo, _processo)) return;
            var codigo = processo.ExitCode;

            if (_paradaSolicitada)
            {
                Log.Information("Display process exited (requested) with code {Code}", codigo);
                return;
            }

            Log.Warning("Display process exited unexpectedly with code {Code}", codigo);
            Evento?.Invoke("TELA_FALHOU", $"KioskBrowser encerrou com código {codigo}");
            SaiuInesperadamente?.Invoke($"código {codigo}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error handling display exit");
        }
    }

    public async Task PararAsync(bool comRoteador = true)
    {
        await _trava.WaitAsync();
        try
        {
            var estavaRodando = EstaRodando;
            _paradaSolicitada = true;

            if (estavaRodando)
            {
                var resposta = await IpcClient.ShutdownAsync();
                if (resposta.Success)
                {
                    Log.Information("Display stopped via IPC");
                    var limite = DateTime.UtcNow.AddSeconds(4);
                    while (EstaRodando && DateTime.UtcNow < limite)
                    {
                        await Task.Delay(150);
                    }
                }
                else
                {
                    Log.Warning("Display shutdown command failed, attempting process kill");
                }

                if (EstaRodando)
                {
                    KioskProcessTakeover.KillAllWithImageName("KioskBrowser");
                }

                Evento?.Invoke("TELA_PARADA", "Tela encerrada");
            }

            _processo = null;

            if (comRoteador)
            {
                PararRoteador();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop display");
            Evento?.Invoke("ERRO_TELA", ex.Message);
        }
        finally
        {
            _trava.Release();
        }
    }

    public bool IniciarRoteador()
    {
        var ok = WindowRouterLifecycle.TryLaunchRouterIfEnabled(out _);
        if (!ok)
        {
            Log.Warning("Window Router is enabled in config but failed to start (exe missing or error — see logs)");
        }
        return ok;
    }

    public void PararRoteador()
    {
        WindowRouterLifecycle.StopRoutingProcesses();
    }

    public static bool ApagarCacheWebView()
    {
        var pasta = Path.Combine(ConfigManager.GetConfigDirectory(), "WebView2");
        for (var tentativa = 0; tentativa < 10; tentativa++)
        {
            try
            {
                if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("Cache do WebView2 em uso (tentativa {N}): {Msg}", tentativa + 1, ex.Message);
                Thread.Sleep(500);
            }
        }
        return false;
    }

    public static string ResolverCaminhoExe()
    {
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
        var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? runtimeBase;
        var exeDir = Path.GetDirectoryName(exePath) ?? runtimeBase;

        var sameDirPath = Path.Combine(exeDir, "KioskBrowser.exe");
        if (File.Exists(sameDirPath)) return sameDirPath;

        var rootsToSearch = new List<string>();
        void AddAncestors(string start)
        {
            try
            {
                var current = new DirectoryInfo(start);
                for (int i = 0; i < 8 && current != null; i++)
                {
                    rootsToSearch.Add(current.FullName);
                    current = current.Parent;
                }
            }
            catch { }
        }
        AddAncestors(exeDir);
        AddAncestors(runtimeBase);

        foreach (var root in rootsToSearch.Distinct())
        {
            var k1 = Path.Combine(root, "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k1)) return k1;

            var k2 = Path.Combine(root, "src", "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k2)) return k2;

            var k3 = Path.Combine(root, "KioskBrowser", "bin", "Debug", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k3)) return k3;

            var k4 = Path.Combine(root, "KioskBrowser", "net8.0-windows", "KioskBrowser.exe");
            if (File.Exists(k4)) return k4;

            var k5 = Path.Combine(root, "build", "publish", "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k5)) return k5;

            var k6 = Path.Combine(root, "temp-run", "KioskBrowser", "KioskBrowser.exe");
            if (File.Exists(k6)) return k6;

            var n1 = Path.Combine(root, "NpsDisplay", "NpsDisplay.exe");
            if (File.Exists(n1)) return n1;

            var n2 = Path.Combine(root, "src", "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n2)) return n2;

            var n3 = Path.Combine(root, "NpsDisplay", "bin", "Debug", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n3)) return n3;

            var n4 = Path.Combine(root, "NpsDisplay", "net8.0-windows", "NpsDisplay.exe");
            if (File.Exists(n4)) return n4;
        }

        foreach (var root in rootsToSearch.Distinct())
        {
            try
            {
                var found = Directory.EnumerateFiles(root, "KioskBrowser.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found!;
            }
            catch { }
            try
            {
                var legacy = Directory.EnumerateFiles(root, "NpsDisplay.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p.Contains("net8.0-windows"))
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(legacy)) return legacy!;
            }
            catch { }
        }

        return sameDirPath;
    }
}
