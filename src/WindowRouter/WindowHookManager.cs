using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NpsShared.Configuration;
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

    private NpsConfig _config;
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

    /// <summary>When true, hooks keep running but windows are not moved.</summary>
    public bool RoutingPaused { get; set; }

    public WindowHookManager(NpsConfig config)
    {
        _config = config;

        // Exempt processes that should not be moved
        _exemptProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KioskBrowser",
            "NpsDisplay", // legacy
            "WindowRouter",
            "taskmgr",
            "ApplicationFrameHost" // UWP host
        };

        var screens = Screen.AllScreens;
        _npsMonitor = Screen.PrimaryScreen ?? (screens.Length > 0 ? screens[0] : throw new InvalidOperationException("No screens"));
        _targetMonitor = _npsMonitor;

        // Fresh config file + kiosk browser HWND + RouterTarget Opposite/etc.
        SynchronizeRoutingLayout();

        Log.Information(
            "Window Router: Kiosk Monitor = {NPS}, Target Monitor = {Target}, FilterMode = {Mode}, MinVisibleArea = {MinArea}, DebugLogging = {Debug}",
            _npsMonitor.DeviceName, _targetMonitor.DeviceName,
            IsRelaxedMode() ? "Relaxed" : "Strict",
            EffectiveMinVisibleArea(),
            _config.RouterDebugLogging);
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
        _scanTimer.Tick += (_, _) => SafeScanAndRoute();
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
            RefreshLayoutFromRunningBrowserOnly();

            if (RoutingPaused)
                return;

            if (ShouldRouteWindow(hwnd, out var skipReason))
            {
                RouteWindowToTargetMonitor(hwnd);
            }
            else if (!string.IsNullOrEmpty(skipReason))
            {
                LogSkippedCandidate(hwnd, skipReason);
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
            SynchronizeRoutingLayout();

            if (RoutingPaused)
                return;

            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (ShouldRouteWindow(hWnd, out var skipReason))
                    {
                        RouteWindowToTargetMonitor(hWnd);
                    }
                    else if (!string.IsNullOrEmpty(skipReason))
                    {
                        LogSkippedCandidate(hWnd, skipReason);
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

    private bool IsRelaxedMode() =>
        string.Equals(_config.RouterWindowFilterMode, "Relaxed", StringComparison.OrdinalIgnoreCase);

    private int EffectiveMinVisibleArea()
    {
        var v = _config.RouterMinVisibleArea;
        return v < 1 ? 400 : v;
    }

    private static Rectangle RectToRectangle(RECT r) =>
        Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private bool TryGetKioskIntersectionArea(RECT windowRect, out int intersectionArea)
    {
        intersectionArea = 0;
        var win = RectToRectangle(windowRect);
        var inter = Rectangle.Intersect(win, _npsMonitor.Bounds);
        if (inter.Width <= 0 || inter.Height <= 0)
            return false;

        intersectionArea = inter.Width * inter.Height;
        return true;
    }

    /// <summary>
    /// Whether to route this HWND. When debug logging is enabled and the window visibly intersects the kiosk monitor
    /// but is skipped, <paramref name="skipReason"/> describes why (for diagnostics only).
    /// </summary>
    private bool ShouldRouteWindow(IntPtr hwnd, out string? skipReason)
    {
        skipReason = null;

        if (!IsWindowVisible(hwnd))
            return false;

        var rect = new RECT();
        if (!GetWindowRect(hwnd, ref rect))
            return false;

        if (!TryGetKioskIntersectionArea(rect, out var intersectArea))
            return false;

        if (intersectArea < EffectiveMinVisibleArea())
        {
            if (_config.RouterDebugLogging)
                skipReason = $"below_min_visible_area (intersect={intersectArea}, min={EffectiveMinVisibleArea()})";
            return false;
        }

        uint style = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_STYLE).ToInt64());
        uint exStyle = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_EXSTYLE).ToInt64());

        if (!IsRelaxedMode())
        {
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                if (_config.RouterDebugLogging)
                    skipReason = "strict_filter_ws_ex_toolwindow";
                return false;
            }

            if ((style & WS_CAPTION) == 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            {
                if (_config.RouterDebugLogging)
                    skipReason = "strict_filter_no_caption_and_no_ws_ex_appwindow";
                return false;
            }
        }

        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;

            if (_exemptProcessNames.Contains(processName))
            {
                if (_config.RouterDebugLogging)
                    skipReason = $"exempt_process:{processName}";
                return false;
            }

            // explorer.exe hosts desktop/taskbar and folder windows — only skip shell chrome, route File Explorer (CabinetWClass, etc.).
            if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                var cls = GetWindowClassName(hwnd);
                if (IsExplorerShellChromeWindowClass(cls))
                {
                    if (_config.RouterDebugLogging)
                        skipReason = $"exempt_explorer_shell:{cls}";
                    return false;
                }
            }

            skipReason = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not determine if window should be routed");
            return false;
        }
    }

    private static IntPtr GetWindowStylePtr(IntPtr hwnd, int nIndex)
    {
        if (nint.Size == 8)
            return GetWindowLongPtrW(hwnd, nIndex);

        return new IntPtr(GetWindowLongW(hwnd, nIndex));
    }

    private void LogSkippedCandidate(IntPtr hwnd, string reason)
    {
        if (!_config.RouterDebugLogging)
            return;

        try
        {
            var cls = GetWindowClassName(hwnd);
            GetWindowThreadProcessId(hwnd, out uint pid);
            string procName = "?";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                procName = p.ProcessName;
            }
            catch { /* ignore */ }

            var rect = new RECT();
            GetWindowRect(hwnd, ref rect);
            uint style = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_STYLE).ToInt64());
            uint exStyle = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_EXSTYLE).ToInt64());

            Log.Information(
                "WindowRouter skipped ({Reason}): HWND=0x{Hwnd:X} Class={Class} Process={Process} Style=0x{Style:X} ExStyle=0x{ExStyle:X} Rect={Left},{Top}-{Right},{Bottom}",
                reason, hwnd.ToInt64(), cls, procName, style, exStyle,
                rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to log skipped router candidate");
        }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) == 0 ? "" : sb.ToString();
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

            var wasMaximized = IsZoomed(hwnd);

            // Avoid restore/move/maximize loops that cause fullscreen/maximized windows to flicker when already on the target monitor.
            if (IsWindowAlreadyPlacedOnTarget(hwnd, rect, width, height, newX, newY, wasMaximized))
                return;

            // Preserve maximized state: restore, move, then re-maximize if needed
            if (wasMaximized)
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            // Move window without changing Z-order or activation
            if (!SetWindowPos(hwnd, IntPtr.Zero, newX, newY, width, height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE))
            {
                if (_config.RouterDebugLogging)
                {
                    var err = Marshal.GetLastWin32Error();
                    LogSkippedCandidate(hwnd, $"setwindowpos_failed win32={err}");
                }
                return;
            }

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

    /// <summary>Desktop/taskbar/shell windows hosted by explorer.exe — not folder/File Explorer.</summary>
    private static bool IsExplorerShellChromeWindowClass(string className) =>
        string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase);

    private bool IsWindowAlreadyPlacedOnTarget(IntPtr hwnd, RECT rect, int width, int height, int newX, int newY, bool wasMaximized)
    {
        const int tol = 12;

        if (wasMaximized)
        {
            var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            return hMon != IntPtr.Zero && MonitorDeviceNameEqualsScreen(hMon, _targetMonitor);
        }

        // Restored / normal windows: skip if we'd move to the same position (within tolerance).
        if (Math.Abs(rect.Left - newX) <= tol && Math.Abs(rect.Top - newY) <= tol)
            return true;

        // Borderless fullscreen (often not "zoomed"): bounds match target monitor.
        if (RectMatchesScreenBounds(rect, _targetMonitor.Bounds, tol))
            return true;

        return false;
    }

    private static bool RectMatchesScreenBounds(RECT r, Rectangle bounds, int tol)
    {
        var rw = r.Right - r.Left;
        var rh = r.Bottom - r.Top;
        return Math.Abs(r.Left - bounds.Left) <= tol
               && Math.Abs(r.Top - bounds.Top) <= tol
               && Math.Abs(rw - bounds.Width) <= tol
               && Math.Abs(rh - bounds.Height) <= tol;
    }

    private static bool MonitorDeviceNameEqualsScreen(IntPtr hMonitor, Screen screen)
    {
        var mi = new MONITORINFOEX();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
        if (!GetMonitorInfo(hMonitor, ref mi))
            return false;

        return string.Equals(mi.szDevice, screen.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private Screen ResolveKioskMonitorFromConfig(Screen[] screens)
    {
        if (screens.Length == 0)
            throw new InvalidOperationException("No screens");

        if (_config.MonitorPolicy == "Primary")
        {
            return Screen.PrimaryScreen ?? screens[0];
        }

        if (!string.IsNullOrEmpty(_config.MonitorDeviceId))
        {
            var byDevice = screens.FirstOrDefault(s =>
                s.DeviceName.Equals(_config.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));
            if (byDevice != null)
                return byDevice;
        }

        if (_config.MonitorIndex >= 0 && _config.MonitorIndex < screens.Length)
        {
            return screens[_config.MonitorIndex];
        }

        return Screen.PrimaryScreen ?? screens[0];
    }

    private bool TryResolveKioskScreenFromBrowser(out Screen screen)
    {
        screen = null!;

        try
        {
            foreach (var processName in new[] { "KioskBrowser", "NpsDisplay" })
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        p.Refresh();
                        var hWnd = p.MainWindowHandle;
                        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
                            continue;

                        var rect = new RECT();
                        if (!GetWindowRect(hWnd, ref rect))
                            continue;

                        var center = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                        screen = Screen.FromPoint(center);
                        return true;
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>Reloads disk config and recomputes kiosk + target monitors (timer path).</summary>
    private void SynchronizeRoutingLayout()
    {
        try
        {
            _config = ConfigManager.LoadConfig();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Window Router failed to reload config; using previous snapshot");
            return;
        }

        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return;

        var kioskFromConfig = ResolveKioskMonitorFromConfig(screens);
        var effectiveKiosk = kioskFromConfig;
        if (TryResolveKioskScreenFromBrowser(out var fromBrowser))
        {
            effectiveKiosk = fromBrowser;
        }

        var newTarget = ChooseTargetMonitor(screens, effectiveKiosk);

        var npsChanged = !string.Equals(_npsMonitor.DeviceName, effectiveKiosk.DeviceName, StringComparison.OrdinalIgnoreCase);
        var tgtChanged = !string.Equals(_targetMonitor.DeviceName, newTarget.DeviceName, StringComparison.OrdinalIgnoreCase);

        _npsMonitor = effectiveKiosk;
        _targetMonitor = newTarget;

        if (npsChanged || tgtChanged)
        {
            Log.Information(
                "Window Router layout synced — kiosk={Kiosk}, target={Target}, RouterTarget={Rt}",
                _npsMonitor.DeviceName,
                _targetMonitor.DeviceName,
                _config.RouterTarget);
        }
    }

    /// <summary>Often enough for WinEvents: browser moved without needing a disk read.</summary>
    private void RefreshLayoutFromRunningBrowserOnly()
    {
        try
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0)
                return;

            if (!TryResolveKioskScreenFromBrowser(out var fromBrowser))
                return;

            if (string.Equals(_npsMonitor.DeviceName, fromBrowser.DeviceName, StringComparison.OrdinalIgnoreCase))
                return;

            _npsMonitor = fromBrowser;
            _targetMonitor = ChooseTargetMonitor(screens, _npsMonitor);
            Log.Information(
                "Window Router: kiosk monitor updated from browser HWND -> {Nps}; target={Target}",
                _npsMonitor.DeviceName,
                _targetMonitor.DeviceName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Browser-only layout refresh failed");
        }
    }

    private Screen ChooseTargetMonitor(Screen[] screens, Screen npsMonitor)
    {
        if (_config.RouterTarget == "Opposite")
        {
            return screens.FirstOrDefault(s =>
                       !string.Equals(s.DeviceName, npsMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                   ?? npsMonitor;
        }

        if (int.TryParse(_config.RouterTarget, out var targetIndex) && targetIndex >= 0 && targetIndex < screens.Length)
        {
            return screens[targetIndex];
        }

        return screens.FirstOrDefault(s =>
                   !string.Equals(s.DeviceName, npsMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
               ?? npsMonitor;
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y,
        int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int GWLP_STYLE = -16;
    private const int GWLP_EXSTYLE = -20;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public void Dispose()
    {
        Stop();
    }
}
