using NpsShared.Utilities;
using KioskTef;
using NpsShared.Configuration;
using Serilog;

namespace KioskController;

public partial class ControllerForm
{
    private GroupBox _grpGestao = null!;
    private TextBox _txtGestaoUrl = null!;
    private TextBox _txtIdentificador = null!;
    private Label _lblSituacaoGestao = null!;
    private Label _lblTef = null!;
    private ComboBox _cmbImpressora = null!;
    private ComboBox _cmbModoImpressao = null!;
    private CheckBox _chkViaCliente = null!;
    private CheckBox _chkViaEstabelecimento = null!;
    private CheckBox _chkCortarPapel = null!;
    private Button _btnSalvarImpressao = null!;
    private Label _lblControladoGestao = null!;
    private bool _impressaoCarregada;
    private bool _impressaoControlada;

    private void InitializeGestaoGroup()
    {
        _grpGestao = new GroupBox
        {
            Text = "Gestão / TEF",
            Location = new Point(16, 320),
            Size = new Size(528, 284)
        };

        var lblUrl = new Label { AutoSize = true, Text = "URL do Gestão:", Location = new Point(10, 28) };
        _txtGestaoUrl = new TextBox { Size = new Size(318, 27), Location = new Point(110, 24), Text = _config.GestaoApiUrl };
        var btnSalvarUrl = new Button { Text = "Salvar", Size = new Size(70, 28), Location = new Point(440, 23) };
        btnSalvarUrl.Click += BtnSalvarGestaoUrl_Click;

        var lblId = new Label { AutoSize = true, Text = "Identificador:", Location = new Point(10, 62) };
        _txtIdentificador = new TextBox { Size = new Size(318, 27), Location = new Point(110, 58), ReadOnly = true, Text = _config.KioskIdentificador };
        var btnCopiar = new Button { Text = "Copiar", Size = new Size(70, 28), Location = new Point(440, 57) };
        btnCopiar.Click += (_, _) =>
        {
            try { Clipboard.SetText(_txtIdentificador.Text); } catch { }
        };

        _lblSituacaoGestao = new Label { AutoSize = false, Size = new Size(508, 54), Location = new Point(10, 92) };
        _lblTef = new Label { AutoSize = false, Size = new Size(508, 20), Location = new Point(10, 150) };

        var lblImpressora = new Label { AutoSize = true, Text = "Impressora:", Location = new Point(10, 184) };
        _cmbImpressora = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Size = new Size(250, 28), Location = new Point(90, 180) };
        var lblModo = new Label { AutoSize = true, Text = "Modo:", Location = new Point(352, 184) };
        _cmbModoImpressao = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Size = new Size(110, 28), Location = new Point(400, 180) };
        _cmbModoImpressao.Items.AddRange(new object[] { "RAW", "Driver" });

        _chkViaCliente = new CheckBox { AutoSize = true, Text = "Via do cliente", Location = new Point(10, 216) };
        _chkViaEstabelecimento = new CheckBox { AutoSize = true, Text = "Via do estabelecimento", Location = new Point(130, 216) };
        _chkCortarPapel = new CheckBox { AutoSize = true, Text = "Cortar papel", Location = new Point(310, 216) };

        _btnSalvarImpressao = new Button { Text = "Salvar impressão", Size = new Size(130, 30), Location = new Point(10, 246) };
        _btnSalvarImpressao.Click += BtnSalvarImpressao_Click;
        var btnTeste = new Button { Text = "Imprimir teste TEF", Size = new Size(140, 30), Location = new Point(150, 246) };
        btnTeste.Click += BtnImprimirTeste_Click;
        _lblControladoGestao = new Label { AutoSize = true, Text = "Definido pelo Gestão", ForeColor = Color.DarkRed, Location = new Point(304, 253), Visible = false };

        _grpGestao.Controls.AddRange(new Control[]
        {
            lblUrl, _txtGestaoUrl, btnSalvarUrl, lblId, _txtIdentificador, btnCopiar,
            _lblSituacaoGestao, _lblTef, lblImpressora, _cmbImpressora, lblModo, _cmbModoImpressao,
            _chkViaCliente, _chkViaEstabelecimento, _chkCortarPapel, _btnSalvarImpressao, btnTeste, _lblControladoGestao
        });
        Controls.Add(_grpGestao);

        try
        {
            foreach (var nome in ServicoTef.ListarImpressoras()) _cmbImpressora.Items.Add(nome);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao listar impressoras");
        }

        AtualizarGestao();
    }

    private void CarregarImpressaoNaTela(ConfiguracaoImpressao config)
    {
        _cmbImpressora.Text = config.Impressora;
        _cmbModoImpressao.SelectedIndex = config.ModoDriver ? 1 : 0;
        _chkViaCliente.Checked = config.ViaCliente;
        _chkViaEstabelecimento.Checked = config.ViaEstabelecimento;
        _chkCortarPapel.Checked = config.CortarPapel;
    }

    private void AtualizarGestao()
    {
        if (_grpGestao == null) return;

        var controlado = _agente.ControladoPeloGestao;

        var linhas = new List<string>
        {
            $"Situação: {TrayApplicationContext.TextoGestao(_agente)}"
                + (string.IsNullOrEmpty(_agente.MensagemEstado) ? "" : $" - {_agente.MensagemEstado}"),
            "Último sinal: " + (_agente.UltimoSinalEm.HasValue ? _agente.UltimoSinalEm.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") : "-"),
            "Caixa: " + (string.IsNullOrEmpty(_agente.DescricaoCaixa) ? "-" : _agente.DescricaoCaixa)
                + "   Tela: " + (string.IsNullOrEmpty(_agente.NomeTela) ? "-" : _agente.NomeTela)
        };
        _lblSituacaoGestao.Text = string.Join("\n", linhas);
        _lblTef.Text = "TEF: " + TrayApplicationContext.TextoTef(_agente);

        if (!_impressaoCarregada || _impressaoControlada != controlado)
        {
            _impressaoCarregada = true;
            _impressaoControlada = controlado;
            CarregarImpressaoNaTela(_agente.Tef.Impressao);
        }

        _cmbImpressora.Enabled = !controlado;
        _cmbModoImpressao.Enabled = !controlado;
        _chkViaCliente.Enabled = !controlado;
        _chkViaEstabelecimento.Enabled = !controlado;
        _chkCortarPapel.Enabled = !controlado;
        _btnSalvarImpressao.Enabled = !controlado;
        _lblControladoGestao.Visible = controlado;
    }

    private void BtnSalvarGestaoUrl_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "alterar o endereço do Gestão")) return;
        var url = _txtGestaoUrl.Text.Trim().TrimEnd('/');
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            MessageBox.Show("Informe uma URL http(s) válida.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var atual = (ConfigManager.LoadConfig().GestaoApiUrl ?? string.Empty).TrimEnd('/');
        if (!string.Equals(url, atual, StringComparison.Ordinal))
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
        AtualizarGestao();
    }

    private void BtnSalvarImpressao_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "alterar a impressão do TEF")) return;
        try
        {
            var config = new ConfiguracaoImpressao
            {
                Impressora = string.IsNullOrWhiteSpace(_cmbImpressora.Text) ? "IMPRESSORATEF" : _cmbImpressora.Text.Trim(),
                ModoDriver = _cmbModoImpressao.SelectedIndex == 1,
                ViaCliente = _chkViaCliente.Checked,
                ViaEstabelecimento = _chkViaEstabelecimento.Checked,
                CortarPapel = _chkCortarPapel.Checked
            };
            config.SalvarIni();
            _agente.Tef.DefinirImpressao(config);
            MessageBox.Show("Configuração de impressão salva.", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao salvar a configuração de impressão");
            MessageBox.Show($"Falha ao salvar: {ex.Message}", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async void BtnImprimirTeste_Click(object? sender, EventArgs e)
    {
        if (!SenhaLocal.Solicitar(this, "imprimir o teste do TEF")) return;
        try
        {
            await Task.Run(() => _agente.Tef.ImprimirTeste());
            MessageBox.Show("Teste de impressão enviado.", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao imprimir o teste TEF");
            MessageBox.Show($"Falha ao imprimir o teste: {ex.Message}", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
