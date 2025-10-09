using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NpsShared.Models;
using Serilog;

namespace WindowRouter;

/// <summary>
/// Manages Windows event hooks to detect and route windows
/// </summary>
public class WindowHookManager : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private readonly NpsConfig _config;
    private Screen _npsMonitor;
    private Screen _targetMonitor;
    private IntPtr _foregroundHook;
    private IntPtr _showHook;
    private IntPtr _createHook;
    private WinEventDelegate? _foregroundDelegate;
    private WinEventDelegate? _showDelegate;
    private WinEventDelegate? _createDelegate;
    private System.Windows.Forms.Timer? _scanTimer;
    private readonly HashSet<string> _exemptProcessNames;

    public WindowHookManager(NpsConfig config)
    {
        _config = config;
        
        // Determine monitors consistent with Display selection logic
        var screens = Screen.AllScreens;

        // Choose the NPS monitor according to MonitorPolicy/DeviceId/Index
        if (_config.MonitorPolicy == "Primary")
        {
            _npsMonitor = Screen.PrimaryScreen ?? screens[0];
        }
        else
        {
            // Explicit policy
            Screen? chosen = null;
            if (!string.IsNullOrEmpty(_config.MonitorDeviceId))
            {
                chosen = screens.FirstOrDefault(s => 
                    s.DeviceName.Equals(_config.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));
            }
            _npsMonitor = chosen ?? screens[Math.Min(_config.MonitorIndex, screens.Length - 1)];
        }

        // Choose target monitor (initial)
        _targetMonitor = ChooseTargetMonitor(screens, _npsMonitor);

        // Exempt processes that should not be moved
        _exemptProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KioskBrowser",
            "NpsDisplay", // legacy
            "WindowRouter",
            "explorer",
            "taskmgr",
            "ApplicationFrameHost" // UWP host
        };

        // Try to align NPS monitor to where NpsDisplay actually is (if running)
        UpdateMonitorsFromDisplayIfAvailable();

        Log.Information("Window Router: Kiosk Monitor = {NPS}, Target Monitor = {Target}",
            _npsMonitor.DeviceName, _targetMonitor.DeviceName);
    }

    public void Start()
    {
        // Create delegates (must be kept alive!)
        _foregroundDelegate = new WinEventDelegate(WinEventProc);
        _showDelegate = new WinEventDelegate(WinEventProc);
        _createDelegate = new WinEventDelegate(WinEventProc);

        // Set hooks
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        _showHook = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
            IntPtr.Zero, _showDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        _createHook = SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE,
            IntPtr.Zero, _createDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        if (_foregroundHook == IntPtr.Zero || _showHook == IntPtr.Zero || _createHook == IntPtr.Zero)
        {
            Log.Error("Failed to set window event hooks");
            throw new InvalidOperationException("Failed to set window event hooks");
        }

        // Periodic scan to catch missed windows
        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (s, e) => SafeScanAndRoute();
        _scanTimer.Start();

        Log.Information("Window event hooks installed and scan timer started");
    }

    public void Stop()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_showHook != IntPtr.Zero)
        {
            UnhookWinEvent(_showHook);
            _showHook = IntPtr.Zero;
        }

        if (_createHook != IntPtr.Zero)
        {
            UnhookWinEvent(_createHook);
            _createHook = IntPtr.Zero;
        }

        if (_scanTimer != null)
        {
            _scanTimer.Stop();
            _scanTimer.Dispose();
            _scanTimer = null;
        }

        Log.Information("Window event hooks removed");
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only handle window events (not child objects)
        if (idObject != 0 || idChild != 0)
            return;

        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            // Check if window should be routed
            if (ShouldRouteWindow(hwnd))
            {
                RouteWindowToTargetMonitor(hwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing window event");
        }
    }

    private void SafeScanAndRoute()
    {
        try
        {
            // Periodically refresh monitors in case Display moved
            UpdateMonitorsFromDisplayIfAvailable();

            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (ShouldRouteWindow(hWnd))
                    {
                        RouteWindowToTargetMonitor(hWnd);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error scanning window");
                }
                return true; // continue
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during periodic scan");
        }
    }

    private bool ShouldRouteWindow(IntPtr hwnd)
    {
        // Must be visible
        if (!IsWindowVisible(hwnd))
            return false;

        // Get window style
        var style = GetWindowLong(hwnd, GWL_STYLE);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        // Ignore tool windows, popup windows without caption
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;

        if ((style & WS_CAPTION) == 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;

        // Get process name
        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;

            // Check exemptions
            if (_exemptProcessNames.Contains(processName))
            {
                return false;
            }

            // Check if window is on NPS monitor
            var rect = new RECT();
            if (GetWindowRect(hwnd, ref rect))
            {
                var windowCenter = new Point(
                    (rect.Left + rect.Right) / 2,
                    (rect.Top + rect.Bottom) / 2);

                var currentScreen = Screen.FromPoint(windowCenter);
                
                // Only route if on NPS monitor
                return currentScreen.DeviceName == _npsMonitor.DeviceName;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not determine if window should be routed");
        }

        return false;
    }

    private void RouteWindowToTargetMonitor(IntPtr hwnd)
    {
        try
        {
            var rect = new RECT();
            if (!GetWindowRect(hwnd, ref rect))
                return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            // Calculate new position on target monitor (centered)
            var newX = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - width) / 2;
            var newY = _targetMonitor.Bounds.Top + (_targetMonitor.Bounds.Height - height) / 2;

            // Preserve maximized state: restore, move, then re-maximize if needed
            var wasMaximized = IsZoomed(hwnd);
            if (wasMaximized)
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            // Move window without changing Z-order or activation
            SetWindowPos(hwnd, IntPtr.Zero, newX, newY, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);

            if (wasMaximized)
            {
                ShowWindow(hwnd, SW_MAXIMIZE);
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            using var process = Process.GetProcessById((int)processId);
            Log.Debug("Routed window from {Process} to target monitor", process.ProcessName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to route window");
        }
    }

    private Screen ChooseTargetMonitor(Screen[] screens, Screen npsMonitor)
    {
        if (_config.RouterTarget == "Opposite")
        {
            return screens.FirstOrDefault(s => s != npsMonitor) ?? npsMonitor;
        }

        if (int.TryParse(_config.RouterTarget, out var targetIndex) && targetIndex >= 0 && targetIndex < screens.Length)
        {
            return screens[targetIndex];
        }

        return screens.FirstOrDefault(s => s != npsMonitor) ?? npsMonitor;
    }

    private void UpdateMonitorsFromDisplayIfAvailable()
    {
        try
        {
            var displayProc = Process.GetProcessesByName("KioskBrowser").FirstOrDefault() ??
                               Process.GetProcessesByName("NpsDisplay").FirstOrDefault();
            if (displayProc == null)
                return;

            var hWnd = displayProc.MainWindowHandle;
            if (hWnd == IntPtr.Zero)
                return;

            var rect = new RECT();
            if (!GetWindowRect(hWnd, ref rect))
                return;

            var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            var screens = Screen.AllScreens;
            var actualNpsMonitor = Screen.FromPoint(center);

            if (!string.Equals(actualNpsMonitor.DeviceName, _npsMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                _npsMonitor = actualNpsMonitor;
                _targetMonitor = ChooseTargetMonitor(screens, _npsMonitor);
                Log.Information("Window Router: Detected Browser on {NPS}; updated Target to {Target}",
                    _npsMonitor.DeviceName, _targetMonitor.DeviceName);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to update monitors from Display process");
        }
    }

    // Windows API declarations
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, 
        int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Constants
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;

    public void Dispose()
    {
        Stop();
    }
}

