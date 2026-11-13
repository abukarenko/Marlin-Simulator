using System.IO.Ports;
using System.Net.Sockets;
using MarlinEmulator.Core;

namespace MarlinEmulator.App;

/// <summary>Transparent byte transport: checksums, line numbers and responses belong to Marlin.</summary>
internal sealed class SerialBridge : IAsyncDisposable
{
    private readonly TcpClient tcp = new();
    private readonly SerialPort serial;
    private readonly CancellationTokenSource lifetime = new();
    private Task? pump;
    private bool stopping;
    public event Action<string>? Closed;

    public SerialBridge(string port, int baud) => serial = new SerialPort(port, baud)
    {
        Handshake = Handshake.None, DtrEnable = false, RtsEnable = false,
        ReadTimeout = 500, WriteTimeout = 1000
    };

    public async Task StartAsync()
    {
        await tcp.ConnectAsync("127.0.0.1", 8099);
        serial.Open();
        pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        string message = "COM-порт отключён";
        try
        {
            var network = tcp.GetStream();
            var fromPort = SerialRelay.CopyAsync(serial.BaseStream, network, lifetime.Token);
            var toPort = network.CopyToAsync(serial.BaseStream, lifetime.Token);
            var first = await Task.WhenAny(fromPort, toPort);
            await first;
            message = "Принтер завершил работу. COM-порт отключён.";
            lifetime.Cancel();
            serial.Close();
            tcp.Close();
            try { await Task.WhenAll(fromPort, toPort); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            message = ex.Message;
        }
        finally
        {
            if (!stopping) Closed?.Invoke(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        lifetime.Cancel();
        serial.Dispose();
        tcp.Dispose();
        if (pump is not null) await pump;
        lifetime.Dispose();
    }
}
