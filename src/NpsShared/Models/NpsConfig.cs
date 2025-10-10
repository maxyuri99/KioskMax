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
    public string LogLevel { get; set; } = "Information";
}

