using System.Diagnostics;
using NpsShared.Configuration;
using Serilog;

namespace NpsShared.Utilities;

public static class SaidaSolicitada
{
    private static string Caminho => Path.Combine(ConfigManager.GetConfigDirectory(), "saida.solicitada");

    public static void Marcar()
    {
        try
        {
            Directory.CreateDirectory(ConfigManager.GetConfigDirectory());
            using var processo = Process.GetCurrentProcess();
            File.WriteAllText(Caminho, processo.SessionId.ToString());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao registrar a saída pedida pelo operador");
        }
    }

    public static void Limpar()
    {
        try
        {
            if (File.Exists(Caminho)) File.Delete(Caminho);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao apagar o registro de saída pedida pelo operador");
        }
    }

    public static bool NaSessao(uint sessao)
    {
        try
        {
            return File.Exists(Caminho) && uint.TryParse(File.ReadAllText(Caminho).Trim(), out var marcada) && marcada == sessao;
        }
        catch
        {
            return true;
        }
    }
}
