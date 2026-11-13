using System.Net;
using System.Net.Sockets;

namespace MarlinEmulator.Core;

/// <summary>Accepts a host connection and forwards its byte stream to Marlin.</summary>
public sealed class TcpConnection : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly int firmwarePort;
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public event Action<string>? Message;

    public TcpConnection(int port, int firmwarePort = 8099)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        this.firmwarePort = firmwarePort;
    }

    public void Start()
    {
        listener.Start();
        worker = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var host = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var firmware = new TcpClient();
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                try
                {
                    await firmware.ConnectAsync(IPAddress.Loopback, firmwarePort, transfer.Token);
                    Message?.Invoke("TCP: управляющая программа подключена.");
                    var input = host.GetStream().CopyToAsync(firmware.GetStream(), transfer.Token);
                    var output = firmware.GetStream().CopyToAsync(host.GetStream(), transfer.Token);
                    try { await await Task.WhenAny(input, output); }
                    finally
                    {
                        transfer.Cancel();
                        host.Close(); firmware.Close();
                        try { await Task.WhenAll(input, output); }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    }
                    Message?.Invoke("TCP: программа отключилась. Ожидание подключения.");
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    if (!lifetime.IsCancellationRequested) Message?.Invoke("TCP: " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (lifetime.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        if (worker is not null) await worker;
        lifetime.Dispose();
    }
}
