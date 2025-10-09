using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using NpsShared.Models;
using Serilog;

namespace NpsShared.IPC;

/// <summary>
/// IPC server using Named Pipes
/// </summary>
public class IpcServer : IDisposable
{
    private const string PipeName = "NpsDisplay_IPC";
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<IpcCommand, Task<IpcResponse>> _commandHandler;
    private Task? _serverTask;

    public IpcServer(Func<IpcCommand, Task<IpcResponse>> commandHandler)
    {
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
    }

    /// <summary>
    /// Starts the IPC server
    /// </summary>
    public void Start()
    {
        _serverTask = Task.Run(() => RunServerAsync(_cancellationTokenSource.Token));
        Log.Information("IPC Server started on pipe: {PipeName}", PipeName);
    }

    /// <summary>
    /// Stops the IPC server
    /// </summary>
    public void Stop()
    {
        _cancellationTokenSource.Cancel();
        _serverTask?.Wait(TimeSpan.FromSeconds(5));
        Log.Information("IPC Server stopped");
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Create a server and wait for a single client, handle it inline,
                // then loop again to accept the next client. This avoids disposing
                // the pipe while a background handler is still using it (which was
                // causing "Pipe is broken" on the client side).
                using var pipeServer = CreatePipeServer();
                await pipeServer.WaitForConnectionAsync(cancellationToken);

                await HandleClientAsync(pipeServer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in IPC server loop");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var pipeSecurity = new PipeSecurity();
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous,
            4096,
            4096,
            pipeSecurity
        );
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipeServer)
    {
        try
        {
            using var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipeServer, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var requestJson = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestJson))
            {
                return;
            }

            Log.Debug("IPC received: {Request}", requestJson);

            var command = JsonSerializer.Deserialize<IpcCommand>(requestJson);
            if (command == null)
            {
                var errorResponse = new IpcResponse(false, "Invalid command format");
                await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
                return;
            }

            var response = await _commandHandler(command);
            var responseJson = JsonSerializer.Serialize(response);

            await writer.WriteLineAsync(responseJson);
            Log.Debug("IPC sent: {Response}", responseJson);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling IPC client");
        }
    }

    public void Dispose()
    {
        Stop();
        _cancellationTokenSource.Dispose();
    }
}

