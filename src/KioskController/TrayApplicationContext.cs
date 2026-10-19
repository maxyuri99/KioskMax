using System.Diagnostics;
using NpsShared.Configuration;
using NpsShared.IPC;
using NpsShared.Models;
using NpsShared.Utilities;
using Serilog;
using System.Runtime.InteropServices;

namespace KioskController;

public class TrayApplicationContext : ApplicationContext
{
	private readonly NotifyIcon _trayIcon;
	private readonly ContextMenuStrip _contextMenu;
	private readonly AgenteKiosk _agente;
	private bool _avisouControleGestao;
	private System.Windows.Forms.Timer _statusCheckTimer;
	private NpsConfig _config;
	private SynchronizationContext? _uiContext;
	private bool _isShuttingDown;

	private ToolStripMenuItem _startStopMenuItem = null!;
	private ToolStripMenuItem _statusMenuItem = null!;
	private ToolStripMenuItem _routerMenuItem = null!;
	private ToolStripMenuItem _openControllerMenuItem = null!;
	private ToolStripMenuItem _gestaoMenuItem = null!;
	private ToolStripMenuItem _tefMenuItem = null!;
	private ControllerForm? _controllerForm;
	private int _hotkeyIdCtrlAltK;
	private int _hotkeyIdCtrlAltH;
	private MessageWindow? _messageWindow;

	public TrayApplicationContext()
	{
		_config = ConfigManager.LoadConfig();
		_uiContext = SynchronizationContext.Current;
		_agente = new AgenteKiosk();
		_agente.EstadoAlterado += () => RunOnUi(AtualizarItensGestaoTef);
		_agente.ReiniciarSolicitado += () => RunOnUi(ReiniciarAgente);

		// Create context menu
		_contextMenu = new ContextMenuStrip();
		BuildContextMenu();

		// Create tray icon
		_trayIcon = new NotifyIcon
		{
			Icon = TryLoadIcon(new[] { "kioskcontroller_32.png", "kioskcontroller_64.png", "kioskcontroller_256.png", "kioskcontroller_1024.png" }),
			ContextMenuStrip = _contextMenu,
			Visible = true,
			Text = "KioskMax"
		};

		_trayIcon.DoubleClick += TrayIcon_DoubleClick;

		// Status check timer
		_statusCheckTimer = new System.Windows.Forms.Timer
		{
			Interval = 5000 // Check every 5 seconds
		};
		_statusCheckTimer.Tick += StatusCheckTimer_Tick;
		_statusCheckTimer.Start();

		Log.Information("Tray application initialized");


		// Ensure cleanup on app exit and session end
		Application.ApplicationExit += (_, __) =>
		{
			try { CleanupAll(); } catch { }
			try { UnregisterHotkeysSafe(); } catch { }
		};
		Microsoft.Win32.SystemEvents.SessionEnding += (_, __) =>
		{
			try { CleanupAll(); } catch { }
			try { UnregisterHotkeysSafe(); } catch { }
		};

		// Agent drives display, router and TEF (local legacy behavior when no Gestao URL)
		try
		{
			_agente.Iniciar();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to start kiosk agent");
		}

		// Register global hotkeys after initialization
		try
		{
			RegisterHotkeys();
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "Failed to register global hotkeys");
		}
	}

	private void BuildContextMenu()
	{
		_statusMenuItem = new ToolStripMenuItem("Status: Checking...");
		_statusMenuItem.Enabled = false;

		_gestaoMenuItem = new ToolStripMenuItem("Gestão: ...") { Enabled = false };
		_tefMenuItem = new ToolStripMenuItem("TEF: ...") { Enabled = false };
		var vinculoMenuItem = new ToolStripMenuItem("Vínculo com o Gestão...", null, Vinculo_Click);
		var imprimirTesteMenuItem = new ToolStripMenuItem("Imprimir teste TEF", null, ImprimirTesteTef_Click);

		_startStopMenuItem = new ToolStripMenuItem("Start Display", null, StartStopDisplay_Click);

		var changeUrlMenuItem = new ToolStripMenuItem("Change URL...", null, ChangeUrl_Click);
		var changeMonitorMenuItem = new ToolStripMenuItem("Change Monitor", null, BuildMonitorSubmenu());
		var usePrimaryMenuItem = new ToolStripMenuItem("Use Primary (Auto)", null, UsePrimaryMonitor_Click)
		{
			Checked = string.Equals(_config.MonitorPolicy, "Primary", StringComparison.OrdinalIgnoreCase)
		};
		var reloadMenuItem = new ToolStripMenuItem("Reload", null, Reload_Click);

		_routerMenuItem = new ToolStripMenuItem($"Window Router: {(_config.RouterEnabled ? "ON" : "OFF")}", 
			null, ToggleRouter_Click);

		_openControllerMenuItem = new ToolStripMenuItem("Open Controller...", null, OpenController_Click);

		var openConfigMenuItem = new ToolStripMenuItem("Open Config Folder", null, OpenConfig_Click);
		var openLogsMenuItem = new ToolStripMenuItem("Open Logs Folder", null, OpenLogs_Click);
		var aboutAuthorMenuItem = new ToolStripMenuItem("Autor: Max Yuri (GitHub)", null, OpenAuthor_Click);
		var exitMenuItem = new ToolStripMenuItem("Exit", null, Exit_Click);

		_contextMenu.Items.Add(_statusMenuItem);
		_contextMenu.Items.Add(_gestaoMenuItem);
		_contextMenu.Items.Add(_tefMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(_startStopMenuItem);
		_contextMenu.Items.Add(reloadMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(_openControllerMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(changeUrlMenuItem);
		_contextMenu.Items.Add(changeMonitorMenuItem);
		_contextMenu.Items.Add(usePrimaryMenuItem);
		_contextMenu.Items.Add(_routerMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(vinculoMenuItem);
		_contextMenu.Items.Add(imprimirTesteMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(openConfigMenuItem);
		_contextMenu.Items.Add(openLogsMenuItem);
		_contextMenu.Items.Add(aboutAuthorMenuItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitMenuItem);
	}

	private ToolStripMenuItem[] BuildMonitorSubmenu()
	{
		var screens = Screen.AllScreens;
		var menuItems = new List<ToolStripMenuItem>();

		for (int i = 0; i < screens.Length; i++)
		{
			var screen = screens[i];
			var index = i;
			var menuItem = new ToolStripMenuItem(
				$"Monitor {i + 1}: {screen.Bounds.Width}x{screen.Bounds.Height} {(screen.Primary ? "(Primary)" : "")}",
				null,
				async (s, e) => await SetMonitor_Click(index)
			);

			if (i == _config.MonitorIndex)
			{
				menuItem.Checked = true;
			}

			menuItems.Add(menuItem);
		}

		return menuItems.ToArray();
	}

	private async void UsePrimaryMonitor_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "usar o monitor principal")) return;
		try
		{
			AvisarControleGestao();
			SetBusy(true, "Switching to Primary...");
			_config = ConfigManager.Update(c => c.MonitorPolicy = "Primary");

			var isRunning = await IpcClient.IsDisplayRunningAsync();
			if (isRunning)
			{
				// Descobrir índice do primário e mandar SetMonitor para aplicar imediatamente
				var screens = Screen.AllScreens;
				var primary = Screen.PrimaryScreen ?? screens.FirstOrDefault() ?? screens[0];
				var index = Array.IndexOf(screens, primary);
				if (index >= 0)
				{
					var result = await IpcClient.SetMonitorAsync(index);
					if (!result.Success)
					{
						MessageBox.Show($"Failed to switch to primary: {result.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					}
					else
					{
						_config = ConfigManager.Update(c => c.MonitorIndex = index);
					}
				}
			}

			// Rebuild to refresh checks
			_contextMenu.Items.Clear();
			BuildContextMenu();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to set primary monitor policy");
			MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void TrayIcon_DoubleClick(object? sender, EventArgs e)
	{
		Log.Information("Tray icon double-click: attempting reload or start");
		Task.Run(async () =>
		{
			var isRunning = await IpcClient.IsDisplayRunningAsync();
			Log.Information("Double-click status: IsDisplayRunning={IsRunning}", isRunning);
			if (isRunning)
			{
				await IpcClient.ReloadAsync();
				Log.Information("Sent reload to Display");
			}
			else
			{
				await StartDisplayAsync();
			}
		});
	}

	private async void StatusCheckTimer_Tick(object? sender, EventArgs e)
	{
		await UpdateStatus();
	}

	private void RunOnUi(Action acao)
	{
		try
		{
			if (_contextMenu.IsHandleCreated && _contextMenu.InvokeRequired)
				_contextMenu.BeginInvoke(acao);
			else
				acao();
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "RunOnUi failed");
		}
	}

	private void AtualizarItensGestaoTef()
	{
		try
		{
			_gestaoMenuItem.Text = "Gestão: " + TextoGestao(_agente);
			_tefMenuItem.Text = "TEF: " + TextoTef(_agente);
		}
		catch { }
	}

	public static string TextoGestao(AgenteKiosk agente)
	{
		return agente.Estado switch
		{
			EstadoGestao.Desativado => "não configurado (modo local)",
			EstadoGestao.Conectando => "conectando...",
			EstadoGestao.Online => "online",
			EstadoGestao.Offline => "offline",
			EstadoGestao.Aguardando => "aguardando aprovação",
			EstadoGestao.Bloqueado => "bloqueado",
			EstadoGestao.Recusado => "vínculo recusado (redefina no Gestão)",
			_ => ""
		};
	}

	public static string TextoTef(AgenteKiosk agente)
	{
		try
		{
			var cfg = ConfigManager.LoadConfig();
			if (!cfg.TefAtivo) return "desativado";
			var estado = agente.Tef.Estado;
			if (estado.Servidor && estado.WebConnection) return estado.Ocupado ? "ativo (transação em andamento)" : "ativo";
			if (estado.Servidor) return "Web Connection desconectado";
			if (!string.IsNullOrWhiteSpace(estado.Erro)) return estado.Erro.Contains("ocupad", StringComparison.OrdinalIgnoreCase) ? "porta ocupada" : "falha - " + estado.Erro;
			return "iniciando...";
		}
		catch
		{
			return "indisponível";
		}
	}

	private void AvisarControleGestao()
	{
		if (_avisouControleGestao || !_agente.ControladoPeloGestao) return;
		_avisouControleGestao = true;
		MessageBox.Show(
			"Este kiosk é controlado pelo Gestão. A alteração local funciona agora, mas será sobrescrita na próxima mudança de configuração feita no Gestão.",
			"KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	private void Vinculo_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "alterar o vínculo com o Gestão")) return;
		try
		{
			using var dialogo = new VinculoGestaoForm(_agente);
			dialogo.ShowDialog();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to open Gestao link dialog");
		}
	}

	private async void ImprimirTesteTef_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "imprimir o teste do TEF")) return;
		try
		{
			await Task.Run(() => _agente.Tef.ImprimirTeste());
			MessageBox.Show("Teste de impressão enviado.", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to print TEF test");
			MessageBox.Show($"Falha ao imprimir o teste: {ex.Message}", "TEF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	private void ReiniciarAgente()
	{
		if (_isShuttingDown) return;

		if (!_agente.Tef.TentarReservar())
		{
			Log.Warning("Reinício do agente adiado: transação TEF em andamento");
			var espera = new System.Windows.Forms.Timer { Interval = 5000 };
			espera.Tick += (_, __) =>
			{
				espera.Stop();
				espera.Dispose();
				ReiniciarAgente();
			};
			espera.Start();
			return;
		}

		try
		{
			Log.Information("Restarting agent by Gestao command");
			var exe = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(exe))
			{
				Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
			}
			_isShuttingDown = true;
			try { _trayIcon.Visible = false; } catch { }
			Application.Exit();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to restart agent");
			_agente.Tef.LiberarReserva();
		}
	}

	private async Task UpdateStatus()
	{
		try
		{
			RunOnUi(AtualizarItensGestaoTef);
			var isRunning = await IpcClient.IsDisplayRunningAsync();

			if (_contextMenu.InvokeRequired)
			{
				_contextMenu.Invoke(() =>
				{
					_statusMenuItem.Text = isRunning ? "Status: ✓ Running" : "Status: ✗ Stopped";
					_startStopMenuItem.Text = isRunning ? "Stop Display" : "Start Display";
				});
			}
			else
			{
				_statusMenuItem.Text = isRunning ? "Status: ✓ Running" : "Status: ✗ Stopped";
				_startStopMenuItem.Text = isRunning ? "Stop Display" : "Start Display";
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error updating status");
			try
			{
				if (_contextMenu.InvokeRequired)
				{
					_contextMenu.Invoke(() =>
					{
						_statusMenuItem.Text = "Status: ✗ Stopped";
						_startStopMenuItem.Text = "Start Display";
					});
				}
				else
				{
					_statusMenuItem.Text = "Status: ✗ Stopped";
					_startStopMenuItem.Text = "Start Display";
				}
			}
			catch { }
		}
	}

	private async void StartStopDisplay_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "iniciar ou parar a tela")) return;
		try
		{
			Log.Information("Start/Stop clicked by user");
			SetBusy(true, "Processing...");
			var isRunning = await IpcClient.IsDisplayRunningAsync();
			Log.Information("IsDisplayRunning={IsRunning}", isRunning);

			if (isRunning)
			{
				Log.Information("Invoking StopDisplay()");
				await StopDisplay();
			}
			else
			{
				Log.Information("Invoking StartDisplay()");
				await StartDisplayAsync();
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error toggling display");
			MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async Task StartDisplayAsync()
	{
		try
		{
			Log.Information("StartDisplay(): begin");
			_config = ConfigManager.LoadConfig();
			var ok = await _agente.Tela.IniciarAsync(_config.RouterEnabled);
			if (!ok)
			{
				RunOnUi(() => MessageBox.Show($"KioskBrowser.exe could not be started.\nExpected at:\n{GerenciadorTela.ResolverCaminhoExe()}\n\nPlease ensure it's in the same directory.",
					"Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
			}

			_ = Task.Delay(1000).ContinueWith(_ => UpdateStatus());
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to start display");
			RunOnUi(() => MessageBox.Show($"Failed to start display: {ex.Message}", "Error",
				MessageBoxButtons.OK, MessageBoxIcon.Error));
		}
	}

	private async Task StopDisplay()
	{
		try
		{
			await _agente.Tela.PararAsync();
			await UpdateStatus();
			Log.Information("Window Router stopped together with display");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to stop display");
			throw;
		}
	}

	private async void Reload_Click(object? sender, EventArgs e)
	{
		try
		{
			SetBusy(true, "Reloading...");
			var response = await IpcClient.ReloadAsync();

			if (response.Success)
			{
				Log.Information("Display reloaded");
			}
			else
			{
				MessageBox.Show($"Reload failed: {response.Message}", "Error", 
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to reload");
			MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async void ChangeUrl_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "trocar o endereço da tela")) return;
		try
		{
			AvisarControleGestao();
			SetBusy(true, "Applying URL...");
			var input = new global::KioskController.InputDialog("Enter new NPS URL:", _config.NpsUrl);

			if (input.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(input.InputValue))
			{
				var newUrl = input.InputValue;
				var response = await IpcClient.SetUrlAsync(newUrl);

				if (response.Success)
				{
					_config = ConfigManager.Update(c =>
					{
						c.NpsUrl = newUrl;
						c.TelaLocal = string.Empty;
					});
					Log.Information("URL changed to: {Url}", newUrl);
					MessageBox.Show("URL updated successfully", "Success", 
						MessageBoxButtons.OK, MessageBoxIcon.Information);
				}
				else
				{
					MessageBox.Show($"Failed to change URL: {response.Message}", "Error", 
						MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to change URL");
			MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async Task SetMonitor_Click(int monitorIndex)
	{
		if (!SenhaLocal.Solicitar(null, "trocar o monitor")) return;
		try
		{
			AvisarControleGestao();
			SetBusy(true, "Changing monitor...");
			var response = await IpcClient.SetMonitorAsync(monitorIndex);

			if (response.Success)
			{
				_config = ConfigManager.Update(c =>
				{
					c.MonitorPolicy = "Explicit";
					c.MonitorIndex = monitorIndex;
					c.MonitorDeviceId = string.Empty;
				});
				Log.Information("Monitor changed to index: {Index}", monitorIndex);

				// Rebuild menu to update checkmarks
				_contextMenu.Items.Clear();
				BuildContextMenu();
			}
			else
			{
				MessageBox.Show($"Failed to change monitor: {response.Message}", "Error", 
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to change monitor");
			MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void ToggleRouter_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "ligar ou desligar o roteador de janelas")) return;
		AvisarControleGestao();
		SetBusy(true, _config.RouterEnabled ? "Stopping Router..." : "Starting Router...");
		var novoRoteador = !_config.RouterEnabled;
		_config = ConfigManager.Update(c => c.RouterEnabled = novoRoteador);
		_routerMenuItem.Text = $"Window Router: {(_config.RouterEnabled ? "ON" : "OFF")}";
		Log.Information("Window Router toggled: {Enabled}", _config.RouterEnabled);

		try
		{
			if (_config.RouterEnabled)
			{
				if (!StartWindowRouter())
				{
					MessageBox.Show(
						"Window Router está habilitado na configuração, mas não foi possível iniciar.\nVerifique se WindowRouter.exe está na mesma pasta do Controller e consulte os logs.",
						"Window Router",
						MessageBoxButtons.OK,
						MessageBoxIcon.Warning);
				}
				else
				{
					MessageBox.Show("Window Router enabled and started.", "Window Router",
						MessageBoxButtons.OK, MessageBoxIcon.Information);
				}
			}
			else
			{
				StopWindowRouter();
				MessageBox.Show("Window Router disabled and stopped.", "Window Router", 
					MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to toggle Window Router");
			MessageBox.Show($"Failed to toggle Window Router: {ex.Message}", "Window Router", 
				MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void OpenController_Click(object? sender, EventArgs e)
	{
		ShowController();
	}

	private void ShowController()
	{
		if (!SenhaLocal.Solicitar(null, "abrir o controle do kiosk")) return;
		try
		{
			if (_controllerForm == null || _controllerForm.IsDisposed)
			{
				_controllerForm = new ControllerForm(_agente);
			}
			_controllerForm.Show();
			_controllerForm.WindowState = FormWindowState.Normal;
			_controllerForm.BringToFront();
			TryBringToForeground(_controllerForm);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to show controller");
		}
	}

	private void OpenConfig_Click(object? sender, EventArgs e)
	{
		if (!SenhaLocal.Solicitar(null, "abrir a pasta de configuração")) return;
		ConfigManager.OpenConfigDirectory();
	}

	private void OpenLogs_Click(object? sender, EventArgs e)
	{
		ConfigManager.OpenLogDirectory();
	}

	private void OpenAuthor_Click(object? sender, EventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "https://github.com/maxyuri99/",
				UseShellExecute = true
			});
		}
		catch { }
	}

	private void Exit_Click(object? sender, EventArgs e)
	{
		if (_agente.Tef.Estado.Ocupado)
		{
			AvisarTefEmAndamento();
			return;
		}

		if (!SenhaLocal.Solicitar(null, "fechar o KioskMax")) return;

		if (!_agente.Tef.TentarReservar())
		{
			AvisarTefEmAndamento();
			return;
		}

		Log.Information("Exit requested by user");
		BeginCleanupAndExit();
	}

	private static void AvisarTefEmAndamento()
	{
		Log.Warning("Saída recusada: transação TEF em andamento");
		MessageBox.Show("Há uma transação TEF em andamento. Aguarde terminar no pinpad antes de fechar o KioskMax.",
			"KioskMax", MessageBoxButtons.OK, MessageBoxIcon.Warning);
	}

	private bool StartWindowRouter()
	{
		return _agente.Tela.IniciarRoteador();
	}

	private void StopWindowRouter()
	{
		_agente.Tela.PararRoteador();
	}

	private void SetBusy(bool isBusy, string? message = null)
	{
		try
		{
			_trayIcon.Text = isBusy && !string.IsNullOrWhiteSpace(message)
				? $"KioskMax - {message}"
				: "KioskMax";
			_contextMenu.Enabled = !isBusy;
			Cursor.Current = isBusy ? Cursors.WaitCursor : Cursors.Default;
		}
		catch { }
	}

	private static void TryKillByName(string processName)
	{
		try
		{
			foreach (var p in Process.GetProcessesByName(processName))
			{
				try
				{
					p.Kill();
					p.WaitForExit(2000);
				}
				catch { }
			}
		}
		catch { }
	}

	private void CleanupAll()
	{
		try
		{
			try { _agente.DisposeAsync().AsTask().Wait(4000); } catch { }

			TryKillByName("WindowRouter");
			TryKillByName("KioskBrowser");
			TryKillByName("NpsDisplay");
		}
		catch { }
	}

	private void BeginCleanupAndExit()
	{
		if (_isShuttingDown) return;
		_isShuttingDown = true;
		try { _statusCheckTimer.Stop(); } catch { }
		try { _trayIcon.Visible = false; } catch { }
		try { _contextMenu.Enabled = false; } catch { }

		Task.Run(() =>
		{
			try { CleanupAll(); } catch { }
			try
			{
				// Garante o encerramento no thread de UI mesmo se o SynchronizationContext não tiver sido capturado
				if (_contextMenu != null)
				{
					if (_contextMenu.IsHandleCreated)
					{
						_contextMenu.BeginInvoke(new Action(() => Application.Exit()));
					}
					else
					{
						Application.Exit();
					}
				}
				else
				{
					Application.Exit();
				}
			}
			catch
			{
				try { Application.Exit(); } catch { }
			}
		});

		// Fallback final: agenda um encerramento forçado por processo externo (não bloqueante)
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = "-NoProfile -WindowStyle Hidden -Command \"Start-Sleep -Milliseconds 500; Stop-Process -Name KioskBrowser,WindowRouter,KioskController -Force -ErrorAction SilentlyContinue; Stop-Process -Name NpsDisplay -Force -ErrorAction SilentlyContinue\"",
				UseShellExecute = false,
				CreateNoWindow = true
			});
		}
		catch { }
	}

	private static Icon TryLoadIcon(string[] candidateFiles)
	{
		try
		{
			var runtimeBase = AppDomain.CurrentDomain.BaseDirectory;
			var roots = new List<string>();
			void AddAnc(string start)
			{
				try
				{
					var d = new DirectoryInfo(start);
					for (int i = 0; i < 8 && d != null; i++)
					{
						roots.Add(d.FullName);
						d = d.Parent;
					}
				}
				catch { }
			}
			AddAnc(runtimeBase);
			var exe = Process.GetCurrentProcess().MainModule?.FileName;
			if (!string.IsNullOrEmpty(exe)) AddAnc(Path.GetDirectoryName(exe)!);

			foreach (var root in roots.Distinct())
			{
				var iconDir = Path.Combine(root, "icones");
				if (!Directory.Exists(iconDir)) continue;
				foreach (var file in candidateFiles)
				{
					var path = Path.Combine(iconDir, file);
					if (!File.Exists(path)) continue;
					try
					{
						using var bmp = new Bitmap(path);
						var h = bmp.GetHicon();
						return Icon.FromHandle(h);
					}
					catch { }
				}
			}
		}
		catch { }
		return SystemIcons.Application;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			try { UnregisterHotkeysSafe(); } catch { }
			_statusCheckTimer?.Dispose();
			_trayIcon?.Dispose();
			_contextMenu?.Dispose();
		}
		base.Dispose(disposing);
	}

	// ==== Global Hotkeys Registration and Handling ====
	private void RegisterHotkeys()
	{
		// Create a message-only window to receive WM_HOTKEY
		var window = new MessageWindow();
		window.HotkeyPressed += (_, args) =>
		{
			Log.Information("Global hotkey received: {Id}", args.HotkeyId);
			if (args.HotkeyId == 1)
				ShowController();
			else if (args.HotkeyId == 2 && SenhaLocal.Solicitar(null, "alternar a tela sempre visível"))
				_ = Task.Run(async () =>
				{
					try { await IpcClient.ToggleTopMostAsync(); } catch (Exception ex) { Log.Warning(ex, "ToggleTopMost from hotkey failed"); }
				});
		};

		// Ctrl+Alt+K
		_hotkeyIdCtrlAltK = 1;
		if (!RegisterHotKey(window.Handle, _hotkeyIdCtrlAltK, MOD_CONTROL | MOD_ALT, (int)System.Windows.Forms.Keys.K))
		{
			throw new InvalidOperationException("Failed to register Ctrl+Alt+K");
		}

		// Ctrl+Alt+H
		_hotkeyIdCtrlAltH = 2;
		if (!RegisterHotKey(window.Handle, _hotkeyIdCtrlAltH, MOD_CONTROL | MOD_ALT, (int)System.Windows.Forms.Keys.H))
		{
			throw new InvalidOperationException("Failed to register Ctrl+Alt+H");
		}

		_messageWindow = window;
		Log.Information("Global hotkeys registered");
	}

	private void UnregisterHotkeysSafe()
	{
		try
		{
			if (_messageWindow != null && _messageWindow.Handle != IntPtr.Zero)
			{
				if (_hotkeyIdCtrlAltK != 0) UnregisterHotKey(_messageWindow.Handle, _hotkeyIdCtrlAltK);
				if (_hotkeyIdCtrlAltH != 0) UnregisterHotKey(_messageWindow.Handle, _hotkeyIdCtrlAltH);
			}
		}
		catch { }
		finally
		{
			try { _messageWindow?.DestroyHandle(); } catch { }
			_messageWindow = null;
			_hotkeyIdCtrlAltK = 0;
			_hotkeyIdCtrlAltH = 0;
		}
	}

	private void TryBringToForeground(Form form)
	{
		try
		{
			var hWnd = form.Handle;
			ShowWindow(hWnd, 9); // SW_RESTORE
			SetForegroundWindow(hWnd);
		}
		catch { }
	}

	private const int WM_HOTKEY = 0x0312;
	private const int MOD_ALT = 0x0001;
	private const int MOD_CONTROL = 0x0002;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	private class MessageWindow : NativeWindow, IDisposable
	{
		public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

		public MessageWindow()
		{
			CreateHandle(new CreateParams());
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WM_HOTKEY)
			{
				var id = m.WParam.ToInt32();
				HotkeyPressed?.Invoke(this, new HotkeyEventArgs(id));
			}
			base.WndProc(ref m);
		}

		public void Dispose()
		{
			try { DestroyHandle(); } catch { }
		}
	}

	private class HotkeyEventArgs : EventArgs
	{
		public int HotkeyId { get; }
		public HotkeyEventArgs(int id) { HotkeyId = id; }
	}
}

