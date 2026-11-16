using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MarlinHost;

public sealed class PrinterLink : IAsyncDisposable
{
    private TcpClient? tcp;
    private SerialPort? serial;
    private Stream? output;
    private Task? reader;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim writes = new(1);
    private readonly object sync = new();
    private TaskCompletionSource<string[]>? pending;
    private bool pendingTerminal;
    private readonly List<string> replies = [];
    private int? resend;
    private int number;
    private bool numbered;
    private int disposed;
    public bool Connected { get; private set; }
    public event Action<string>? Received;
    public event Action<string>? Sent;
    public event Action<string>? Lost;
    public event Action<string, bool>? TerminalTraffic;

    public async Task ConnectTcp(string host, int port)
    {
        tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        output = tcp.GetStream();
        Connected = true;
        reader = ReadTcp(output);
        await Initialize();
    }

    public async Task ConnectSerial(string port, int baud)
    {
        serial = new SerialPort(port, baud) { ReadTimeout = 250, WriteTimeout = 2000, DtrEnable = false, RtsEnable = false, NewLine = "\n" };
        serial.Open();
        output = serial.BaseStream;
        Connected = true;
        reader = Task.Run(ReadSerial);
        // Allow boards that reset on opening the port to finish their startup.
        await Task.Delay(2000, lifetime.Token);
        await Initialize();
    }

    private async Task Initialize()
    {
        await Send("M110 N0");
        numbered = true;
        number = 0;
        await Send("M115");
    }

    private async Task ReadTcp(Stream stream)
    {
        try
        {
            using var input = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            while (await input.ReadLineAsync(lifetime.Token) is { } line) ProcessLine(line);
            if (!lifetime.IsCancellationRequested) Fail("Принтер закрыл соединение.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { if (!lifetime.IsCancellationRequested) Fail(ex.Message); }
    }

    private void ReadSerial()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
                try { ProcessLine(serial!.ReadLine().TrimEnd('\r')); }
                catch (TimeoutException) { }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { if (!lifetime.IsCancellationRequested) Fail(ex.Message); }
    }

    private void ProcessLine(string line)
    {
        bool terminal;
        lock (sync) terminal = pending is not null && pendingTerminal;
        if (terminal) TerminalTraffic?.Invoke(line, true);
        Received?.Invoke(line);
        lock (sync)
        {
            if (line == "start" && pending is not null) { Fail("Прошивка перезапустилась. Задание остановлено."); return; }
            if (pending is null) return;
            replies.Add(line);
            var request = Regex.Match(line, @"^Resend:\s*(\d+)", RegexOptions.IgnoreCase);
            if (request.Success) resend = int.Parse(request.Groups[1].Value);
            else if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("!!"))
            {
                // Marlin follows protocol errors with Resend and ok. Consume that entire reply first.
                if (!line.Contains("checksum", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Line Number", StringComparison.OrdinalIgnoreCase))
                    Fail(line);
            }
            else if (line == "ok" || line.StartsWith("ok ")) pending.TrySetResult(replies.ToArray());
        }
    }

    private void Fail(string reason)
    {
        if (!Connected) return;
        Connected = false;
        lock (sync) pending?.TrySetException(new IOException(reason));
        Lost?.Invoke(reason);
    }

    public async Task<string[]> Send(string command, bool terminal = false)
    {
        if (command.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("Одна команда на строку.");
        if (numbered && GCode.Op(command) == "M110") throw new ArgumentException("M110 управляется приложением и недоступна вручную.");
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (!Connected) throw new IOException("Нет связи с принтером.");
            int current = numbered ? ++number : 0;
            string payload = numbered ? $"N{current} {command}" : command;
            string wire = numbered ? payload + "*" + Encoding.ASCII.GetBytes(payload).Aggregate(0, (a, b) => a ^ b) : payload;
            if (wire.Length >= 96) throw new IOException("Команда длиннее входного буфера Marlin (96 байт): " + command);
            for (int retry = 0; retry < 4; retry++)
            {
                var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (sync) { pending = completion; pendingTerminal = terminal; replies.Clear(); resend = null; }
                if (terminal) TerminalTraffic?.Invoke(command, false);
                await Raw(wire);
                Sent?.Invoke(command);
                var result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(15), lifetime.Token);
                int? repeat;
                lock (sync) { repeat = resend; pending = null; }
                if (repeat is null) return result;
                if (repeat != current) throw new IOException($"Прошивка запросила строку {repeat}, ожидалась {current}. Продолжение невозможно.");
            }
            throw new IOException("Превышено число повторных передач.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            throw;
        }
        finally { lock (sync) pending = null; gate.Release(); }
    }

    public async Task Raw(string command)
    {
        await writes.WaitAsync(lifetime.Token);
        try
        {
            if (serial is not null) await Task.Run(() => serial.WriteLine(command));
            else if (output is not null) await output.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), lifetime.Token);
            else throw new IOException("Нет соединения.");
        }
        finally { writes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Connected = false;
        lifetime.Cancel();
        lock (sync) pending?.TrySetCanceled();
        tcp?.Dispose();
        serial?.Close();
        if (reader is not null) await reader;
        await gate.WaitAsync(); gate.Release();
        serial?.Dispose();
        lifetime.Dispose();
    }
}
