using System.Net;
using System.Net.Sockets;
using System.Text;
using MarlinEmulator.Core;
using MarlinEmulator.App;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

if (args is ["--bridge-idle", var comPort])
{
    await using var bridge = new SerialBridge(comPort, 115200);
    var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    bridge.Closed += reason => closed.TrySetResult(reason);
    await bridge.StartAsync();
    var result = await Task.WhenAny(closed.Task, Task.Delay(3000));
    Check(result != closed.Task, "COM bridge closed while idle: " + (closed.Task.IsCompleted ? closed.Task.Result : ""));
    Console.WriteLine("PASS: real " + comPort + " bridge stays connected while idle.");
    return;
}

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
await using var session = new FirmwareSession();
var accepting = listener.AcceptTcpClientAsync();
await session.ConnectAsync(port);
using var server = await accepting;
using var reader = new StreamReader(server.GetStream(), Encoding.ASCII, leaveOpen: true);
using var writer = new StreamWriter(server.GetStream(), Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

// Temperature and busy messages must not release the sender before Marlin's ok.
var heat = session.SendCommandAsync("M109 S200");
Check(await reader.ReadLineAsync() == "M109 S200", "Heating command lost");
await writer.WriteLineAsync("T:180.0 /200.0 B:60.0 /60.0");
await writer.WriteLineAsync("echo:busy: processing");
var position = session.SendCommandAsync("M114");
await Task.Delay(50);
Check(!heat.IsCompleted && !position.IsCompleted && !server.GetStream().DataAvailable, "Command sent before ok");
await writer.WriteLineAsync("ok");
await heat.WaitAsync(TimeSpan.FromSeconds(2));
Check(await reader.ReadLineAsync() == "M114", "Queued command lost");
await writer.WriteLineAsync("X:0 Y:0 Z:0 E:0");
await writer.WriteLineAsync("ok N2 P15 B3");
await position.WaitAsync(TimeSpan.FromSeconds(2));

// Emergency stop bypasses an in-flight heating command.
heat = session.SendCommandAsync("M109 S210");
Check(await reader.ReadLineAsync() == "M109 S210", "Second command lost");
await session.EmergencyStopAsync();
Check(await reader.ReadLineAsync() == "M112", "Emergency command blocked by pending command");
await writer.WriteLineAsync("ok");
await heat.WaitAsync(TimeSpan.FromSeconds(2));

// A firmware error stops transmission instead of treating a later ok as success.
var bad = session.SendCommandAsync("G9999");
Check(await reader.ReadLineAsync() == "G9999", "Error command lost");
await writer.WriteLineAsync("Error:test failure");
try { await bad; throw new Exception("Firmware error ignored"); }
catch (IOException ex) { Check(ex.Message.Contains("test failure"), "Wrong failure"); }
Check(!session.IsConnected, "Uncertain protocol state must disconnect");
listener.Stop();
Console.WriteLine("PASS: acknowledgement ordering, busy/temperature replies, emergency bypass, firmware errors.");

using var relayStop = new CancellationTokenSource();
using var serialInput = new IdleSerialInput();
using var relayOutput = new SignalingOutput();
var relay = SerialRelay.CopyAsync(serialInput, relayOutput, relayStop.Token);
await relayOutput.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
relayStop.Cancel();
try { await relay; } catch (OperationCanceledException) { }
Check(Encoding.ASCII.GetString(relayOutput.ToArray()) == "M115\nM105\n", "Idle COM read or timeout closed the relay");
Console.WriteLine("PASS: COM idle reads, timeouts, subsequent traffic, cancellation.");

var firmwareListener = new TcpListener(IPAddress.Loopback, 0);
firmwareListener.Start();
await using (var tcp = new TcpConnection(0, ((IPEndPoint)firmwareListener.LocalEndpoint).Port))
{
    tcp.Start();
    for (int attempt = 0; attempt < 2; attempt++)
    {
        using var host = new TcpClient();
        await host.ConnectAsync(IPAddress.Loopback, tcp.Port);
        using var firmwarePeer = await firmwareListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
        byte[] request = Encoding.ASCII.GetBytes("N1 M105*38\r\n");
        await host.GetStream().WriteAsync(request);
        byte[] received = new byte[request.Length];
        await firmwarePeer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(received.SequenceEqual(request), "TCP request changed");
        byte[] response = Encoding.ASCII.GetBytes("ok T:50.0 /50.0\n");
        await firmwarePeer.GetStream().WriteAsync(response);
        received = new byte[response.Length];
        await host.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(received.SequenceEqual(response), "TCP response changed");
        host.Close();
        var eof = await firmwarePeer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(eof == 0, "TCP disconnect did not release firmware");
    }
}
firmwareListener.Stop();
Console.WriteLine("PASS: TCP bidirectional bytes, disconnect, reconnect, listener shutdown.");

var windowSettings = new WindowPreferences { Transport="TCP", Port="COM42", Baud="250000", TcpPort=17000, Width=1234, Height=789, Maximized=true };
windowSettings.Save();
var loadedSettings = WindowPreferences.Load();
Check(loadedSettings.Transport=="TCP" && loadedSettings.Port=="COM42" && loadedSettings.Baud=="250000" && loadedSettings.TcpPort==17000 && loadedSettings.Width==1234 && loadedSettings.Maximized, "Window preferences not restored");
var settingsFile = Path.Combine(AppContext.BaseDirectory,"data","ui-settings.json");
File.WriteAllText(settingsFile,"invalid JSON");
Check(WindowPreferences.Load().TcpPort==7000,"Malformed preferences did not restore defaults");
File.Delete(settingsFile);
Console.WriteLine("PASS: connection/window preferences, reload, malformed-file defaults.");

sealed class IdleSerialInput : MemoryStream
{
    private int step;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        switch (step++)
        {
            case 0: return 0;
            case 1: return Copy("M115\n", buffer);
            case 2: throw new TimeoutException();
            case 3: return Copy("M105\n", buffer);
            default: await Task.Delay(Timeout.Infinite, token); return 0;
        }
    }
    private static int Copy(string text, Memory<byte> buffer) => Encoding.ASCII.GetBytes(text.AsSpan(), buffer.Span);
}

sealed class SignalingOutput : MemoryStream
{
    public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        await base.WriteAsync(bytes, token);
        if (Length == 10) Completed.TrySetResult();
    }
}
