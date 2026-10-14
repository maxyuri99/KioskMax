using NpsShared.Utilities;
using NpsShared.Configuration;

namespace KioskController;

public sealed class VinculoGestaoForm : Form
{
    private readonly AgenteKiosk _agente;
    private readonly TextBox _txtUrl;
    private readonly TextBox _txtIdentificador;
    private readonly Label _lblSituacao;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _urlOriginal;

    public VinculoGestaoForm(AgenteKiosk agente)
    {
        _agente = agente;
        var cfg = ConfigManager.LoadConfig();
        _urlOriginal = cfg.GestaoApiUrl ?? string.Empty;

        Text = "KioskMax - Vínculo com o Gestão";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 220);

        var lblUrl = new Label { AutoSize = true, Text = "URL da API do Gestão:", Location = new Point(16, 16) };
        _txtUrl = new TextBox { Size = new Size(488, 27), Location = new Point(16, 38), Text = _urlOriginal };

        var lblId = new Label { AutoSize = true, Text = "Identificador deste kiosk:", Location = new Point(16, 76) };
        _txtIdentificador = new TextBox { Size = new Size(400, 27), Location = new Point(16, 98), ReadOnly = true, Text = cfg.KioskIdentificador };
        var btnCopiar = new Button { Text = "Copiar", Size = new Size(80, 28), Location = new Point(424, 97) };
        btnCopiar.Click += (_, _) =>
        {
            try { Clipboard.SetText(_txtIdentificador.Text); } catch { }
        };

        _lblSituacao = new Label { AutoSize = false, Size = new Size(488, 40), Location = new Point(16, 136), Text = "" };

        var btnSalvar = new Button { Text = "Salvar", Size = new Size(100, 30), Location = new Point(304, 180) };
        btnSalvar.Click += BtnSalvar_Click;
        var btnFechar = new Button { Text = "Fechar", Size = new Size(100, 30), Location = new Point(412, 180), DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] { lblUrl, _txtUrl, lblId, _txtIdentificador, btnCopiar, _lblSituacao, btnSalvar, btnFechar });
        AcceptButton = btnSalvar;
        CancelButton = btnFechar;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => AtualizarSituacao();
        _timer.Start();
        AtualizarSituacao();
    }

    private void AtualizarSituacao()
    {
        var texto = TrayApplicationContext.TextoGestao(_agente);
        var detalhe = string.IsNullOrEmpty(_agente.MensagemEstado) ? string.Empty : $" - {_agente.MensagemEstado}";
        var ultimo = _agente.UltimoSinalEm.HasValue ? $"\nÚltimo sinal: {_agente.UltimoSinalEm.Value.LocalDateTime:dd/MM/yyyy HH:mm:ss}" : string.Empty;
        _lblSituacao.Text = $"Situação: {texto}{detalhe}{ultimo}";
    }

    private void BtnSalvar_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "alterar o endereço do Gestão")) return;
        var url = _txtUrl.Text.Trim().TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            MessageBox.Show("Informe uma URL http(s) válida.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!string.Equals(url, _urlOriginal.TrimEnd('/'), StringComparison.Ordinal))
        {
            ConfigManager.Update(c =>
            {
                c.GestaoApiUrl = url;
                c.UltimaSituacao = string.Empty;
                c.UltimaVersaoConfig = 0;
            });
            _agente.EsquecerToken();
        }

        _agente.AcordarAgora();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
