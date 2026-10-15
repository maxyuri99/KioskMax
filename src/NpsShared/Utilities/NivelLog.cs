using NpsShared.Configuration;
using Serilog.Events;

namespace NpsShared.Utilities;

public static class NivelLog
{
    public static LogEventLevel Ler()
    {
        try
        {
            var texto = ConfigManager.LoadConfig().LogLevel;
            if (Enum.TryParse<LogEventLevel>(texto, true, out var nivel))
                return nivel;
        }
        catch
        {
        }

        return LogEventLevel.Information;
    }
}
