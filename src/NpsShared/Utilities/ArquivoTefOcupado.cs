using NpsShared.Configuration;
using Serilog;

namespace NpsShared.Utilities;

public static class ArquivoTefOcupado
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(15);
    private static readonly object Trava = new();

    private static string Caminho => Path.Combine(ConfigManager.GetConfigDirectory(), "tef.ocupado");

    public static void Marcar(bool ocupado)
    {
        lock (Trava)
        {
            try
            {
                if (ocupado)
                {
                    Directory.CreateDirectory(ConfigManager.GetConfigDirectory());
                    File.WriteAllText(Caminho, DateTimeOffset.Now.ToString("o"));
                }
                else if (File.Exists(Caminho))
                {
                    File.Delete(Caminho);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao registrar o estado do TEF para o serviço do KioskMax");
            }
        }
    }

    public static bool Ocupado()
    {
        try
        {
            var arquivo = new FileInfo(Caminho);
            return arquivo.Exists && DateTime.UtcNow - arquivo.LastWriteTimeUtc < Validade;
        }
        catch
        {
            return true;
        }
    }
}
