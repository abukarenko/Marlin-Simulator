using System.Net.Sockets;
using System.Text;

namespace MarlinEmulator.Core;

/// <summary>A host connection to real Marlin. G-code is never interpreted here.</summary>
public sealed class FirmwareSession : IAsyncDisposable
{
    private readonly TcpClient client = new();
    private readonly SemaphoreSlim commandGate = new(1);
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private TaskCompletionSource? acknowledgement;
    private Task? reader;
    private bool connected;
    public event Action<string>? LineReceived;
    public event Action<string>? CommandSent;
    public event Action<string>? Disconnected;
    public bool IsConnected => connected;

    public async Task ConnectAsync(int port = 8099, CancellationToken token = default)
    {
        await client.ConnectAsync("127.0.0.1", port, token);
        connected = true;
        reader = ReadAsync();
    }

    private async Task ReadAsync()
    {
        string reason = "Соединение с прошивкой закрыто";
        try
        {
            using var input = new StreamReader(client.GetStream(), Encoding.ASCII, false, 4096, true);
            while (await input.ReadLineAsync(lifetime.Token) is { } line)
            {
                LineReceived?.Invoke(line);
                lock (gate)
                {
                    if (line == "ok" || line.StartsWith("ok ", StringComparison.Ordinal))
                        acknowledgement?.TrySetResult();
                    else if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("Resend:", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("!!", StringComparison.Ordinal))
                        acknowledgement?.TrySetException(new IOException(line));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            reason = ex.Message;
        }
        finally
        {
            connected = false;
            lock (gate) acknowledgement?.TrySetException(new IOException(reason));
            if (!lifetime.IsCancellationRequested) Disconnected?.Invoke(reason);
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken token = default)
    {
        if (!connected) throw new IOException("Прошивка не подключена");
        if (command.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Отправляйте одну строку за раз");
        if (string.IsNullOrWhiteSpace(command)) return;
        await commandGate.WaitAsync(token);
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) acknowledgement = completion;
            await SendRawAsync(Encoding.ASCII.GetBytes(command + "\n"), token);
            CommandSent?.Invoke(command);
            // Heating and homing can legitimately take minutes. A timeout must not
            // let a late 'ok' acknowledge a different command on this connection.
            try { await completion.Task.WaitAsync(TimeSpan.FromMinutes(15), token); }
            catch
            {
                lifetime.Cancel();
                client.Close();
                connected = false;
                throw;
            }
        }
        finally
        {
            lock (gate) acknowledgement = null;
            commandGate.Release();
        }
    }

    public async Task SendRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        await writeGate.WaitAsync(token);
        try { await client.GetStream().WriteAsync(bytes, token); }
        finally { writeGate.Release(); }
    }

    public Task EmergencyStopAsync() => SendRawAsync(Encoding.ASCII.GetBytes("M112\n"));

    public async Task PrintFileAsync(string path, IProgress<int>? progress = null, CancellationToken token = default)
    {
        int count = 0;
        using var file = new StreamReader(path);
        while (await file.ReadLineAsync(token) is { } line)
        {
            int comment = line.IndexOf(';');
            string command = (comment >= 0 ? line[..comment] : line).Trim();
            if (command.Length == 0) continue;
            await SendCommandAsync(command, token);
            progress?.Report(++count);
        }
        await SendCommandAsync("M400", token);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        client.Dispose();
        if (reader is not null) await reader;
        await commandGate.WaitAsync();
        commandGate.Release();
        lifetime.Dispose();
    }
}
