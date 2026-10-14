using System.Security.Cryptography;
using System.Text;
using NpsShared.Configuration;
using Serilog;

namespace NpsShared.Utilities;

public static class KioskToken
{
    private static string CaminhoArquivo => Path.Combine(ConfigManager.GetConfigDirectory(), "kiosk.token");

    public static string? Ler()
    {
        try
        {
            if (!File.Exists(CaminhoArquivo)) return null;
            var protegido = File.ReadAllBytes(CaminhoArquivo);
            var bytes = ProtectedData.Unprotect(protegido, null, DataProtectionScope.LocalMachine);
            var token = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao ler o token do kiosk");
            return null;
        }
    }

    public static bool Salvar(string token)
    {
        try
        {
            Directory.CreateDirectory(ConfigManager.GetConfigDirectory());
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(CaminhoArquivo, bytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao gravar o token do kiosk");
            return false;
        }
    }

    public static void Apagar()
    {
        try
        {
            if (File.Exists(CaminhoArquivo)) File.Delete(CaminhoArquivo);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao apagar o token do kiosk");
        }
    }
}
