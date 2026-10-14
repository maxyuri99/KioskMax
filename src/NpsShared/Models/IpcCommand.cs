namespace NpsShared.Models;

/// <summary>
/// IPC command types for communication between components
/// </summary>
public enum IpcCommandType
{
    Ping,
    Reload,
    SetUrl,
    SetMonitor,
    ToggleTopMost,
    Shutdown,
    GetStatus,
    SetKioskContext,
    NavigateLocal,
    PostToPage
}

/// <summary>
/// IPC command message
/// </summary>
public class IpcCommand
{
    public IpcCommandType CommandType { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();

    public IpcCommand()
    {
    }

    public IpcCommand(IpcCommandType commandType)
    {
        CommandType = commandType;
    }

    public IpcCommand(IpcCommandType commandType, Dictionary<string, string> parameters)
    {
        CommandType = commandType;
        Parameters = parameters;
    }
}

/// <summary>
/// IPC response message
/// </summary>
public class IpcResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Data { get; set; } = new();

    public IpcResponse()
    {
    }

    public IpcResponse(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public IpcResponse(bool success, string message, Dictionary<string, string> data)
    {
        Success = success;
        Message = message;
        Data = data;
    }
}

