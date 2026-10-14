using System.Security.Cryptography;
using System.Text;
using NpsShared.Configuration;
using Serilog;

namespace NpsShared.Utilities;

public static class SenhaLocal
{
    private const string HashPadrao = "sha256:809ef866022888be64e395b4b727cc9a:8a6ea031a3fcd4db2988d73a2d156271a14efe30d98925b981d4a45c594b82d4";
    private const int TentativasAntesDoBloqueio = 5;
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TempoBloqueio = TimeSpan.FromSeconds(30);
    private static readonly object Trava = new();
    private static DateTime _liberadoAte = DateTime.MinValue;
    private static DateTime _bloqueadoAte = DateTime.MinValue;
    private static int _erros;

    public static event Action<string, string>? Registro;

    public static bool Liberado
    {
        get
        {
            lock (Trava) return DateTime.UtcNow < _liberadoAte;
        }
    }

    public static TimeSpan ValidadeLiberacao => Validade;

    public static void Bloquear()
    {
        lock (Trava) _liberadoAte = DateTime.MinValue;
    }

    public static bool Conferir(string senha)
    {
        string hash;
        try
        {
            var configurado = ConfigManager.LoadConfig().SenhaLocalHash?.Trim();
            hash = FormatoValido(configurado) ? configurado! : HashPadrao;
        }
        catch
        {
            hash = HashPadrao;
        }

        return ConferirHash(hash, senha);
    }

    public static bool FormatoValido(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;
        var partes = hash.Split(':');
        return partes.Length == 3 && partes[0] == "sha256" && partes[1].Length > 0 && partes[2].Length == 64;
    }

    private static bool ConferirHash(string hash, string senha)
    {
        var partes = hash.Split(':');
        if (partes.Length != 3 || partes[0] != "sha256") return false;
        var calculado = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partes[1] + senha))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(calculado), Encoding.ASCII.GetBytes(partes[2].ToLowerInvariant()));
    }

    public static bool Solicitar(IWin32Window? dono, string acao)
    {
        lock (Trava)
        {
            if (DateTime.UtcNow < _liberadoAte)
            {
                _liberadoAte = DateTime.UtcNow.Add(Validade);
                return true;
            }
        }

        while (true)
        {
            TimeSpan espera;
            lock (Trava) espera = _bloqueadoAte - DateTime.UtcNow;
            if (espera > TimeSpan.Zero)
            {
                MessageBox.Show(dono,
                    $"Muitas tentativas com a senha errada. Aguarde {Math.Ceiling(espera.TotalSeconds)} segundos e tente de novo.",
                    "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            using var dialogo = new SenhaLocalForm(acao);
            if (dialogo.ShowDialog(dono) != DialogResult.OK) return false;

            if (Conferir(dialogo.Senha))
            {
                lock (Trava)
                {
                    _erros = 0;
                    _liberadoAte = DateTime.UtcNow.Add(Validade);
                }
                Log.Information("Acesso local liberado: {Acao}", acao);
                Registro?.Invoke("ACESSO_LOCAL_LIBERADO", acao);
                return true;
            }

            bool bloqueou;
            lock (Trava)
            {
                _erros++;
                bloqueou = _erros >= TentativasAntesDoBloqueio;
                if (bloqueou)
                {
                    _erros = 0;
                    _bloqueadoAte = DateTime.UtcNow.Add(TempoBloqueio);
                }
            }
            Log.Warning("Senha local incorreta: {Acao}", acao);
            Registro?.Invoke("ACESSO_LOCAL_NEGADO", acao);

            if (bloqueou) continue;
            MessageBox.Show(dono, "Senha incorreta.", "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private sealed class SenhaLocalForm : Form
    {
        private readonly TextBox _senha;

        public string Senha => _senha.Text;

        public SenhaLocalForm(string acao)
        {
            Text = "KioskMax - senha local";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(380, 150);
            Font = new Font("Segoe UI", 9F);

            var rotulo = new Label
            {
                Text = $"Digite a senha local para: {acao}",
                AutoSize = false,
                Location = new Point(14, 14),
                Size = new Size(352, 36)
            };

            _senha = new TextBox
            {
                Location = new Point(14, 56),
                Size = new Size(352, 23),
                UseSystemPasswordChar = true
            };

            var ok = new Button { Text = "Confirmar", DialogResult = DialogResult.OK, Location = new Point(200, 104), Size = new Size(80, 30) };
            var cancelar = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(286, 104), Size = new Size(80, 30) };

            Controls.AddRange(new Control[] { rotulo, _senha, ok, cancelar });
            AcceptButton = ok;
            CancelButton = cancelar;
            Shown += (_, _) =>
            {
                Activate();
                _senha.Focus();
            };
        }
    }
}
