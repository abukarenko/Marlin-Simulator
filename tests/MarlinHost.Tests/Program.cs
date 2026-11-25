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

if(args.Contains("--motion"))
{
    double now=0;
    var motion=new MotionPreview(()=>now);
    motion.Settings(["echo: M92 X80 Y80 Z400 E95","M201 X10 Y20 Z5 E100","M203 X100 Y100 Z5 E25","M204 P20 R30 T100"]);
    Check(motion.Limits.HasSteps && motion.Limits.HasAcceleration && motion.Limits.Steps[2]==400,"M503 limits missing");
    motion.Observe(["X:0 Y:0 Z:0 E:0"],true);
    motion.Accept("G90"); motion.Accept("G0 X100 F10");
    now=60;
    Check(Math.Abs(motion.Position.X-10)<.02,"F10 must travel about 10 mm per minute");
    motion.Observe(["X:100 Y:0 Z:0 E:0 Count X:8000 Y:0 Z:0"],true);
    Check(!motion.RealtimeObserved && Math.Abs(motion.Position.X-10)<.02,"Projected M114 endpoint short-circuited slow movement");
    motion.Accept("G92 E0");
    Check(Math.Abs(motion.Position.X-10)<.02,"G92 E reset XYZ animation");
    now=300; Check(Math.Abs(motion.Position.X-50)<.02,"Slow movement midpoint wrong");
    now=601; Check(Math.Abs(motion.Position.X-100)<.001,"Slow movement did not finish");
    var limits=motion.Limits;
    var profile=new MotionProfile(System.Numerics.Vector3.Zero,new(100,0,0),0,600,100,limits,0);
    Check(Math.Abs(profile.Duration-11)<1e-5 && Math.Abs(profile.At(.5).X-1.25)<1e-5,"Axis acceleration/trapezoid wrong");
    limits.Apply("M203 X2");
    profile=new MotionProfile(System.Numerics.Vector3.Zero,new(100,0,0),0,600,100,limits,0);
    Check(Math.Abs(profile.Duration-50.2)<1e-5,"M203 axis speed cap wrong");
    now=0; var scaled=new MotionPreview(()=>now); scaled.Observe(["X:20 Y:0 Z:0 E:0"],true);
    scaled.Accept("G91"); scaled.Accept("M220 S50"); scaled.Accept("G1 X10 F60");
    now=10; Check(Math.Abs(scaled.Position.X-25)<.01,"Relative motion or M220 wrong");
    var stopped=scaled.Position; scaled.Stop(); now=30; Check(scaled.Position==stopped,"Stopped head kept moving");
    now=0; var actual=new MotionPreview(()=>now); actual.Observe(["X:0 Y:0 Z:0 E:0"],true); actual.Accept("G0 X100 F10");
    now=1; actual.Observe(["X:0.16 Y:0 Z:0 E:0"],true);
    Check(actual.RealtimeObserved && Math.Abs(actual.Position.X-.16)<.001,"Intermediate realtime position not used");
    now=2; actual.Observe(["X:0.33 Y:0 Z:0 E:0"],true);
    Check(Math.Abs(actual.Position.X-.33)<.001,"Realtime sample did not correct estimate");
    actual.Observe(["X:100 Y:0 Z:0 E:0"],true); Check(actual.Position.X==100,"Realtime endpoint ignored");
    Console.WriteLine("PASS motion: F10 ten-minute move with virtual clock, projected/realtime M114, M92/M201/M203/M204, ramps, M220, relative moves, G92 E, stop.");
    return;
}

if (args.Contains("--ui"))
{
    var uiListener = new TcpListener(IPAddress.Loopback, 0); uiListener.Start();
    int uiPort = ((IPEndPoint)uiListener.LocalEndpoint).Port;
    var uiCommands = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var serving = Task.Run(async () =>
    {
        using var client = await uiListener.AcceptTcpClientAsync();
        using var reader = new StreamReader(client.GetStream());
        using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } command)
        {
            uiCommands.Enqueue(command);
            if (command.Contains("M115")) await writer.WriteLineAsync("FIRMWARE_NAME:Marlin terminal-test");
            if (command.Contains("M503")) await writer.WriteLineAsync("echo:Steps per unit:\nM92 X80 Y80 Z400 E93");
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
                    Check(((Label)Field("temperatures")).Text.Contains("205"), "Temperature telemetry absent");
                    var stepSelector=(ComboBox)Field("step"); var feedSelector=(ComboBox)Field("jogFeed");
                    Check(stepSelector.Text=="5" && feedSelector.Text=="5000","Jog defaults differ");
                    Check(stepSelector.Items.Cast<string>().SequenceEqual(new[]{"0,05","0,1","0,5","1","5","10","50","100"}),"Step presets differ");
                    Check(feedSelector.Items.Cast<string>().SequenceEqual(new[]{"50","100","500","1000","5000","10000","50000"}),"Feed presets differ");
                    IEnumerable<Control> Descendants(Control parent)=>parent.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Descendants(c)));
                    var jogX=Descendants(form).OfType<Button>().Single(b=>b.Text=="X+");
                    jogX.PerformClick(); await Until(()=>uiCommands.Any(c=>c.Contains("G1 X5 F5000")));
                    stepSelector.Text="0,05"; feedSelector.Text="50000";
                    jogX.PerformClick(); await Until(()=>uiCommands.Any(c=>c.Contains("G1 X0.05 F50000")));
                    stepSelector.Text="0.25"; feedSelector.Text="1234.5";
                    jogX.PerformClick(); await Until(()=>uiCommands.Any(c=>c.Contains("G1 X0.25 F1234.5")));
                    await Task.Delay(150);
                    int moves=uiCommands.Count(c=>c.Contains("G1 X"));
                    stepSelector.Text="invalid"; jogX.PerformClick(); await Task.Delay(150);
                    Check(uiCommands.Count(c=>c.Contains("G1 X"))==moves,"Invalid selector text sent movement");
                    stepSelector.Text="5"; feedSelector.Text="5000";
                    Console.WriteLine("PASS jog selectors: presets/defaults, comma/point/custom values, 50000 feed and rejected invalid input.");
                    var terminalToggle=(CheckBox)Field("terminal");
                    var terminalLog=(RichTextBox)Field("journal");
                    var commandInput=(ComboBox)Field("input");
                    terminalToggle.Checked=true; terminalLog.Clear();
                    await Task.Delay(2200);
                    Check(!terminalLog.Text.Contains("←") && !terminalLog.Text.Contains("→"),"Background polling leaked into terminal");
                    commandInput.Text="M115"; await Call("SendInput");
                    await Until(()=>terminalLog.Text.Contains("← ok"));
                    Check(terminalLog.Text.Contains("→ M115") && terminalLog.Text.Contains("FIRMWARE_NAME:Marlin terminal-test"),"Manual terminal response missing");
                    terminalLog.Clear(); commandInput.Text="M105"; await Call("SendInput");
                    await Until(()=>terminalLog.Text.Contains("← T:205.0"));
                    Check(terminalLog.Text.Contains("→ M105"),"Explicit status query was filtered");
                    terminalLog.Clear(); commandInput.Text="M503"; await Call("SendInput");
                    await Until(()=>terminalLog.Text.Contains("M92 X80 Y80 Z400 E93"));
                    Check(terminalLog.Text.Contains("echo:Steps per unit:"),"Multiline response truncated");
                    terminalToggle.Checked=false; terminalLog.Clear();
                    commandInput.Text="M115"; await Call("SendInput"); await Task.Delay(100);
                    Check(!terminalLog.Text.Contains("FIRMWARE_NAME:"),"Terminal switch did not hide responses");
                    Console.WriteLine("PASS terminal: polling including ACK hidden, explicit M105 visible, M115 and multiline M503 replies, toggle off.");
                    var motorToggle=(CheckBox)Field("motors");
                    Check(motorToggle.Checked && motorToggle.Enabled,"Motor ON not selected by default");
                    motorToggle.Checked=false;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M84")) && motorToggle.Enabled);
                    Check(!motorToggle.Checked && motorToggle.Text=="Motor OFF","Motor off state not retained after acknowledgement");
                    motorToggle.Checked=true;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M17")) && motorToggle.Enabled);
                    Check(motorToggle.Checked && motorToggle.Text=="Motor ON","Motor on state not retained after acknowledgement");
                    commandInput.Text="M84"; await Call("SendInput");
                    await Until(()=>!motorToggle.Checked);
                    commandInput.Text="M17"; await Call("SendInput"); await Until(()=>motorToggle.Checked);
                    commandInput.Text="M84 S30"; await Call("SendInput"); await Task.Delay(150);
                    Check(motorToggle.Checked,"Motor timeout command incorrectly treated as immediate off");
                    Console.WriteLine("PASS motors: default ON, switch sends M84/M17, terminal commands synchronize state, timeout preserves enabled state.");
                    string path = Path.GetFullPath("artifacts/host-ui-qa/connected.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await (Task)typeof(HostForm).GetMethod("SaveSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[path])!;
                    var view=Field("preview");
                    Check((int)view.GetType().GetProperty("SceneCount")!.GetValue(view)! > 0,"Native viewer did not import scene");
                    Check(File.Exists(Path.ChangeExtension(path,".native.bmp")),"Native viewer screenshot absent");
                    await Task.Delay(2200); // Exclude scene import and synchronous screenshot from the FPS window.
                    ((System.Windows.Forms.Timer)Field("timer")).Stop();
                    await Task.Delay(250); // Drain the last telemetry reply before the synthetic head sample.
                    void Endstop(string sample)=>typeof(HostForm).GetMethod("Receive",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[sample]);
                    async Task WaitEndstops(int mask)
                    {
                        for(int i=0;i<40;i++)
                        {
                            view.GetType().GetMethod("Send")!.Invoke(view,["STATS"]); await Task.Delay(50);
                            string reply=(string)view.GetType().GetProperty("RendererStats")!.GetValue(view)!;
                            if(Regex.IsMatch(reply,@"endstops="+mask+@"\b")) return;
                        }
                        throw new Exception("Endstop overlay did not update: "+mask+" actual="+view.GetType().GetProperty("RendererStats")!.GetValue(view));
                    }
                    Endstop("z_min: open"); Endstop("x_max: TRIGGERED"); Endstop("z_max: TRIGGERED");
                    await WaitEndstops(34);
                    string endstopShot=Path.GetFullPath("artifacts/host-ui-qa/endstops.png");
                    await (Task)typeof(HostForm).GetMethod("SaveSnapshot",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[endstopShot])!;
                    Endstop("x_max: open"); await WaitEndstops(32);
                    Endstop("z_max: open"); await WaitEndstops(0);
                    Console.WriteLine("PASS endstops: X+/Z+ native overlay, individual release and all-open hiding.");
                    view.GetType().GetProperty("Head")!.SetValue(view,new System.Numerics.Vector3(80,70,10));
                    await Task.Delay(200);
                    string beforeStats=(string)view.GetType().GetProperty("RendererStats")!.GetValue(view)!;
                    view.GetType().GetMethod("Send")!.Invoke(view,["STATS"]);
                    string stats="";
                    for(int i=0;i<30;i++) { stats=(string)view.GetType().GetProperty("RendererStats")!.GetValue(view)!; if(stats.Length>0 && stats!=beforeStats) break; await Task.Delay(50); }
                    var measured=Regex.Match(stats,@"fps=([\d.]+) heads=(\d+) interpolated=(\d+)");
                    Check(measured.Success && double.Parse(measured.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>0 && int.Parse(measured.Groups[2].Value)>5 && int.Parse(measured.Groups[3].Value)>0,"Renderer/polling/interpolation statistics missing: "+stats);
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
                    var heater=(TemperatureControl)Field("nozzleTemperature");
                    var heaterTarget=(NumericUpDown)Field("nozzle");
                    var heaterOn=(CheckBox)Field("nozzleOn");
                    Check(heater.ActualTemperature==205,"Temperature marker differs from sensor");
                    heaterOn.Checked=false;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M104 S0")));
                    heaterTarget.Value=225;
                    heaterOn.Checked=true;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M104 S225")));
                    typeof(TemperatureControl).GetMethod("OnKeyDown",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(heater,[new KeyEventArgs(Keys.Right)]);
                    await Until(()=>uiCommands.Any(c=>c.Contains("M104 S226")));
                    Check(heaterTarget.Value==226 && heater.ActualTemperature==205,"Setpoint editing changed sensor reading");
                    heaterOn.Checked=false;
                    await Task.Delay(100);
                    Check(heaterTarget.Value==226,"Turning heat off erased stored target");
                    heater.SetReportedTemperature(100,0);
                    Check(heaterTarget.Value==226 && heater.ActualTemperature==100,"Off telemetry erased remembered target");
                    ((ComboBox)Field("input")).Focus();
                    heater.SetReportedTemperature(101,240);
                    Check(heaterTarget.Value==240,"Print target not reflected by setpoint");
                    var bedOn=(CheckBox)Field("bedOn"); bedOn.Checked=false;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M140 S0")));
                    ((NumericUpDown)Field("bed")).Value=80; bedOn.Checked=true;
                    await Until(()=>uiCommands.Any(c=>c.Contains("M140 S80")));
                    Console.WriteLine("PASS heaters: sensor marker, setpoint keyboard, T0/bed commands and retained target after heat off.");
                    Endstop("z_max: TRIGGERED"); await WaitEndstops(32);
                    await Call("Disconnect");
                    await WaitEndstops(0);
                    Check(heater.ActualTemperature is null,"Stale temperature after disconnect");
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

if(args.Contains("--native-motion") || args.Contains("--native-motion-fast"))
{
    string folder=Path.GetFullPath("artifacts/host-motion-native-qa"); Directory.CreateDirectory(folder);
    bool accelerated=args.Contains("--native-motion-fast");
    File.WriteAllText(Path.Combine(folder,"simulator.settings"),accelerated ? "speed=30\n" : "speed=1\n");
    var start=new ProcessStartInfo(Path.GetFullPath("dist/host/viewer/MarlinViewer.exe")) { WorkingDirectory=folder,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
    start.ArgumentList.Add("--port"); start.ArgumentList.Add("18201"); start.ArgumentList.Add("--quit-after"); start.ArgumentList.Add("60");
    using var firmware=Process.Start(start)!;
    try
    {
        await Task.Delay(1800);
        await using var relay=new MarlinEmulator.Core.TcpConnection(0,18201); relay.Start();
        await using var link=new PrinterLink(); await link.ConnectTcp("127.0.0.1",relay.Port);
        var controller=new PrintController(link); await controller.Initialize();
        Check(controller.Motion.Limits.HasSteps && controller.Motion.Limits.HasAcceleration,"Actual M503 not parsed");
        if(accelerated)
        {
            await controller.Manual("G21\nG90\nG92 X0 Y0 Z0\nG0 X100 F10");
            double x=0;
            for(int i=0;i<10;i++)
            {
                await Task.Delay(100);
                var report=await controller.Manual("M114 R",true,background:true);
                var match=Regex.Match(string.Join(" ",report),@"X:\s*([-\d.]+)");
                x=double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
                Check(Math.Abs(controller.Position.X-x)<.025,"Host differs from accelerated simulator");
            }
            Check(controller.Motion.RealtimeObserved && x>1 && x<100,"Accelerated telemetry not detected");
            await controller.Manual("M410\nM114 R");
            Console.WriteLine("PASS native motion at speed=30: F10 follows actual firmware clock, not host wall-clock estimate.");
            return;
        }
        await controller.Manual("G21\nG90\nG92 X0 Y0 Z0\nG0 X2 F60");
        var watch=System.Diagnostics.Stopwatch.StartNew();
        bool intermediate=false;
        double previous=-1;
        while(watch.Elapsed.TotalSeconds<1.8)
        {
            var report=await controller.Manual("M114 R",true,background:true);
            var match=Regex.Match(string.Join(" ",report),@"X:\s*([-\d.]+)");
            Check(match.Success,"No native realtime coordinates");
            double x=double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            Check(x>=previous-.02,"Native position moved backwards"); previous=x;
            if(x>.03 && x<1.9) intermediate=true;
            Check(Math.Abs(controller.Motion.Position.X-x)<.025,"Host head differs from firmware realtime position");
            await Task.Delay(100);
        }
        Check(intermediate && controller.Motion.RealtimeObserved,"M114 R returned only projected endpoints");
        Check(previous<2 && previous>.3,"Native 1 mm/s speed not observed");
        await controller.Manual("M400\nM114 R");
        Check(Math.Abs(controller.Motion.Position.X-2)<.025,"Native endpoint wrong");
        await controller.Manual("G0 X3 F10");
        await Task.Delay(1000);
        var slow=await controller.Manual("M114 R",true,background:true);
        var slowMatch=Regex.Match(string.Join(" ",slow),@"X:\s*([-\d.]+)");
        double slowX=double.Parse(slowMatch.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
        Check(slowX>2.02 && slowX<2.4 && Math.Abs(controller.Motion.Position.X-slowX)<.025,"Native F10 preview moved too fast");
        await controller.Manual("M410\nM114 R");
        float stoppedX=controller.Motion.Position.X;
        await Task.Delay(300); await controller.Manual("M114 R",true,background:true);
        Check(Math.Abs(controller.Motion.Position.X-stoppedX)<.025,"Preview continued after native quick stop");
        Console.WriteLine("PASS native motion: actual M503, M114 R, F60/F10 speeds, host/stepper agreement, endpoint and M410 stop.");
    }
    finally { if(!firmware.HasExited) firmware.Kill(true); await firmware.WaitForExitAsync(); }
    return;
}

if (args.Contains("--native") || args.Contains("--lcd"))
{
    bool lcd = args.Contains("--lcd");
    string root = Directory.GetCurrentDirectory();
    string dir = Path.Combine(root, "artifacts", lcd ? "host-lcd-qa" : "host-native-qa"); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "simulator.settings"), "speed=30\n");
    var start = new ProcessStartInfo(lcd ? Path.Combine(root, "dist", "lcd", "EasyMarlinEmulator.exe") : Path.Combine(root, "dist", "firmware", "MarlinSimulator.exe")) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    start.ArgumentList.Add("--port"); start.ArgumentList.Add("18199"); start.ArgumentList.Add("--quit-after"); start.ArgumentList.Add("300");
    start.Environment["PATH"] = Path.Combine(root, ".tools", "mingw64", "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
    using var firmware = Process.Start(start)!;
    try
    {
        await Task.Delay(1800);
        await using var relay = new MarlinEmulator.Core.TcpConnection(0, 18199); relay.Start();
        await using var link = new PrinterLink();
        link.Received += l => { if (l.StartsWith("Error:") || l.Contains("Min:")) Console.WriteLine(l); };
        await link.ConnectTcp("127.0.0.1", lcd ? 18199 : relay.Port);
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
    try { await running; throw new Exception("Emergency job completed normally"); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
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
