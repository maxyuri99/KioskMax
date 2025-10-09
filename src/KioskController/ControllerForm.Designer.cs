namespace KioskController;

partial class ControllerForm
{
    private System.ComponentModel.IContainer components = null!;
    private Label lblTitle = null!;
    private Label lblStatus = null!;
    private Button btnStartStop = null!;
    private Button btnReload = null!;
    private Label lblUrl = null!;
    private TextBox txtUrl = null!;
    private Button btnApplyUrl = null!;
    private Label lblMonitor = null!;
    private ComboBox cmbMonitors = null!;
    private CheckBox chkRouter = null!;
    private Button btnOpenLogs = null!;
    private Button btnOpenConfig = null!;
    private LinkLabel lnkAuthor = null!;

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

        Text = "KioskMax - Controller";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(560, 300);

        lblTitle = new Label
        {
            AutoSize = true,
            Text = "Controller",
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(16, 16)
        };

        lblStatus = new Label
        {
            AutoSize = true,
            Text = "Status: ...",
            Location = new Point(16, 48)
        };

        btnStartStop = new Button
        {
            Text = "Start",
            Size = new Size(100, 30),
            Location = new Point(16, 76)
        };
        btnStartStop.Click += btnStartStop_Click;

        btnReload = new Button
        {
            Text = "Reload",
            Size = new Size(100, 30),
            Location = new Point(126, 76)
        };
        btnReload.Click += btnReload_Click;

        lblUrl = new Label
        {
            AutoSize = true,
            Text = "Kiosk URL:",
            Location = new Point(16, 124)
        };

        txtUrl = new TextBox
        {
            Size = new Size(360, 27),
            Location = new Point(80, 120)
        };

        btnApplyUrl = new Button
        {
            Text = "Apply",
            Size = new Size(80, 28),
            Location = new Point(446, 119)
        };
        btnApplyUrl.Click += btnApplyUrl_Click;

        lblMonitor = new Label
        {
            AutoSize = true,
            Text = "Monitor:",
            Location = new Point(16, 164)
        };

        cmbMonitors = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Size = new Size(270, 28),
            Location = new Point(80, 160)
        };
        cmbMonitors.SelectedIndexChanged += cmbMonitors_SelectedIndexChanged;

        chkRouter = new CheckBox
        {
            AutoSize = true,
            Text = "Window Router Enabled",
            Location = new Point(370, 164)
        };
        chkRouter.CheckedChanged += chkRouter_CheckedChanged;

        btnOpenLogs = new Button
        {
            Text = "Open Logs",
            Size = new Size(100, 28),
            Location = new Point(16, 210)
        };
        btnOpenLogs.Click += btnOpenLogs_Click;

        btnOpenConfig = new Button
        {
            Text = "Open Config",
            Size = new Size(110, 28),
            Location = new Point(126, 210)
        };
        btnOpenConfig.Click += btnOpenConfig_Click;

        lnkAuthor = new LinkLabel
        {
            AutoSize = true,
            Text = "Criado por Max Yuri (GitHub)",
            Location = new Point(16, 252),
            LinkColor = Color.RoyalBlue,
            ActiveLinkColor = Color.DarkBlue
        };
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

        Controls.Add(lblTitle);
        Controls.Add(lblStatus);
        Controls.Add(btnStartStop);
        Controls.Add(btnReload);
        Controls.Add(lblUrl);
        Controls.Add(txtUrl);
        Controls.Add(btnApplyUrl);
        Controls.Add(lblMonitor);
        Controls.Add(cmbMonitors);
        Controls.Add(chkRouter);
        Controls.Add(btnOpenLogs);
        Controls.Add(btnOpenConfig);
        Controls.Add(lnkAuthor);
    }
}


