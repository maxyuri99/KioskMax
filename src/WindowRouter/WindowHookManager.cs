using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NpsShared.Configuration;
using NpsShared.Models;
using Serilog;

namespace WindowRouter;

public class WindowHookManager : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

    private static readonly string[] BuiltInForceRouteProcessNames =
    {
        "PDVSiTef",
        "CliSiTef",
        "CliSiTefI",
        "CliSiTefL",
        "CliSiTef32",
        "SiTef",
        "SiTefCom",
        "SitefCom",
        "SitefClient",
        "InterfaceSiTef",
        "SESiTef"
    };

    private static readonly string[] ForceRouteTitleFragments =
    {
        "sitef",
        "pdvsitef",
        "clisitef"
    };

    private NpsConfig _config;
    private Screen _npsMonitor;
    private Screen _targetMonitor;
    private IntPtr _foregroundHook;
    private IntPtr _showHook;
    private IntPtr _createHook;
    private IntPtr _locationHook;
    private IntPtr _dialogHook;
    private WinEventDelegate? _foregroundDelegate;
    private WinEventDelegate? _showDelegate;
    private WinEventDelegate? _createDelegate;
    private WinEventDelegate? _locationDelegate;
    private WinEventDelegate? _dialogDelegate;
    private System.Windows.Forms.Timer? _scanTimer;
    private System.Windows.Forms.Timer? _pinTimer;
    private readonly HashSet<string> _exemptProcessNames;
    private readonly HashSet<IntPtr> _stickyHwnds = new();
    private readonly Dictionary<uint, (string Name, long Tick)> _processNameCache = new();
    private readonly Dictionary<IntPtr, long> _lastForceRouteLogTick = new();
    private HashSet<string> _forceRouteProcessNames;

    public bool RoutingPaused { get; set; }

    public WindowHookManager(NpsConfig config)
    {
        _config = config;
        _forceRouteProcessNames = BuildForceRouteProcessNames(_config);

        _exemptProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KioskBrowser",
            "NpsDisplay",
            "WindowRouter",
            "taskmgr",
            "ApplicationFrameHost"
        };

        var screens = Screen.AllScreens;
        _npsMonitor = Screen.PrimaryScreen ?? (screens.Length > 0 ? screens[0] : throw new InvalidOperationException("No screens"));
        _targetMonitor = _npsMonitor;

        SynchronizeRoutingLayout();

        Log.Information(
            "Window Router: Kiosk Monitor = {NPS}, Target Monitor = {Target}, FilterMode = {Mode}, MinVisibleArea = {MinArea}, ForceRoute = {Force}, DebugLogging = {Debug}",
            _npsMonitor.DeviceName, _targetMonitor.DeviceName,
            IsRelaxedMode() ? "Relaxed" : "Strict",
            EffectiveMinVisibleArea(),
            string.Join(",", _forceRouteProcessNames),
            _config.RouterDebugLogging);
    }

    public void Start()
    {
        _foregroundDelegate = WinEventProc;
        _showDelegate = WinEventProc;
        _createDelegate = WinEventProc;
        _locationDelegate = WinEventProc;
        _dialogDelegate = WinEventProc;

        var hookFlags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundDelegate, 0, 0, hookFlags);
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _showDelegate, 0, 0, hookFlags);
        _createHook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero, _createDelegate, 0, 0, hookFlags);
        _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _locationDelegate, 0, 0, hookFlags);
        _dialogHook = SetWinEventHook(EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART, IntPtr.Zero, _dialogDelegate, 0, 0, hookFlags);

        if (_foregroundHook == IntPtr.Zero || _showHook == IntPtr.Zero || _createHook == IntPtr.Zero
            || _locationHook == IntPtr.Zero || _dialogHook == IntPtr.Zero)
        {
            Log.Error("Failed to set window event hooks");
            throw new InvalidOperationException("Failed to set window event hooks");
        }

        _scanTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _scanTimer.Tick += (_, _) => SafeScanAndRoute();
        _scanTimer.Start();

        _pinTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pinTimer.Tick += (_, _) => PinStickyWindows();
        _pinTimer.Start();

        Log.Information("Window event hooks installed and scan/pin timers started");
    }

    public void Stop()
    {
        Unhook(ref _foregroundHook);
        Unhook(ref _showHook);
        Unhook(ref _createHook);
        Unhook(ref _locationHook);
        Unhook(ref _dialogHook);

        if (_scanTimer != null)
        {
            _scanTimer.Stop();
            _scanTimer.Dispose();
            _scanTimer = null;
        }

        if (_pinTimer != null)
        {
            _pinTimer.Stop();
            _pinTimer.Dispose();
            _pinTimer = null;
        }

        Log.Information("Window event hooks removed");
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero)
            return;
        UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (idChild != 0)
            return;

        if (idObject != OBJID_WINDOW && idObject != OBJID_CLIENT)
            return;

        try
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero)
                hwnd = root;

            if (eventType == EVENT_OBJECT_LOCATIONCHANGE && !_stickyHwnds.Contains(hwnd))
                return;

            RefreshLayoutFromRunningBrowserOnly();

            if (RoutingPaused)
                return;

            TrackAndRoute(hwnd);
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
                    TrackAndRoute(hWnd);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error scanning window");
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during periodic scan");
        }
    }

    private void PinStickyWindows()
    {
        if (RoutingPaused || _stickyHwnds.Count == 0)
            return;

        try
        {
            RefreshLayoutFromRunningBrowserOnly();

            foreach (var hwnd in _stickyHwnds.ToArray())
            {
                if (!IsWindow(hwnd))
                {
                    _stickyHwnds.Remove(hwnd);
                    _lastForceRouteLogTick.Remove(hwnd);
                    continue;
                }

                if (ShouldRouteWindow(hwnd, out _))
                    RouteWindowToTargetMonitor(hwnd, activate: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error pinning sticky windows");
        }
    }

    private void TrackAndRoute(IntPtr hwnd)
    {
        var force = IsForceRouteWindow(hwnd);
        if (force)
            _stickyHwnds.Add(hwnd);

        if (ShouldRouteWindow(hwnd, out var skipReason))
        {
            RouteWindowToTargetMonitor(hwnd, activate: force);
        }
        else if (!force && !string.IsNullOrEmpty(skipReason))
        {
            LogSkippedCandidate(hwnd, skipReason);
        }
    }

    private bool IsRelaxedMode() =>
        string.Equals(_config.RouterWindowFilterMode, "Relaxed", StringComparison.OrdinalIgnoreCase);

    private int EffectiveMinVisibleArea()
    {
        var v = _config.RouterMinVisibleArea;
        return v < 1 ? 400 : v;
    }

    private static HashSet<string> BuildForceRouteProcessNames(NpsConfig config)
    {
        var set = new HashSet<string>(BuiltInForceRouteProcessNames, StringComparer.OrdinalIgnoreCase);
        if (config.RouterForceProcessNames == null)
            return set;

        foreach (var name in config.RouterForceProcessNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            set.Add(name.Trim());
        }

        return set;
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

    private bool ShouldRouteWindow(IntPtr hwnd, out string? skipReason)
    {
        skipReason = null;

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        var rect = new RECT();
        if (!GetWindowRect(hwnd, ref rect))
            return false;

        var force = IsForceRouteWindow(hwnd);
        var onKioskMonitor = IsWindowOnKioskMonitor(hwnd, rect, out var intersectArea);

        if (!onKioskMonitor)
            return false;

        if (!force && intersectArea < EffectiveMinVisibleArea())
        {
            if (_config.RouterDebugLogging)
                skipReason = $"below_min_visible_area (intersect={intersectArea}, min={EffectiveMinVisibleArea()})";
            return false;
        }

        if (!force && !IsRelaxedMode())
        {
            uint style = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_STYLE).ToInt64());
            uint exStyle = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_EXSTYLE).ToInt64());

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
            if (!TryGetProcessName(hwnd, out var processName, out _))
                return force;

            if (_exemptProcessNames.Contains(processName))
            {
                if (_config.RouterDebugLogging)
                    skipReason = $"exempt_process:{processName}";
                return false;
            }

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
            return force;
        }
    }

    private bool IsWindowOnKioskMonitor(IntPtr hwnd, RECT rect, out int intersectArea)
    {
        var hasIntersect = TryGetKioskIntersectionArea(rect, out intersectArea);
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon != IntPtr.Zero && MonitorDeviceNameEqualsScreen(hMon, _npsMonitor))
            return true;
        return hasIntersect;
    }

    private bool IsForceRouteWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        if (TryGetProcessName(hwnd, out var processName, out _)
            && _forceRouteProcessNames.Contains(processName))
        {
            return true;
        }

        var title = GetWindowTitle(hwnd);
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var fragment in ForceRouteTitleFragments)
            {
                if (title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var cls = GetWindowClassName(hwnd);
        if (!string.IsNullOrEmpty(cls))
        {
            foreach (var fragment in ForceRouteTitleFragments)
            {
                if (cls.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool TryGetProcessName(IntPtr hwnd, out string processName, out uint processId)
    {
        processName = "";
        GetWindowThreadProcessId(hwnd, out processId);
        if (processId == 0)
            return false;

        var now = Environment.TickCount64;
        if (_processNameCache.TryGetValue(processId, out var cached) && now - cached.Tick < 2000)
        {
            processName = cached.Name;
            return true;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            _processNameCache[processId] = (processName, now);
            return true;
        }
        catch
        {
            _processNameCache.Remove(processId);
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
            TryGetProcessName(hwnd, out var procName, out _);
            if (string.IsNullOrEmpty(procName))
                procName = "?";

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

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0)
            return "";
        var sb = new StringBuilder(len + 1);
        return GetWindowTextW(hwnd, sb, sb.Capacity) <= 0 ? "" : sb.ToString();
    }

    private void RouteWindowToTargetMonitor(IntPtr hwnd, bool activate)
    {
        try
        {
            var rect = new RECT();
            if (!GetWindowRect(hwnd, ref rect))
                return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return;

            var newX = _targetMonitor.Bounds.Left + (_targetMonitor.Bounds.Width - width) / 2;
            var newY = _targetMonitor.Bounds.Top + (_targetMonitor.Bounds.Height - height) / 2;

            var wasMaximized = IsZoomed(hwnd);

            if (IsWindowAlreadyPlacedOnTarget(hwnd, rect, newX, newY, wasMaximized))
                return;

            if (wasMaximized)
                ShowWindow(hwnd, SW_RESTORE);

            if (!TryMoveWindow(hwnd, newX, newY, width, height, activate))
            {
                Log.Warning(
                    "SetWindowPos failed for HWND=0x{Hwnd:X} Class={Class} Title={Title} win32={Err}",
                    hwnd.ToInt64(), GetWindowClassName(hwnd), GetWindowTitle(hwnd), Marshal.GetLastWin32Error());
                return;
            }

            if (wasMaximized)
                ShowWindow(hwnd, SW_MAXIMIZE);

            if (activate)
                TryFocusWindow(hwnd);

            var still = new RECT();
            if (GetWindowRect(hwnd, ref still) && IsWindowOnKioskMonitor(hwnd, still, out _) && activate)
            {
                ShowWindow(hwnd, SW_HIDE);
                TryMoveWindow(hwnd, newX, newY, width, height, activate: true);
                ShowWindow(hwnd, SW_SHOW);
                TryFocusWindow(hwnd);
            }

            TryGetProcessName(hwnd, out var processName, out _);
            if (activate)
            {
                var now = Environment.TickCount64;
                if (!_lastForceRouteLogTick.TryGetValue(hwnd, out var last) || now - last >= 2000)
                {
                    _lastForceRouteLogTick[hwnd] = now;
                    Log.Information("Routed TEF/forced window from {Process} ({Title}) to target monitor",
                        processName, GetWindowTitle(hwnd));
                }
            }
            else
            {
                Log.Debug("Routed window from {Process} to target monitor", processName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to route window");
        }
    }

    private bool TryMoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool activate)
    {
        uint exStyle = unchecked((uint)GetWindowStylePtr(hwnd, GWLP_EXSTYLE).ToInt64());
        var isTopMost = (exStyle & WS_EX_TOPMOST) != 0;
        var insertAfter = isTopMost ? HWND_TOPMOST : HWND_NOTOPMOST;
        uint flags = SWP_NOACTIVATE | SWP_NOSIZE;
        if (!isTopMost)
            flags |= SWP_NOZORDER;
        if (activate)
            flags &= ~SWP_NOACTIVATE;

        if (SetWindowPos(hwnd, insertAfter, x, y, width, height, flags))
            return true;

        uint thisTid = GetCurrentThreadId();
        uint otherTid = GetWindowThreadProcessId(hwnd, out _);
        var attached = otherTid != 0 && otherTid != thisTid && AttachThreadInput(thisTid, otherTid, true);
        try
        {
            if (SetWindowPos(hwnd, insertAfter, x, y, width, height, flags))
                return true;

            if (SetWindowPos(hwnd, insertAfter, x, y, width, height, flags & ~SWP_NOSIZE))
                return true;

            return MoveWindow(hwnd, x, y, width, height, true);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisTid, otherTid, false);
        }
    }

    private static void TryFocusWindow(IntPtr hwnd)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
            var fg = GetForegroundWindow();
            uint selfTid = GetCurrentThreadId();
            uint fgTid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint targetTid = GetWindowThreadProcessId(hwnd, out _);

            var attachedFg = fgTid != 0 && fgTid != selfTid && AttachThreadInput(selfTid, fgTid, true);
            var attachedTarget = targetTid != 0 && targetTid != selfTid && AttachThreadInput(selfTid, targetTid, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attachedTarget)
                    AttachThreadInput(selfTid, targetTid, false);
                if (attachedFg)
                    AttachThreadInput(selfTid, fgTid, false);
            }
        }
        catch
        {
        }
    }

    private static bool IsExplorerShellChromeWindowClass(string className) =>
        string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase);

    private bool IsWindowAlreadyPlacedOnTarget(IntPtr hwnd, RECT rect, int newX, int newY, bool wasMaximized)
    {
        const int tol = 12;

        if (wasMaximized)
        {
            var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            return hMon != IntPtr.Zero && MonitorDeviceNameEqualsScreen(hMon, _targetMonitor);
        }

        if (Math.Abs(rect.Left - newX) <= tol && Math.Abs(rect.Top - newY) <= tol)
            return true;

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
        }

        return false;
    }

    private void SynchronizeRoutingLayout()
    {
        try
        {
            _config = ConfigManager.LoadConfig();
            _forceRouteProcessNames = BuildForceRouteProcessNames(_config);
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
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;
    private const int ASFW_ANY = -1;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

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
