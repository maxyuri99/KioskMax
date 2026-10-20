using NpsShared.Utilities;
using KioskTef;
using NpsShared.Configuration;
using Serilog;

namespace KioskController;

public partial class ControllerForm
{
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

    private TabPage CriarAbaGestao()
    {
        var grade = NovaGrade();

        _txtGestaoUrl = new TextBox { Dock = DockStyle.Fill, Text = _config.GestaoApiUrl };
        var btnSalvarUrl = NovoBotao("Salvar");
        btnSalvarUrl.Click += BtnSalvarGestaoUrl_Click;
        Campo(grade, "Endereço do Gestão:", _txtGestaoUrl, btnSalvarUrl,
            "Endereço da API do Gestão que controla este kiosk (ex.: https://gestaov2.upmais.com.br/api). " +
            "Em branco, o kiosk funciona sozinho, só com o que está configurado neste computador (modo local).");

        _txtIdentificador = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = _config.KioskIdentificador };
        var btnCopiar = NovoBotao("Copiar");
        btnCopiar.Click += (_, _) =>
        {
            try { Clipboard.SetText(_txtIdentificador.Text); } catch { }
        };
        Campo(grade, "Identificador:", _txtIdentificador, btnCopiar,
            "Código único deste computador. No Gestão (Ajustes > Dispositivos > Kiosks), " +
            "procure por ele para aprovar o kiosk e escolher o caixa e a tela que ele mostra.");

        Secao(grade, "Situação do vínculo");
        _lblSituacaoGestao = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.WhiteSmoke,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(3, 0, 3, 6)
        };
        var linha = NovaLinha(grade);
        grade.Controls.Add(_lblSituacaoGestao, 0, linha);
        grade.SetColumnSpan(_lblSituacaoGestao, 3);
        Dica(grade,
            "Online: o kiosk conversa com o Gestão e segue o que foi definido lá. " +
            "Aguardando aprovação: falta aprovar o identificador no Gestão. " +
            "Offline: sem resposta do Gestão; o kiosk continua com a última configuração recebida.");

        return NovaAba("Gestão", grade);
    }

    private TabPage CriarAbaTef()
    {
        var grade = NovaGrade();

        Secao(grade, "Situação do TEF");
        _lblTef = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.WhiteSmoke,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(3, 0, 3, 6)
        };
        var linha = NovaLinha(grade);
        grade.Controls.Add(_lblTef, 0, linha);
        grade.SetColumnSpan(_lblTef, 3);
        Dica(grade,
            "O KioskMax recebe os pagamentos com cartão do PDV e conversa com o pinpad pelo SiTef (Web Connection). " +
            "Com transação em andamento, não feche o KioskMax.");

        Secao(grade, "Comprovante do cartão");

        _cmbImpressora = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        Campo(grade, "Impressora:", _cmbImpressora, null,
            "Impressora onde sai o comprovante do TEF. Pode escolher da lista ou digitar o nome exato do Windows.");

        _cmbModoImpressao = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        // A ordem importa: índice 0 = RAW, 1 = Driver.
        _cmbModoImpressao.Items.AddRange(new object[] { "RAW (direto na impressora térmica)", "Driver do Windows" });
        Campo(grade, "Modo de impressão:", _cmbModoImpressao, null,
            "RAW manda o texto direto para a impressora térmica (mais rápido). " +
            "Use Driver se o comprovante sair em branco ou com caracteres estranhos.");

        _chkViaCliente = new CheckBox { AutoSize = true, Text = "Via do cliente", Margin = new Padding(0, 3, 16, 3) };
        _chkViaEstabelecimento = new CheckBox { AutoSize = true, Text = "Via do estabelecimento", Margin = new Padding(0, 3, 16, 3) };
        _chkCortarPapel = new CheckBox { AutoSize = true, Text = "Cortar papel", Margin = new Padding(0, 3, 16, 3) };
        var vias = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0) };
        vias.Controls.AddRange(new Control[] { _chkViaCliente, _chkViaEstabelecimento, _chkCortarPapel });
        Campo(grade, "Imprimir:", vias, null,
            "Quais vias do comprovante saem na impressora e se ela corta o papel no final.");

        _btnSalvarImpressao = NovoBotao("Salvar impressão");
        _btnSalvarImpressao.Margin = new Padding(0, 2, 6, 2);
        _btnSalvarImpressao.Click += BtnSalvarImpressao_Click;
        var btnTeste = NovoBotao("Imprimir teste");
        btnTeste.Margin = new Padding(0, 2, 6, 2);
        btnTeste.Click += BtnImprimirTeste_Click;
        _lblControladoGestao = new Label
        {
            AutoSize = true,
            Text = "Definido pelo Gestão: altere por lá.",
            ForeColor = Color.DarkRed,
            Margin = new Padding(6, 8, 0, 0),
            Visible = false
        };
        var acoes = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 0) };
        acoes.Controls.AddRange(new Control[] { _btnSalvarImpressao, btnTeste, _lblControladoGestao });
        linha = NovaLinha(grade);
        grade.Controls.Add(acoes, 0, linha);
        grade.SetColumnSpan(acoes, 3);

        try
        {
            foreach (var nome in ServicoTef.ListarImpressoras()) _cmbImpressora.Items.Add(nome);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao listar impressoras");
        }

        return NovaAba("TEF e impressão", grade);
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
        if (_lblSituacaoGestao == null) return;

        var controlado = _agente.ControladoPeloGestao;
        var textoGestao = TrayApplicationContext.TextoGestao(_agente);
        var textoTef = TrayApplicationContext.TextoTef(_agente);

        var linhas = new List<string>
        {
            $"Situação: {textoGestao}"
                + (string.IsNullOrEmpty(_agente.MensagemEstado) ? "" : $" - {_agente.MensagemEstado}"),
            "Último sinal: " + (_agente.UltimoSinalEm.HasValue ? _agente.UltimoSinalEm.Value.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") : "-"),
            "Caixa: " + (string.IsNullOrEmpty(_agente.DescricaoCaixa) ? "-" : _agente.DescricaoCaixa),
            "Tela: " + (string.IsNullOrEmpty(_agente.NomeTela) ? "-" : _agente.NomeTela)
        };
        _lblSituacaoGestao.Text = string.Join("\n", linhas);
        _lblTef.Text = "TEF: " + textoTef;

        lblResumoGestao.Text = "● Gestão: " + (_agente.Estado == EstadoGestao.Desativado ? "modo local" : textoGestao);
        lblResumoGestao.ForeColor = _agente.Estado switch
        {
            EstadoGestao.Online => CorOk,
            EstadoGestao.Offline or EstadoGestao.Bloqueado or EstadoGestao.Recusado => CorErro,
            EstadoGestao.Desativado => SystemColors.GrayText,
            _ => CorAviso
        };
        lblResumoTef.Text = "● TEF: " + textoTef;
        lblResumoTef.ForeColor = textoTef.StartsWith("ativo", StringComparison.Ordinal) ? CorOk
            : textoTef == "desativado" ? SystemColors.GrayText
            : textoTef == "iniciando..." ? CorAviso
            : CorErro;

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

            if (ConfigManager.UltimoErroGravacao is { } erro)
            {
                MessageBox.Show("Não foi possível salvar a configuração do KioskMax:\n" + erro +
                                "\n\nDê permissão de alteração aos usuários na pasta " + ConfigManager.GetConfigDirectory() +
                                " (ou reinstale a versão mais nova do KioskMax) e tente de novo.",
                                "KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

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
