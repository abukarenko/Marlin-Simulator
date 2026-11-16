using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.IO.Pipes;
using System.Reflection;
using MarlinHost;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Until(Func<bool> condition, int seconds = 10)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("State timeout"); await Task.Delay(10); }
}

if (args.Contains("--four-point"))
{
    string fixture=Path.GetFullPath("artifacts/four-point-test.gcode"); Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
    File.WriteAllText(fixture,"G28\nG21\nG90\nM83\nM140 S50\nG1 X20 Y30 Z2\nG1 E2 F120\n;TYPE:Skirt/Brim\nG1 X10 Y10 E1\nG1 X90 Y90\n;TYPE:External perimeter\nG1 X110 Y90 E1\nG1 X110 Y110 E1\nG1 X90 Y110 E1\nG1 X90 Y90 E1\nM104 S0\n");
    var job4=PrintJob.Load(fixture);
    Check(job4.ModelFootprint==new ProbeRectangle(90,110,90,110),"Skirt included in model footprint");
    var area4=FourPointLeveling.Plan(job4,new(10,200,20,200));
    Check(area4==new ProbeRectangle(88,112,88,112),"Model probing margin wrong");
    foreach(string scenario in new[]{"success","unsupported","failed","out-of-bounds","no-home","cancel"})
    {
        var listener4=new TcpListener(IPAddress.Loopback,0); listener4.Start();
        var trace4=new System.Collections.Concurrent.ConcurrentQueue<string>();
        var probeStarted4=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout4=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serve4=Task.Run(async ()=>
        {
            using var client4=await listener4.AcceptTcpClientAsync(timeout4.Token);
            using var reader4=new StreamReader(client4.GetStream());
            using var writer4=new StreamWriter(client4.GetStream()){AutoFlush=true};
            while(await reader4.ReadLineAsync(timeout4.Token) is {} wire4)
            {
                string command4=Regex.Replace(wire4,@"^N\d+ (.*)\*\d+$","$1"); trace4.Enqueue(command4);
                if(command4=="M115" && scenario!="unsupported") await writer4.WriteLineAsync("Cap:FOUR_POINT_LEVELING:1");
                if(command4=="M211") await writer4.WriteLineAsync("Min: X0 Y0 Z0 Max: X210 Y210 Z160");
                if(command4=="M114") await writer4.WriteLineAsync("X:20 Y:30 Z:2 E:0");
                if(command4=="G29 K0") await writer4.WriteLineAsync(scenario=="out-of-bounds" ? "FOUR_POINT_CAPS V1 L10 R80 F20 B200" : "FOUR_POINT_CAPS V1 L10 R200 F20 B200");
                if(command4.StartsWith("G29 K4"))
                {
                    probeStarted4.TrySetResult();
                    if(scenario=="cancel") await Task.Delay(200,timeout4.Token);
                    for(int corner4=1;corner4<=4;corner4++) await writer4.WriteLineAsync($"FOUR_POINT_POINT {corner4} Z0.01");
                    await writer4.WriteLineAsync(scenario=="failed" ? "FOUR_POINT_FAILED" : "FOUR_POINT_OK");
                }
                await writer4.WriteLineAsync("ok");
            }
        });
        await using(var link4=new PrinterLink())
        {
            await link4.ConnectTcp("127.0.0.1",((IPEndPoint)listener4.LocalEndpoint).Port);
            var controller4=new PrintController(link4); await controller4.Initialize();
            var runJob4=scenario=="no-home" ? job4 with { Commands=job4.Commands[1..], FirstExtrusionCommand=job4.FirstExtrusionCommand-1 } : job4;
            var task4=controller4.Run(runJob4,true);
            if(scenario=="cancel") { await probeStarted4.Task.WaitAsync(TimeSpan.FromSeconds(5)); controller4.Cancel(); }
            bool failed4=false; try { await task4; } catch(IOException) { failed4=true; }
            var sent4=trace4.ToArray();
            if(scenario=="success")
            {
                int probe4=Array.FindIndex(sent4,c=>c.StartsWith("G29 K4"));
                int extrude4=Array.IndexOf(sent4,"G1 E2 F120");
                Check(!failed4 && controller4.State==JobState.Completed && probe4>=0 && extrude4>probe4,"Probe not completed before first extrusion");
                Check(sent4[probe4]=="G29 K4 L88 R112 F88 B112 V1","Wrong probe rectangle command");
                Check(sent4.Skip(probe4).Take(extrude4-probe4).Contains("G1 X20 Y30 F3000") && sent4.Skip(probe4).Take(extrude4-probe4).Contains("M83"),"Return position or relative E mode not restored");
            }
            else Check(!sent4.Contains("G1 E2 F120"),"Extrusion started despite probe failure/cancel");
            if(scenario is "unsupported" or "out-of-bounds" or "no-home") Check(!sent4.Any(c=>c.StartsWith("G29 K4")),"Unsafe/unsupported probe dispatched");
            if(scenario=="unsupported") Check(!sent4.Contains("G29 K0"),"Old firmware received dangerous capability query");
            if(scenario=="cancel") Check(!failed4 && controller4.State==JobState.Cancelled && sent4.Contains("M104 S0"),"Probe cancellation did not stop job/heaters");
            if(scenario is "failed" or "out-of-bounds" or "unsupported" or "no-home") Check(failed4,"Invalid four-point setup accepted");
            Check(!sent4.Any(c=>c is "M500" or "M501"),"Probing wrote/reloaded EEPROM");
        }
        await serve4; listener4.Stop();
        Console.WriteLine("PASS four-point: "+scenario);
    }
    return;
}

if (args.Contains("--ui"))
{
    var uiListener = new TcpListener(IPAddress.Loopback, 0); uiListener.Start();
    int uiPort = ((IPEndPoint)uiListener.LocalEndpoint).Port;
    var uiCommands=new System.Collections.Concurrent.ConcurrentQueue<string>();
    var serving = Task.Run(async () =>
    {
        using var client = await uiListener.AcceptTcpClientAsync();
        using var reader = new StreamReader(client.GetStream());
        using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } command)
        {
            uiCommands.Enqueue(command);
            if(Regex.IsMatch(command,@"\bG1\b")) await Task.Delay(5);
            if (command.Contains("M211")) await writer.WriteLineAsync("  Min: X0 Y0 Z0  Max: X220 Y220 Z250");
            if (command.Contains("M105")) await writer.WriteLineAsync("T:205.0 /230.0 B:85.0 /85.0");
            if (command.Contains("M114")) await writer.WriteLineAsync("X:20.00 Y:30.00 Z:2.00 E:5.00");
            if (command.Contains("M119")) await writer.WriteLineAsync("x_min: open\nx_max: open\ny_min: open\ny_max: open\nz_min: TRIGGERED\nz_max: open");
            await writer.WriteLineAsync("ok");
        }
    });
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            Application.EnableVisualStyles();
            using var form = new HostForm();
            object Field(string name) => typeof(HostForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            Task Call(string name) => (Task)typeof(HostForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null)!;
            form.Shown += async (_, _) =>
            {
                try
                {
                    ((ComboBox)Field("transport")).SelectedItem = "TCP";
                    ((TextBox)Field("address")).Text = "127.0.0.1";
                    ((NumericUpDown)Field("port")).Value = uiPort;
                    await Call("ToggleConnection");
                    int uiFileArg=Array.IndexOf(args,"--file");
                    await form.Import(uiFileArg<0 ? Path.GetFullPath("examples/square.gcode") : Path.GetFullPath(args[uiFileArg+1]));
                    await Task.Delay(300);
                    Check(((ComboBox)Field("transport")).Text == "TCP", "Transport selector lost value");
                    Check(((Button)Field("printButton")).Enabled, "Print button disabled after load and connection");
                    Check(((CheckBox)Field("additionalLeveling")).Enabled && ((Label)Field("levelingArea")).Text!="Загрузите модель","Four-point option/footprint absent after import");
                    var sidebar=form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().Single(p=>p.AutoScroll);
                    Check(!sidebar.HorizontalScroll.Visible,"Sidebar controls clipped by horizontal scrolling");
                    Check(((Label)Field("temperatures")).Text.Contains("205"), "Temperature telemetry absent");
                    string path = Path.GetFullPath("artifacts/host-ui-qa/connected.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await (Task)typeof(HostForm).GetMethod("SaveSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[path])!;
                    var view=Field("preview");
                    Check((int)view.GetType().GetProperty("SceneCount")!.GetValue(view)! > 0,"Native viewer did not import scene");
                    Check(File.Exists(Path.ChangeExtension(path,".native.bmp")),"Native viewer screenshot absent");
                    await Task.Delay(2200); // Exclude scene import and synchronous screenshot from the FPS window.
                    ((System.Windows.Forms.Timer)Field("timer")).Stop();
                    await Task.Delay(250); // Drain the last telemetry reply before the synthetic head sample.
                    var animation=(MotionPreview)Field("motion");
                    animation.Started("G1 X80 Y70 Z10 F600");
                    await Task.Delay(400);
                    var animatedHead=(System.Numerics.Vector3)view.GetType().GetProperty("Head")!.GetValue(view)!;
                    Check(animatedHead.X>20 && animatedHead.X<80 && animatedHead.Z>2 && animatedHead.Z<10,"Command-driven head did not move between telemetry replies");
                    view.GetType().GetMethod("Send")!.Invoke(view,["STATS"]);
                    string stats="";
                    for(int i=0;i<30;i++) { stats=(string)view.GetType().GetProperty("RendererStats")!.GetValue(view)!; if(stats.Length>0) break; await Task.Delay(50); }
                    var measured=Regex.Match(stats,@"fps=([\d.]+) heads=(\d+) interpolated=(\d+)");
                    Check(measured.Success && double.Parse(measured.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>0 && int.Parse(measured.Groups[2].Value)>5 && Regex.IsMatch(stats,@"predicted=[1-9]\d*"),"Renderer/prediction statistics missing: "+stats);
                    Check(double.Parse(measured.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)<=32,"Viewer exceeded 30 FPS budget");
                    Check(stats.Contains("cursor=1 relative=0"),"Cursor hidden or relative mouse mode enabled: "+stats);
                    Console.WriteLine(stats);
                    ((System.Windows.Forms.Timer)Field("timer")).Stop();
                    var loaded=(PrintJob)Field("job");
                    async Task<string> WaitVisual(Func<int,bool> expected)
                    {
                        for(int i=0;i<40;i++)
                        {
                            view.GetType().GetMethod("Send")!.Invoke(view,["STATS"]); await Task.Delay(50);
                            string reply=(string)view.GetType().GetProperty("RendererStats")!.GetValue(view)!;
                            var printed=Regex.Match(reply,@"printed=(\d+)");
                            if(printed.Success && expected(int.Parse(printed.Groups[1].Value))) return reply;
                        }
                        throw new Exception("Visual progress did not reach expected state");
                    }
                    Check(Regex.IsMatch(stats,@"printed=0\b"),"Loaded job already colored before printing");
                    view.GetType().GetProperty("Progress")!.SetValue(view,loaded.Commands.Length/2);
                    Console.WriteLine(await WaitVisual(n=>n>0 && n<loaded.Segments.Count));
                    string half=Path.GetFullPath("artifacts/host-ui-qa/half.png");
                    await (Task)typeof(HostForm).GetMethod("SaveSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[half])!;
                    view.GetType().GetProperty("Progress")!.SetValue(view,loaded.Commands.Length);
                    Console.WriteLine(await WaitVisual(n=>n==loaded.Segments.Count));
                    string full=Path.GetFullPath("artifacts/host-ui-qa/full.png");
                    await (Task)typeof(HostForm).GetMethod("SaveSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[full])!;
                    view.GetType().GetProperty("Progress")!.SetValue(view,0);
                    await WaitVisual(n=>n==0);
                    Console.WriteLine("PASS viewer: ghost before print, partial/full coloring, reset, visible cursor and 30 FPS cap.");
                    if (!args.Contains("--transport"))
                    {
                    int imports=(int)view.GetType().GetProperty("SceneCount")!.GetValue(view)!;
                    var child=(Process)view.GetType().GetField("process",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
                    child.Kill();
                    for(int i=0;i<200 && (int)view.GetType().GetProperty("SceneCount")!.GetValue(view)!<=imports;i++) await Task.Delay(50);
                    Check((int)view.GetType().GetProperty("SceneCount")!.GetValue(view)!>imports,"Viewer did not recover and reload scene after termination");
                    Console.WriteLine("PASS viewer recovery: terminated child restarted and scene restored.");
                    }
                    string streamFile=Path.GetFullPath("artifacts/host-ui-qa/stream.gcode");
                    File.WriteAllLines(streamFile,new[]{"G90","M83"}.Concat(Enumerable.Range(0,200).Select(i=>$"G1 X{i%20} Y30 E0.1 F600")));
                    await form.Import(streamFile);
                    int endstopReports=uiCommands.Count(c=>c.Contains("M119"));
                    int temperatureReports=uiCommands.Count(c=>c.Contains("M105"));
                    var printStarted=Call("StartPrint");
                    // Deliberately block repainting: acknowledgements and file transmission must continue.
                    Thread.Sleep(500);
                    Check(((PrintController)Field("controller")).Progress>0,"Printer transmission stalled with the UI thread");
                    Check(((PrintController)Field("controller")).Active,"Test print ended before active telemetry check");
                    typeof(HostForm).GetField("nextSlowPoll",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(form,0L);
                    await Call("Poll");
                    Check(uiCommands.Count(c=>c.Contains("M119"))==endstopReports && uiCommands.Count(c=>c.Contains("M105"))>temperatureReports,"Active telemetry queried BLTouch endstops or skipped temperatures");
                    await printStarted;
                    Check(((PrintController)Field("controller")).State==JobState.Completed,"Background print did not finish");
                    Console.WriteLine("PASS transport: file commands continue while the UI thread is blocked for 500 ms.");
                    Console.WriteLine("PASS active telemetry: temperature queried, M119 excluded during printing.");
                    await Call("Disconnect");
                    Check(!((Button)Field("printButton")).Enabled, "Print allowed after disconnect");
                    Console.WriteLine("PASS UI: connection, file import, print availability, temperature telemetry, disconnect and layout snapshot.");
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        catch (Exception ex) { done.TrySetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
    await done.Task.WaitAsync(TimeSpan.FromSeconds(40)); thread.Join(); uiListener.Stop(); await serving;
    return;
}

if (args.Contains("--import"))
{
    string folder = Path.GetFullPath("artifacts/host-import-qa"); Directory.CreateDirectory(folder);
    string temporary = Path.Combine(folder, "temporary.gcode"), final = Path.Combine(folder, "final.gcode");
    File.WriteAllText(temporary, "G28\nG1 X20\n"); File.WriteAllText(final, "old file");
    using var pipe = new NamedPipeServerStream("MarlinHost-0.1-import", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var connection = pipe.WaitForConnectionAsync();
    var info = new ProcessStartInfo(Path.GetFullPath("dist/host/MarlinHost.App.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    info.ArgumentList.Add("--from-slicer"); info.ArgumentList.Add(temporary);
    info.Environment["SLIC3R_PP_HOST"] = "File"; info.Environment["SLIC3R_PP_OUTPUT_NAME"] = final;
    using var helper = Process.Start(info)!; await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(helper.ExitCode == 0, "Postprocessor failed");
    await Task.Delay(1000); Check(!connection.IsCompleted, "Imported BEFORE successful final export");
    File.Copy(temporary, final, true);
    await connection.WaitAsync(TimeSpan.FromSeconds(10));
    using var reader = new StreamReader(pipe);
    string? snapshot = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(snapshot is not null && File.ReadAllText(snapshot) == File.ReadAllText(final), "Imported content differs");
    File.WriteAllText(final, "changed later");
    Check(File.ReadAllText(snapshot!) == "G28\nG1 X20\n", "Job snapshot not isolated");
    Console.WriteLine("PASS: PrusaSlicer postprocessor waits for final export, delivers through IPC, keeps immutable snapshot.");
    return;
}

if (args.Contains("--native"))
{
    string root = Directory.GetCurrentDirectory();
    string dir = Path.Combine(root, "artifacts", "host-native-qa"); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "simulator.settings"), "speed=30\n");
    var start = new ProcessStartInfo(Path.Combine(root, "dist", "firmware", "MarlinSimulator.exe")) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    start.ArgumentList.Add("--port"); start.ArgumentList.Add("18199"); start.ArgumentList.Add("--quit-after"); start.ArgumentList.Add("300");
    start.Environment["PATH"] = Path.Combine(root, ".tools", "mingw64", "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
    using var firmware = Process.Start(start)!;
    try
    {
        await Task.Delay(1800);
        await using var relay = new MarlinEmulator.Core.TcpConnection(0, 18199); relay.Start();
        await using var link = new PrinterLink();
        link.Received += l => { if (l.StartsWith("Error:") || l.Contains("Min:")) Console.WriteLine(l); };
        await link.ConnectTcp("127.0.0.1", relay.Port);
        var control = new PrintController(link); await control.Initialize();
        Check(control.MaxZ == 250 && control.MinX == 0 && control.MinY == 0, "M211 bounds not read");
        await control.Manual("G28\nG90\nM83\nG1 X30 Y40 Z2 F3000");
        var job = new PrintJob("native", ["G4 P500", "G1 X35 F1200", "G1 Y45", "M104 S0"], [], false);
        var running = control.Run(job); control.Pause();
        await Until(() => control.State == JobState.Paused, 30);
        await control.Manual("M220 S125\nM221 S108\nM105", true);
        var parked = await control.Manual("M114", true);
        Check(parked.Any(l => l.StartsWith("X:0.00 Y:0.00 Z:17.00")), "Native parking coordinates wrong: " + string.Join("|", parked));
        control.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(30));
        Check(control.State == JobState.Completed && control.Speed == 125 && control.Flow == 108, "Native resume failed");
        var position = await control.Manual("M114");
        Check(position.Any(l => l.StartsWith("X:35.00 Y:45.00 Z:2.00")), "Native restored position wrong: " + string.Join("|", position));
        var settings = await control.Manual("M503 S0"); Check(settings.Any(l => l.Contains("M92")), "EEPROM report missing");
        await control.Manual("M104 S50\nM109 S50\nM104 S0\nM140 S0");
        Console.WriteLine("PASS native: numbered protocol, M211 bounds, homing, park Z+15/X0Y0, resume M83, live speed/flow, M503, heating.");
        int fileArg = Array.IndexOf(args, "--file");
        if (fileArg >= 0)
        {
            var imported = PrintJob.Load(args[fileArg + 1]);
            await control.Manual("M220 S100\nM221 S100");
            int lastProgress = 0;
            control.Changed += () => { if (control.Progress >= lastProgress + 500) { lastProgress = control.Progress; Console.WriteLine($"File: {control.Progress}/{imported.Commands.Length}"); } };
            await control.Run(imported).WaitAsync(TimeSpan.FromSeconds(180));
            Check(control.State == JobState.Completed, "Full file not completed");
            Console.WriteLine("PASS full file through emulator TCP relay: " + Path.GetFileName(imported.FileName));
        }
    }
    finally { if (!firmware.HasExited) { firmware.CloseMainWindow(); if (!firmware.WaitForExit(3000)) firmware.Kill(); } }
    return;
}

var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
var commands = new List<string>();
var resendDone = false;
int fakeZ = 2;
var stop = new CancellationTokenSource();
var server = Task.Run(async () =>
{
    using var client = await listener.AcceptTcpClientAsync(stop.Token);
    using var reader = new StreamReader(client.GetStream());
    using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\n" };
    while (await reader.ReadLineAsync(stop.Token) is { } wire)
    {
        string command = wire;
        var frame = Regex.Match(wire, @"^N(\d+) (.*)\*(\d+)$");
        if (frame.Success)
        {
            string payload = wire[..wire.LastIndexOf('*')];
            Check(payload.Aggregate(0, (a, c) => a ^ c) == int.Parse(frame.Groups[3].Value), "Checksum wrong");
            command = frame.Groups[2].Value;
            if (command == "M220 S123" && !resendDone)
            {
                resendDone = true;
                await writer.WriteLineAsync("Error:checksum mismatch, Last Line: " + (int.Parse(frame.Groups[1].Value) - 1));
                await writer.WriteLineAsync("Resend: " + frame.Groups[1].Value);
                await writer.WriteLineAsync("ok"); continue;
            }
        }
        lock (commands) commands.Add(command);
        if (command == "M211") await writer.WriteLineAsync("  Min:  X:0.00 Y:0.00 Z:0.00   Max:  X:220.00 Y:220.00 Z:250.00");
        if (command == "M114") await writer.WriteLineAsync($"X:20.00 Y:30.00 Z:{fakeZ}.00 E:5.00 Count X:0 Y:0 Z:0");
        if (command.StartsWith("G4")) { await writer.WriteLineAsync("echo:busy: processing"); await Task.Delay(100); }
        await writer.WriteLineAsync("ok");
    }
});
await using (var link = new PrinterLink())
{
    await link.ConnectTcp("127.0.0.1", port);
    var controller = new PrintController(link); await controller.Initialize();
    await controller.Manual("M220 S123", true); Check(resendDone && controller.Speed == 123, "Resend not recovered");
    await controller.Manual("G28\nG90\nM83");
    var job = new PrintJob("mock", ["G4 P100", "G1 X25 E1 F1200", "M104 S0"], [], false);
    var running = controller.Run(job); controller.Pause();
    await Until(() => controller.State == JobState.Paused);
    Check(commands.Contains("G1 Z17 F600") && commands.Contains("G1 X0 Y0 F3000"), "Park sequence missing");
    await controller.Manual("M221 S110", true);
    try { await controller.Manual("G28"); throw new Exception("Unsafe manual command allowed"); } catch (InvalidOperationException) { }
    try { await controller.Manual("G1 X9", true); throw new Exception("Unsafe live command allowed"); } catch (InvalidOperationException) { }
    controller.Resume(); await running;
    Check(controller.State == JobState.Completed && controller.Flow == 110, "Resume state wrong");
    Check(commands.Contains("G1 X20 Y30 F3000") && commands.Contains("G1 Z2 F600") && commands.Contains("G92 E5"), "Restore sequence wrong");
    Check(commands.LastIndexOf("M83") < commands.LastIndexOf("G1 X25 E1 F1200"), "E mode not restored");
    await controller.Manual("G28");
    running = controller.Run(job); controller.Pause(); await Until(() => controller.State == JobState.Paused);
    controller.Cancel(); await running;
    Check(controller.State == JobState.Cancelled && commands.TakeLast(3).SequenceEqual(new[] { "M104 S0", "M140 S0", "M107" }), "Cancel did not switch heaters off");
    fakeZ = 248;
    running = controller.Run(job); controller.Pause(); await Until(() => controller.State == JobState.Paused);
    Check(commands.Contains("G1 Z250 F600") && !commands.Contains("G1 Z263 F600"), "Pause exceeded max Z");
    controller.Cancel(); await running; fakeZ = 2;
    var fresh = new PrintController(link); await fresh.Initialize();
    running = fresh.Run(job); fresh.Pause(); await Until(() => fresh.State == JobState.Paused);
    Check(!fresh.Parked, "Unknown position parked without homing");
    fresh.Resume(); await running;
    Check(fresh.State == JobState.Completed, "Unhomed pause cannot resume");
    running = fresh.Run(job); fresh.Pause(); await Until(() => fresh.State == JobState.Paused);
    await fresh.Emergency();
    try { await running; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    Check(!link.Connected && fresh.State == JobState.Faulted, "Emergency did not stop session");
    Console.WriteLine("PASS: checksums/resend, busy handling, pause/restore modes, live controls, blocked manual motion, cancellation while paused.");
    Console.WriteLine("PASS: max-Z clamp, pause before homing, emergency while paused, repeated connection disposal.");
}
stop.Cancel(); listener.Stop(); try { await server; } catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or IOException) { }

var modal = new ModalState(); modal.Apply("G90"); modal.Apply("M83"); modal.Apply("G1 X10 E5 F600"); modal.Apply("G1 X20 E2");
Check(modal.Position.X == 20 && modal.E == 7 && modal.ERelative && modal.Feed == 600, "Modal parsing wrong");
Console.WriteLine("PASS: absolute XYZ with relative E tracking.");

string arcFile = Path.GetFullPath("artifacts/host-arc-test.gcode"); Directory.CreateDirectory(Path.GetDirectoryName(arcFile)!);
File.WriteAllText(arcFile, "G90\nM83\nG1 X10 Y0\nG3 X0 Y10 I-10 J0 E1\n");
var arcJob = PrintJob.Load(arcFile);
Check(!arcJob.HasArcs && arcJob.Segments.Count > 20 && arcJob.Segments.Last().To == new System.Numerics.Vector3(0, 10, 0), "Arc preview wrong");
Check(arcJob.Segments.Skip(1).All(s => Math.Abs(s.To.Length() - 10) < .01), "Arc tessellation radius wrong");
Console.WriteLine("PASS: arc preview follows circle and preserves endpoint.");

string colorFile=Path.GetFullPath("artifacts/host-color-test.gcode");
File.WriteAllText(colorFile,"G90\nM83\n;TYPE:Internal infill\nG1 X10 E1\n;TYPE:External perimeter\nG1 X20 E1\n; filament_colour = #336699\n");
var colored=PrintJob.Load(colorFile);
Check(colored.FilamentColor.R==0x33 && colored.FilamentColor.G==0x66 && colored.FilamentColor.B==0x99,"Filament color not read from slicer metadata");
Check(!colored.Segments[0].Outline && colored.Segments[1].Outline,"Internal infill obscures planned exterior outline");
Check(arcJob.Segments.All(s=>s.Outline),"G-code without path type metadata lost its preview");
Console.WriteLine("PASS: filament RGB, transparent exterior outline metadata, fallback for plain G-code.");

string layerFile = Path.GetFullPath("artifacts/host-layer-test.gcode");
foreach (string marker in new[] { ";LAYER_CHANGE", ";LAYER:0" })
{
    File.WriteAllText(layerFile, $"G90\nM83\nG1 X1 E1\n{marker}\nG1 Z0.2\nG1 X10 E1\nG1 Z0.8\nG1 Z0.2\nG1 X20 E1\n{(marker == ";LAYER_CHANGE" ? marker : ";LAYER:1")}\nG1 Z0.4\nG1 X10 E1\nM104 S0\n");
    var layered = PrintJob.Load(layerFile);
    Check(!layered.EstimatedLayers && layered.LayerEndCommands.Length == 2,"Layer markers not recognized or purge/Z-hop counted as layers");
    int end = layered.LayerEndCommands[0];
    Check(layered.PrintedLayers(0)==0 && layered.PrintedLayers(end)==0 && layered.PrintedLayers(end+1)==1 && layered.PrintedLayers(layered.Commands.Length)==2,"Layer completion boundary wrong");
}
File.WriteAllText(layerFile,"G90\nM83\nG1 Z0.2\nG1 X10 E1\nG1 Z0.8\nG1 X20\nG1 Z0.2\nG1 X30 E1\nG1 Z0.4\nG1 X40 E1\n");
var estimated = PrintJob.Load(layerFile);
Check(estimated.EstimatedLayers && estimated.LayerEndCommands.Length==2,"Unmarked layer estimate counted travel Z-hop");
Console.WriteLine("PASS layers: Prusa/Cura markers, purge exclusion, Z-hop, confirmation boundaries and unmarked estimate.");

double animationTime=0;
var predicted = new MotionPreview(new MotionProfile(),()=>animationTime);
predicted.Correct(new(0,0,0)); predicted.Started("G90"); predicted.Started("M83");
predicted.Started("G1X10Y10F600");
animationTime=.5;
var halfway=predicted.Sample();
Check(halfway.X>3 && halfway.X<4 && Math.Abs(halfway.X-halfway.Y)<.001,"G1 did not animate without M114");
predicted.Correct(new(10,10,0));
Check(predicted.Sample().X<4,"Planner endpoint M114 teleported animated head");
animationTime=2; Check(System.Numerics.Vector3.Distance(predicted.Sample(),new(10,10,0))<.001,"G1 endpoint wrong");
predicted.Started("G91"); predicted.Started("M220 S50"); predicted.Started("G1 X10 F600");
animationTime=3; Check(predicted.Sample().X>14 && predicted.Sample().X<16,"Relative G1 or M220 duration wrong");
animationTime=5; Check(Math.Abs(predicted.Sample().X-20)<.001,"Relative endpoint wrong");
predicted.Started("G1 X50 F600"); animationTime=5.5; predicted.Stop();
var stopped=predicted.Sample(); animationTime=9; Check(predicted.Sample()==stopped,"Stopped prediction kept moving");
predicted.Started("G90"); predicted.Started("M220 S100"); predicted.Started("G1 X30 F600");
predicted.Completed("M400",["ok"]); animationTime+=.2;
Check(Math.Abs(predicted.Sample().X-30)<.001,"M400 did not synchronize prediction");

animationTime=0;
var homingPreview=new MotionPreview(MotionProfile.Ramps(),()=>animationTime);
homingPreview.Correct(new(50,50,0)); homingPreview.Started("G28");
Check(homingPreview.Homing && homingPreview.Pending>=10 && homingPreview.Planned==new System.Numerics.Vector3(105,81,5),"Homing phases or safe probe point wrong");
animationTime=1; Check(homingPreview.Sample().Z>0 && homingPreview.Sample().Z<11,"Homing Z clearance not animated");
homingPreview.Correct(new(0,0,0)); Check(homingPreview.Planned==new System.Numerics.Vector3(105,81,5),"Telemetry interrupted G28 animation");
homingPreview.Completed("G28",["ok"]); animationTime+=.2;
Check(System.Numerics.Vector3.Distance(homingPreview.Sample(),new(105,81,5))<.001,"Homing acknowledgement did not settle final position");
homingPreview.Completed("M114",["X:105.00 Y:81.00 Z:5.20 E:0", "ok"]);
Check(Math.Abs(homingPreview.Sample().Z-5.2f)<.001,"Post-homing correction missing");
homingPreview.ReadSettings("echo: M203 X80 Y90 Z3 E8"); homingPreview.ReadSettings("echo: M201 X100 Y200 Z30");
homingPreview.ReadSettings("echo: M204 P400 T500"); homingPreview.ReadSettings("echo: M851 X0 Y24 Z-1.5");
Check(homingPreview.Profile.MaxFeed.X==80 && homingPreview.Profile.MaxAcceleration.Z==30 && homingPreview.Profile.PrintAcceleration==400 && homingPreview.Profile.ProbeOffset.Z==-1.5f,"Live M503 limits not read");

animationTime=0;
var arcMotion=new MotionPreview(new MotionProfile(),()=>animationTime);
arcMotion.Correct(new(10,0,0)); arcMotion.Started("G3 X0 Y10 I-10 J0 F600"); animationTime=.7;
var onArc=arcMotion.Sample(); Check(onArc.X>0 && onArc.Y>0 && Math.Abs(onArc.Length()-10)<.02,"Animated arc cuts through circle");
Console.WriteLine("PASS motion: compact G1 without telemetry, relative coordinates, M220, planner correction, stop, M400, homing phases, live limits and arcs.");
