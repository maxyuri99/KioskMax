using System.Runtime.InteropServices;
using System.Text;

namespace KioskTef;

public sealed class ConfiguracaoImpressao
{
    public string Impressora { get; set; } = "";
    public bool ModoDriver { get; set; }
    public bool ViaCliente { get; set; } = true;
    public bool ViaEstabelecimento { get; set; } = true;
    public bool CortarPapel { get; set; } = true;

    public static string PastaPadrao =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TEFManagerUP");

    public static string ArquivoIni => Path.Combine(PastaPadrao, "TEFManager.ini");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPrivateProfileStringW")]
    private static extern int GetPrivateProfileString(string secao, string chave, string padrao, StringBuilder retorno, int tamanho, string arquivo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WritePrivateProfileStringW", SetLastError = true)]
    private static extern bool WritePrivateProfileString(string secao, string chave, string valor, string arquivo);

    private static string Ler(string arquivo, string chave, string padrao)
    {
        var sb = new StringBuilder(1024);
        GetPrivateProfileString("Impressao", chave, padrao, sb, sb.Capacity, arquivo);
        return sb.ToString();
    }

    private static int LerInteiro(string arquivo, string chave, int padrao)
    {
        var s = Ler(arquivo, chave, padrao.ToString());
        return int.TryParse(s.Trim(), out var v) ? v : padrao;
    }

    private static bool LerBool(string arquivo, string chave, bool padrao)
    {
        return LerInteiro(arquivo, chave, padrao ? 1 : 0) != 0;
    }

    public static ConfiguracaoImpressao CarregarIni()
    {
        var arquivo = ArquivoIni;
        var config = new ConfiguracaoImpressao();

        var impressora = Ler(arquivo, "Impressora", "");
        config.ModoDriver = LerInteiro(arquivo, "Modo", 0) == 1;
        config.ViaCliente = LerBool(arquivo, "ViaCliente", true);
        config.ViaEstabelecimento = LerBool(arquivo, "ViaEstabelecimento", true);
        config.CortarPapel = LerBool(arquivo, "CortarPapel", true);

        if (impressora == "")
            impressora = "IMPRESSORATEF";

        config.Impressora = impressora;
        return config;
    }

    public void SalvarIni()
    {
        try
        {
            var arquivo = ArquivoIni;
            Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
            WritePrivateProfileString("Impressao", "Impressora", Impressora, arquivo);
            WritePrivateProfileString("Impressao", "Modo", ModoDriver ? "1" : "0", arquivo);
            WritePrivateProfileString("Impressao", "ViaCliente", ViaCliente ? "1" : "0", arquivo);
            WritePrivateProfileString("Impressao", "ViaEstabelecimento", ViaEstabelecimento ? "1" : "0", arquivo);
            WritePrivateProfileString("Impressao", "CortarPapel", CortarPapel ? "1" : "0", arquivo);
        }
        catch
        {
        }
    }
}
