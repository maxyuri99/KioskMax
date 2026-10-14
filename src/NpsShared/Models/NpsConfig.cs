namespace NpsShared.Models;

/// <summary>
/// Configuration model for KioskMax
/// </summary>
public class NpsConfig
{
    public string NpsUrl { get; set; } = "https://nps-caixas-frente-caixa-teste.6yhsxt.easypanel.host/";
    public string MonitorPolicy { get; set; } = "Primary"; // "Explicit" or "Primary"
    public string MonitorDeviceId { get; set; } = string.Empty;
    public int MonitorIndex { get; set; } = 0;
    public bool TopMost { get; set; } = true;
    public bool KioskFullScreen { get; set; } = true;
    public bool AutoRecover { get; set; } = true;
    public string DpiMode { get; set; } = "PerMonitorV2";
    public bool RouterEnabled { get; set; } = true;
    public string RouterTarget { get; set; } = "Opposite"; // "Opposite" or explicit index
    /// <summary>
    /// Strict: legacy filters (skip WS_EX_TOOLWINDOW and caption-less popups).
    /// Relaxed: route those windows too if they visibly intersect the kiosk monitor (helps PDV/TEF popups).
    /// </summary>
    public string RouterWindowFilterMode { get; set; } = "Relaxed";
    /// <summary>
    /// Extra process names (without .exe) that are always pulled off the kiosk monitor.
    /// Built-in SiTef names are always included even if this list is empty.
    /// </summary>
    public List<string> RouterForceProcessNames { get; set; } = new();
    /// <summary>
    /// Minimum area in pixels (of the rectangle intersecting the kiosk monitor) required to consider routing.
    /// </summary>
    public int RouterMinVisibleArea { get; set; } = 400;
    /// <summary>
    /// When true, log windows that intersect the kiosk monitor but were not routed (and SetWindowPos failures).
    /// </summary>
    public bool RouterDebugLogging { get; set; }
    /// <summary>When true, Ctrl+Alt+RouterPauseHotKeyVk toggles pause (WindowRouter process).</summary>
    public bool RouterPauseHotKeyEnabled { get; set; } = true;
    /// <summary>Win32 virtual key (1–254). Default 0x50 = 'P'. Example: 0x31 = '1'.</summary>
    public int RouterPauseHotKeyVk { get; set; } = 0x50;
    public string LogLevel { get; set; } = "Information";
    public string GestaoApiUrl { get; set; } = string.Empty;
    public string KioskIdentificador { get; set; } = string.Empty;
    public KioskContexto? KioskContexto { get; set; }
    public bool TefAtivo { get; set; } = true;
    public int UltimaVersaoConfig { get; set; }
    public string UltimaSituacao { get; set; } = string.Empty;
    public string TelaLocal { get; set; } = string.Empty;
    public bool GestaoExibir { get; set; } = true;
    public TefImpressaoConfig? TefGestao { get; set; }
}

