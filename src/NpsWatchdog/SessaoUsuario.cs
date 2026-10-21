using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace NpsWatchdog;

public static class SessaoUsuario
{
    private const uint SemSessao = 0xFFFFFFFF;
    private const uint CriarAmbienteUnicode = 0x00000400;
    private const int TokenPrimario = 1;
    private const int PersonificacaoNivel = 2;
    private const uint AcessoTotalToken = 0xF01FF;

    public static uint? SessaoAtiva()
    {
        var sessao = WTSGetActiveConsoleSessionId();
        if (sessao == SemSessao || sessao == 0) return null;
        return WTSQueryUserToken(sessao, out var token) ? FecharE(token, sessao) : null;
    }

    private static uint? FecharE(IntPtr token, uint sessao)
    {
        CloseHandle(token);
        return sessao;
    }

    public static bool Rodando(string nomeProcesso, uint sessao)
    {
        foreach (var processo in Process.GetProcessesByName(nomeProcesso))
        {
            using (processo)
            {
                try
                {
                    if ((uint)processo.SessionId == sessao) return true;
                }
                catch
                {
                }
            }
        }
        return false;
    }

    public static void Encerrar(params string[] nomes)
    {
        foreach (var nome in nomes)
        {
            foreach (var processo in Process.GetProcessesByName(nome))
            {
                using (processo)
                {
                    try
                    {
                        processo.Kill(true);
                        processo.WaitForExit(10000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Falha ao encerrar {Processo}: {Erro}", nome, ex.Message);
                    }
                }
            }
        }
    }

    public static bool Abrir(string executavel)
    {
        var sessao = WTSGetActiveConsoleSessionId();
        if (sessao == SemSessao || sessao == 0) return false;
        if (!WTSQueryUserToken(sessao, out var tokenUsuario)) return false;

        var tokenPrimario = IntPtr.Zero;
        var ambiente = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(tokenUsuario, AcessoTotalToken, IntPtr.Zero, PersonificacaoNivel, TokenPrimario, out tokenPrimario))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out ambiente, tokenPrimario, false))
                ambiente = IntPtr.Zero;

            var inicio = new InformacaoInicio { cb = Marshal.SizeOf<InformacaoInicio>(), lpDesktop = @"winsta0\default" };
            var pasta = Path.GetDirectoryName(executavel);
            var linha = $"\"{executavel}\"";
            if (!CreateProcessAsUser(tokenPrimario, null, linha, IntPtr.Zero, IntPtr.Zero, false,
                    CriarAmbienteUnicode, ambiente, pasta, ref inicio, out var processo))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            CloseHandle(processo.hThread);
            CloseHandle(processo.hProcess);
            Log.Information("{Programa} aberto na sessão {Sessao} do operador", Path.GetFileName(executavel), sessao);
            return true;
        }
        finally
        {
            if (ambiente != IntPtr.Zero) DestroyEnvironmentBlock(ambiente);
            if (tokenPrimario != IntPtr.Zero) CloseHandle(tokenPrimario);
            CloseHandle(tokenUsuario);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InformacaoInicio
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InformacaoProcesso
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref InformacaoInicio startupInfo, out InformacaoProcesso processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
