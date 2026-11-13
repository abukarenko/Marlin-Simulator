using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace MarlinHost;

internal static class Program
{
    private const string PipeName = "MarlinHost-0.1-import";
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            if (args is ["--from-slicer", var temporary])
            {
                if (Environment.GetEnvironmentVariable("SLIC3R_PP_HOST") != "File") return;
                string target = Environment.GetEnvironmentVariable("SLIC3R_PP_OUTPUT_NAME") ?? throw new IOException("Нет пути экспорта PrusaSlicer.");
                string folder = Path.Combine(HostSettings.Data, "incoming", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string snapshot = Path.Combine(folder, Path.GetFileName(target));
                File.Copy(temporary, snapshot);
                StartHidden("--await-export", target, snapshot);
                return;
            }
            if (args is ["--await-export", var destination, var copy])
            {
                AwaitExport(destination, copy).GetAwaiter().GetResult();
                return;
            }
            using var mutex = new Mutex(true, "Local\\MarlinHost-0.1", out bool first);
            string? file = args.FirstOrDefault(a => !a.StartsWith("--"));
            if (!first) { if (file is not null) Deliver(file).GetAwaiter().GetResult(); return; }
            ApplicationConfiguration.Initialize();
            using var window = new HostForm();
            using var stop = new CancellationTokenSource();
            window.Shown += async (_, _) =>
            {
                if (file is not null) await window.Import(file);
                if (args.Contains("--smoke-ui"))
                {
                    if (args.Contains("--snapshot"))
                    {
                        string folder = Path.GetFullPath("artifacts/host-ui-qa"); Directory.CreateDirectory(folder);
                        await window.SaveSnapshot(Path.Combine(folder, "host.png"));
                    }
                    window.Close(); return;
                }
                _ = Listen(window, stop.Token);
            };
            Application.Run(window);
            stop.Cancel();
        }
        catch (Exception ex)
        {
            if (args.Contains("--smoke-ui")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "Marlin Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
    private static void StartHidden(params string[] args)
    {
        var info = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
    }
    private static async Task AwaitExport(string destination, string copy)
    {
        byte[] expected = SHA256.HashData(await File.ReadAllBytesAsync(copy));
        for (int i = 0; i < 240; i++)
        {
            try
            {
                // The postprocessor is called BEFORE final export. Wait for the actual final file.
                byte[] actual;
                using (var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                    actual = await SHA256.HashDataAsync(stream);
                if (CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    try { await Deliver(copy); }
                    catch (TimeoutException) { StartHidden(copy); }
                    return;
                }
            }
            catch (IOException) { }
            await Task.Delay(500);
        }
        File.WriteAllText(copy + ".error.txt", "Экспорт не подтверждён за 120 секунд. Откройте готовый G-code вручную.");
    }
    private static async Task Deliver(string path)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        await pipe.ConnectAsync(3000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(Path.GetFullPath(path));
    }
    private static async Task Listen(HostForm window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                string? path = await reader.ReadLineAsync(token);
                if (path is not null && !window.IsDisposed)
                    window.BeginInvoke(new Action(async () => await window.Import(path)));
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }
}
