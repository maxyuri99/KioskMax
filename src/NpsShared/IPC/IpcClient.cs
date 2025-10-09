using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NpsShared.Models;
using Serilog;

namespace NpsShared.IPC;

/// <summary>
/// IPC client for communicating with NpsDisplay
/// </summary>
public class IpcClient
{
    private const string PipeName = "NpsDisplay_IPC";
    private const int TimeoutMs = 5000;

    /// <summary>
    /// Sends a command to NpsDisplay and waits for response
    /// </summary>
    public static async Task<IpcResponse> SendCommandAsync(IpcCommand command)
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            await pipeClient.ConnectAsync(TimeoutMs);

            using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipeClient, Encoding.UTF8, leaveOpen: true);

            var requestJson = JsonSerializer.Serialize(command);
            await writer.WriteLineAsync(requestJson);
            Log.Debug("IPC sent: {Request}", requestJson);

            var responseJson = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(responseJson))
            {
                return new IpcResponse(false, "No response received");
            }

            Log.Debug("IPC received: {Response}", responseJson);

            var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
            return response ?? new IpcResponse(false, "Invalid response format");
        }
        catch (TimeoutException)
        {
            Log.Warning("IPC command timed out: {CommandType}", command.CommandType);
            return new IpcResponse(false, "Connection timeout");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "IPC command failed: {CommandType}", command.CommandType);
            return new IpcResponse(false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if NpsDisplay is running and responding
    /// </summary>
    public static async Task<bool> IsDisplayRunningAsync()
    {
        var response = await SendCommandAsync(new IpcCommand(IpcCommandType.Ping));
        return response.Success;
    }

    /// <summary>
    /// Sends a reload command to NpsDisplay
    /// </summary>
    public static async Task<IpcResponse> ReloadAsync()
    {
        return await SendCommandAsync(new IpcCommand(IpcCommandType.Reload));
    }

    /// <summary>
    /// Changes the URL displayed by NpsDisplay
    /// </summary>
    public static async Task<IpcResponse> SetUrlAsync(string url)
    {
        var command = new IpcCommand(IpcCommandType.SetUrl, new Dictionary<string, string>
        {
            { "url", url }
        });
        return await SendCommandAsync(command);
    }

    /// <summary>
    /// Changes the monitor used by NpsDisplay
    /// </summary>
    public static async Task<IpcResponse> SetMonitorAsync(int monitorIndex)
    {
        var command = new IpcCommand(IpcCommandType.SetMonitor, new Dictionary<string, string>
        {
            { "monitorIndex", monitorIndex.ToString() }
        });
        return await SendCommandAsync(command);
    }

    /// <summary>
    /// Toggles TopMost state
    /// </summary>
    public static async Task<IpcResponse> ToggleTopMostAsync()
    {
        return await SendCommandAsync(new IpcCommand(IpcCommandType.ToggleTopMost));
    }

    /// <summary>
    /// Shuts down NpsDisplay
    /// </summary>
    public static async Task<IpcResponse> ShutdownAsync()
    {
        return await SendCommandAsync(new IpcCommand(IpcCommandType.Shutdown));
    }

    /// <summary>
    /// Gets status information from NpsDisplay
    /// </summary>
    public static async Task<IpcResponse> GetStatusAsync()
    {
        return await SendCommandAsync(new IpcCommand(IpcCommandType.GetStatus));
    }
}

