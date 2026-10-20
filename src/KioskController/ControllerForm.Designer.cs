#nullable enable

using System.Reflection;

namespace KioskController;

partial class ControllerForm
{
    private System.ComponentModel.IContainer components = null!;
    private Label lblStatus = null!;
    private Label lblResumoGestao = null!;
    private Label lblResumoTef = null!;
    private Button btnStartStop = null!;
    private Button btnReload = null!;
    private TextBox txtUrl = null!;
    private Button btnApplyUrl = null!;
    private ComboBox cmbMonitors = null!;
    private CheckBox chkRouter = null!;
    private ComboBox cmbRouterFilter = null!;
    private CheckBox chkRouterDebugLogging = null!;
    private Button btnOpenLogs = null!;
    private Button btnOpenConfig = null!;
    private LinkLabel lnkAuthor = null!;
    private TabControl tabs = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        // Tudo em painéis de layout e escala por DPI: em telas com zoom do Windows (125%, 150%...)
        // os campos crescem junto com o texto em vez de ficarem uns por cima dos outros.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        Text = "KioskMax - Painel de controle";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(660, 640);

        var raiz = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        raiz.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        raiz.Controls.Add(CriarCabecalho(), 0, 0);

        tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(12, 10, 12, 0), Padding = new Point(12, 5) };
        tabs.TabPages.Add(CriarAbaTela());
        tabs.TabPages.Add(CriarAbaJanelas());
        tabs.TabPages.Add(CriarAbaGestao());
        tabs.TabPages.Add(CriarAbaTef());
        raiz.Controls.Add(tabs, 0, 1);

        raiz.Controls.Add(CriarRodape(), 0, 2);

        Controls.Add(raiz);
        ResumeLayout(false);
        PerformLayout();
    }

    private Control CriarCabecalho()
    {
        var cabecalho = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 12),
            Margin = Padding.Empty
        };
        cabecalho.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cabecalho.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titulo = new Label
        {
            AutoSize = true,
            Text = "KioskMax",
            Font = new Font("Segoe UI Semibold", 15F),
            Margin = new Padding(0)
        };
        var subtitulo = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Text = "Painel de controle do kiosk deste computador. As alterações pedem a senha local.",
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(1, 0, 0, 8)
        };

        btnStartStop = new Button { Text = "Iniciar tela", AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(0, 0, 6, 0) };
        btnStartStop.Click += btnStartStop_Click;
        btnReload = new Button { Text = "Recarregar página", AutoSize = true, MinimumSize = new Size(130, 32), Margin = Padding.Empty };
        btnReload.Click += btnReload_Click;
        var botoes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty };
        botoes.Controls.AddRange(new Control[] { btnStartStop, btnReload });

        lblStatus = CriarIndicador();
        lblStatus.Text = "● Tela: verificando...";
        lblResumoGestao = CriarIndicador();
        lblResumoTef = CriarIndicador();
        var indicadores = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 2, 0, 0) };
        indicadores.Controls.AddRange(new Control[] { lblStatus, lblResumoGestao, lblResumoTef });

        cabecalho.Controls.Add(titulo, 0, 0);
        cabecalho.Controls.Add(botoes, 1, 0);
        cabecalho.SetRowSpan(botoes, 2);
        cabecalho.Controls.Add(subtitulo, 0, 1);
        cabecalho.Controls.Add(indicadores, 0, 2);
        cabecalho.SetColumnSpan(indicadores, 2);

        cabecalho.Paint += (_, e) =>
            e.Graphics.DrawLine(Pens.Gainsboro, 0, cabecalho.Height - 1, cabecalho.Width, cabecalho.Height - 1);
        return cabecalho;
    }

    private static Label CriarIndicador() => new()
    {
        AutoSize = true,
        BackColor = Color.WhiteSmoke,
        Padding = new Padding(8, 4, 8, 4),
        Margin = new Padding(0, 0, 8, 0),
        Text = "..."
    };

    private TabPage CriarAbaTela()
    {
        var grade = NovaGrade();

        txtUrl = new TextBox { Dock = DockStyle.Fill };
        btnApplyUrl = NovoBotao("Aplicar");
        btnApplyUrl.Click += btnApplyUrl_Click;
        Campo(grade, "Endereço da página:", txtUrl, btnApplyUrl,
            "Página que o kiosk abre em tela cheia (por exemplo, a frente de caixa). " +
            "Se o kiosk estiver vinculado ao Gestão, o endereço vem da tela configurada lá.");

        cmbMonitors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        cmbMonitors.SelectedIndexChanged += cmbMonitors_SelectedIndexChanged;
        Campo(grade, "Monitor:", cmbMonitors, null,
            "Monitor onde a página do kiosk aparece. A troca vale na hora se a tela estiver aberta.");

        Secao(grade, "Botões do topo");
        Dica(grade,
            "Iniciar/Parar tela abre ou fecha o navegador do kiosk. " +
            "Recarregar página atualiza a página aberta, como um F5, sem fechar o navegador.");

        return NovaAba("Tela do kiosk", grade);
    }

    private TabPage CriarAbaJanelas()
    {
        var grade = NovaGrade();

        chkRouter = new CheckBox { AutoSize = true, Text = "Liberar o monitor do kiosk (Window Router)", Margin = new Padding(3, 3, 3, 0) };
        chkRouter.CheckedChanged += chkRouter_CheckedChanged;
        Opcao(grade, chkRouter,
            "Quando ligado, qualquer outra janela que abrir no monitor do kiosk (TEF, PDV, avisos do Windows) " +
            "é levada para o outro monitor, para não ficar escondida atrás da página. " +
            "Vale na próxima vez que a tela for iniciada.");

        cmbRouterFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        Campo(grade, "Janelas movidas:", cmbRouterFilter, null,
            "Amplo também move janelas sem barra de título, como os pop-ups do TEF e do PDV (recomendado). " +
            "Restrito move só as janelas comuns.");

        chkRouterDebugLogging = new CheckBox { AutoSize = true, Text = "Registrar no log as janelas que não foram movidas", Margin = new Padding(3, 3, 3, 0) };
        Opcao(grade, chkRouterDebugLogging,
            "Só para diagnóstico, quando uma janela não sai do monitor do kiosk. Vale após reiniciar a tela.");

        return NovaAba("Janelas", grade);
    }

    private Control CriarRodape()
    {
        var rodape = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12, 8, 12, 10),
            Margin = Padding.Empty
        };
        rodape.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rodape.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        btnOpenLogs = NovoBotao("Abrir logs");
        btnOpenLogs.Click += btnOpenLogs_Click;
        btnOpenConfig = NovoBotao("Abrir pasta de configuração");
        btnOpenConfig.Click += btnOpenConfig_Click;
        var botoes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        botoes.Controls.AddRange(new Control[] { btnOpenLogs, btnOpenConfig });

        var versao = (typeof(ControllerForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];
        lnkAuthor = new LinkLabel
        {
            AutoSize = true,
            Text = $"Versão {versao} · Criado por Max Yuri (GitHub)",
            Anchor = AnchorStyles.Right,
            LinkColor = Color.RoyalBlue,
            ActiveLinkColor = Color.DarkBlue
        };
        lnkAuthor.LinkArea = new LinkArea(lnkAuthor.Text.IndexOf("Criado", StringComparison.Ordinal), lnkAuthor.Text.Length - lnkAuthor.Text.IndexOf("Criado", StringComparison.Ordinal));
        lnkAuthor.LinkClicked += (s, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/maxyuri99/",
                    UseShellExecute = true
                });
            }
            catch { }
        };

        rodape.Controls.Add(botoes, 0, 0);
        rodape.Controls.Add(lnkAuthor, 1, 0);
        return rodape;
    }

    // ---- Montagem das abas: rótulo | campo | botão, com a explicação em cinza logo abaixo ----

    private static TabPage NovaAba(string titulo, TableLayoutPanel grade)
    {
        // A grade cresce só o necessário e fica no topo; a aba rola se o conteúdo não couber.
        var aba = new TabPage(titulo) { UseVisualStyleBackColor = true, Padding = Padding.Empty, AutoScroll = true };
        aba.Controls.Add(grade);
        return aba;
    }

    private static TableLayoutPanel NovaGrade()
    {
        var grade = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 0,
            Padding = new Padding(10, 12, 10, 8)
        };
        grade.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grade.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grade.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grade;
    }

    private static int NovaLinha(TableLayoutPanel grade)
    {
        grade.RowCount++;
        grade.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return grade.RowCount - 1;
    }

    private static Button NovoBotao(string texto) =>
        new() { Text = texto, AutoSize = true, MinimumSize = new Size(80, 28), Margin = new Padding(6, 2, 3, 2) };

    private static void Campo(TableLayoutPanel grade, string rotulo, Control campo, Control? botao, string? dica)
    {
        var linha = NovaLinha(grade);
        grade.Controls.Add(new Label { AutoSize = true, Text = rotulo, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 10, 3) }, 0, linha);
        campo.Margin = new Padding(3, 3, 3, 3);
        grade.Controls.Add(campo, 1, linha);
        if (botao != null) grade.Controls.Add(botao, 2, linha);
        else grade.SetColumnSpan(campo, 2);

        if (dica != null)
        {
            var linhaDica = NovaLinha(grade);
            var lbl = NovaDica(dica);
            grade.Controls.Add(lbl, 1, linhaDica);
            grade.SetColumnSpan(lbl, 2);
        }
    }

    private static void Opcao(TableLayoutPanel grade, CheckBox opcao, string dica)
    {
        var linha = NovaLinha(grade);
        grade.Controls.Add(opcao, 0, linha);
        grade.SetColumnSpan(opcao, 3);

        var linhaDica = NovaLinha(grade);
        var lbl = NovaDica(dica);
        lbl.Margin = new Padding(22, 0, 3, 12);
        grade.Controls.Add(lbl, 0, linhaDica);
        grade.SetColumnSpan(lbl, 3);
    }

    private static void Secao(TableLayoutPanel grade, string titulo)
    {
        var linha = NovaLinha(grade);
        var lbl = new Label
        {
            AutoSize = true,
            Text = titulo,
            Font = new Font("Segoe UI Semibold", 9.75F),
            Margin = new Padding(3, 10, 3, 4)
        };
        grade.Controls.Add(lbl, 0, linha);
        grade.SetColumnSpan(lbl, 3);
    }

    private static void Dica(TableLayoutPanel grade, string texto)
    {
        var linha = NovaLinha(grade);
        var lbl = NovaDica(texto);
        lbl.Margin = new Padding(3, 0, 3, 12);
        grade.Controls.Add(lbl, 0, linha);
        grade.SetColumnSpan(lbl, 3);
    }

    private static Label NovaDica(string texto) => new()
    {
        // AutoSize + âncora esquerda/direita dentro do TableLayoutPanel = quebra de linha na largura da coluna.
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Text = texto,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 0, 3, 12)
    };
}
