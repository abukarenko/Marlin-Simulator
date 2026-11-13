using System.Diagnostics;
using System.IO.Ports;
using System.Numerics;
using System.Text.RegularExpressions;

namespace MarlinHost;

public sealed class HostForm : Form
{
    private readonly HostSettings settings = HostSettings.Load();
    private readonly ComboBox transport = new() { Width = 75, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox address = new() { Width = 125 };
    private readonly NumericUpDown port = Num(7000, 1, 65535);
    private readonly ComboBox com = new() { Width = 100 };
    private readonly NumericUpDown baud = Num(115200, 1200, 1000000);
    private readonly Label stateLabel = new() { AutoSize = true, Padding = new Padding(4, 8, 0, 0) };
    private readonly Label fileLabel = new() { AutoSize = true, Padding = new Padding(4, 8, 0, 0), MaximumSize = new Size(500, 50) };
    private readonly Label temperatures = new() { AutoSize = true, Text = "Сопло — / — °C\nСтол — / — °C", Padding = new Padding(5), Font = new Font("Segoe UI", 12) };
    private readonly RichTextBox journal = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(246, 248, 250), Font = new Font("Consolas", 9) };
    private readonly CheckBox details = new() { Text = "Подробный обмен", AutoSize = true };
    private readonly CheckBox standby = new() { Text = "Поддерживать температуру при простое", AutoSize = true };
    private readonly NumericUpDown nozzle = Num(230, 0, 300);
    private readonly NumericUpDown bed = Num(85, 0, 130);
    private readonly NumericUpDown fan = Num(0, 0, 100);
    private readonly NumericUpDown fanIndex = Num(0, 0, 7);
    private readonly NumericUpDown speed = Num(100, 10, 300);
    private readonly NumericUpDown flow = Num(100, 10, 200);
    private readonly NumericUpDown step = Num(10, .1m, 100, 1);
    private readonly NumericUpDown jogFeed = Num(1200, 30, 10000);
    private readonly NumericUpDown extrudeLength = Num(5, .1m, 100, 1);
    private readonly NumericUpDown extrudeFeed = Num(180, 1, 1200);
    private readonly ComboBox input = new() { Width = 350 };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 20 };
    private readonly Label progressLabel = new() { AutoSize = true };
    private readonly List<Button> idleButtons = [];
    private readonly List<Button> connectedButtons = [];
    private readonly ToolTip tips = new();
    private readonly Button connectButton;
    private readonly Button printButton;
    private readonly Button pauseButton;
    private readonly Button cancelButton;
    private readonly Button stopButton;
    private readonly NativePreview preview;
    private readonly Label nozzleReadout = new(), bedReadout = new(), extrusionReadout = new();
    private readonly Dictionary<string, Label> endstopLabels = [];
    private readonly CheckBox nozzleOn = new(), bedOn = new(), fanOn = new();
    private bool syncingHeaters;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private long nextSlowPoll;
    private int uiDirty;
    private volatile bool verbose;
    private PrinterLink? link;
    private PrintController? controller;
    private PrintJob? job;
    private Task? running;
    private bool connecting, polling, closing, closeReady, hasRun;
    private bool idleSuppressed;
    private long importGeneration;
    private double lastNozzle, lastBed;
    private readonly Queue<string> deferredImports = new();
    private string? pendingNotice;
        private readonly List<(string Op, string Raw, int Row, char Key)> eepromRows = [];

    public HostForm()
    {
        Text = "Marlin Host · 0.2"; Size = new Size(1280, 900); MinimumSize = new Size(1060, 740);
        Font = new Font("Segoe UI", 9); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(76,76,76); ForeColor = Color.WhiteSmoke;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (string.IsNullOrWhiteSpace(settings.Slicer))
        {
            string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Prusa3D", "PrusaSlicer", "prusa-slicer.exe");
            if (File.Exists(candidate)) settings.Slicer = candidate;
        }
        transport.Items.AddRange(["TCP", "COM"]); transport.SelectedItem = settings.Transport == "COM" ? "COM" : "TCP";
        address.Text = settings.Address; port.Value = Math.Clamp(settings.Port,1,65535); com.Text = settings.Com; baud.Value = Math.Clamp(settings.Baud,1200,1000000);
        fanIndex.Value=Math.Clamp(settings.FanIndex,0,7);
        standby.Checked = settings.AutoStandby;
        standby.CheckedChanged += async (_,_) => { settings.AutoStandby=standby.Checked; Save(); if(standby.Checked && controller is { Active:false }) await Guard(Standby); };
        var root = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=4, Padding=new Padding(10,3,10,3) };
        root.ColumnStyles.Add(new(SizeType.Absolute,245)); root.ColumnStyles.Add(new(SizeType.Percent,100));
        root.RowStyles.Add(new(SizeType.Absolute,42)); root.RowStyles.Add(new(SizeType.Percent,100)); root.RowStyles.Add(new(SizeType.Absolute,125)); root.RowStyles.Add(new(SizeType.Absolute,24));
        Controls.Add(root);
        var top=Row(); top.WrapContents=false; root.Controls.Add(top,0,0); root.SetColumnSpan(top,2);
        connectButton=Button(top,"Подключить",ToggleConnection); top.Controls.Add(stateLabel);
        printButton=Button(top,"▶ Печать",StartPrint);
        pauseButton=Button(top,"Пауза",()=> { if(controller?.State==JobState.Paused) controller.Resume(); else controller?.Pause(); return Task.CompletedTask; });
        cancelButton=Button(top,"Отменить",()=> { controller?.Cancel(); return Task.CompletedTask; });
        stopButton=Button(top,"Аварийный стоп",Emergency); stopButton.ForeColor=Color.Firebrick;
        top.Controls.Add(fileLabel);
        var side=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.TopDown, WrapContents=false, AutoScroll=true, Margin=new Padding(0,0,8,5) }; root.Controls.Add(side,0,1);
        Panel Group(int height) { var panel=new Panel { Width=230, Height=height, BackColor=Color.Black, BorderStyle=BorderStyle.FixedSingle, Margin=new Padding(0,0,0,7) }; side.Controls.Add(panel); return panel; }
        var heaters=Group(195);
        void Heater(int y,string name,NumericUpDown target,Label readout,CheckBox on,decimal max,Func<Task> set,Func<Task> off,bool enabled=true)
        {
            var title=new Label { Text=name, Location=new Point(7,y), AutoSize=true }; heaters.Controls.Add(title);
            readout.Text=enabled ? "— °C" : "не установлен"; readout.AutoSize=true; readout.Location=new Point(110,y); readout.ForeColor=enabled ? Color.WhiteSmoke : Color.Gray; heaters.Controls.Add(readout);
            target.Location=new Point(133,y+19); target.Width=64; target.ForeColor=Color.Black; target.Enabled=enabled; heaters.Controls.Add(target);
            var slider=new TrackBar { Location=new Point(3,y+15), Size=new Size(130,29), AutoSize=false, Minimum=0, Maximum=(int)max, Value=(int)target.Value, TickStyle=TickStyle.None, Enabled=enabled }; heaters.Controls.Add(slider);
            target.ValueChanged+=(_,_)=> { slider.Value=Math.Clamp((int)target.Value,slider.Minimum,slider.Maximum); };
            slider.ValueChanged+=(_,_)=> target.Value=slider.Value;
            slider.MouseUp+=async (_,_)=> { if(on.Checked && enabled) await Guard(set); };
            target.KeyDown+=async (_,e)=> { if(e.KeyCode==Keys.Enter && enabled) { e.SuppressKeyPress=true; await Guard(set); } };
            on.Location=new Point(204,y+22); on.Size=new Size(18,18); on.Enabled=enabled; heaters.Controls.Add(on);
            on.CheckedChanged+=async (_,_)=> { if(!syncingHeaters && enabled) await Guard(on.Checked ? set : off); };
            target.Enabled=enabled;
        }
        Heater(5,"Сопло T0",nozzle,nozzleReadout,nozzleOn,300,()=>Live("M104 S"+nozzle.Value),()=>Live("M104 S0"));
        Heater(52,"Сопло T1",Num(0,0,300),new Label(),new CheckBox(),300,()=>Task.CompletedTask,()=>Task.CompletedTask,false);
        Heater(99,"Стол",bed,bedReadout,bedOn,130,()=>Live("M140 S"+bed.Value),()=>Live("M140 S0"));
        var fanReadout=new Label();
        Heater(146,"Вентилятор",fan,fanReadout,fanOn,100,()=>Live($"M106 P{fanIndex.Value} S{Math.Round(fan.Value*255/100)}"),()=>Live("M107 P"+fanIndex.Value));
        fan.ValueChanged+=(_,_)=>fanReadout.Text=fan.Value+" %"; fanReadout.Text=fan.Value+" %";
        var motion=Group(184);
        Button At(Panel panel,string text,int x,int y,int w,int h,Func<Task> action,bool idle=true)
        {
            var button=Button(panel,text,action); button.AutoSize=false; button.SetBounds(x,y,w,h); if(idle) idleButtons.Add(button); return button;
        }
        At(motion,"Y+",57,6,39,30,()=>Require().Jog('Y',(double)step.Value,(double)jogFeed.Value));
        At(motion,"X−",12,41,39,30,()=>Require().Jog('X',-(double)step.Value,(double)jogFeed.Value));
        var jogStop=At(motion,"■",57,41,44,30,()=>Manual("M410")); tips.SetToolTip(jogStop,"Остановить ручное движение (M410)");
        At(motion,"X+",107,41,39,30,()=>Require().Jog('X',(double)step.Value,(double)jogFeed.Value));
        At(motion,"Y−",57,76,39,30,()=>Require().Jog('Y',-(double)step.Value,(double)jogFeed.Value));
        At(motion,"Z+",177,6,39,30,()=>Require().Jog('Z',(double)step.Value,(double)jogFeed.Value));
        At(motion,"Z−",177,76,39,30,()=>Require().Jog('Z',-(double)step.Value,(double)jogFeed.Value));
        motion.Controls.Add(new Label { Text="Шаг, мм",AutoSize=true,Location=new Point(7,109) });
        motion.Controls.Add(new Label { Text="Скор., мм/мин",AutoSize=true,Location=new Point(108,109) });
        step.SetBounds(8,126,77,25); jogFeed.SetBounds(112,126,100,25); step.ForeColor=jogFeed.ForeColor=Color.Black; motion.Controls.AddRange([step,jogFeed]);
        string[] stops=["x_min","x_max","y_min","y_max","z_min","z_max"];
        for(int i=0;i<stops.Length;i++) { var label=new Label { Text=new[]{"X−","X+","Y−","Y+","Z−","Z+"}[i],TextAlign=ContentAlignment.MiddleCenter,Location=new Point(7+i*36,155),Size=new Size(31,18),BackColor=Color.FromArgb(50,50,50) }; endstopLabels[stops[i]]=label; motion.Controls.Add(label); }
        var ext=Group(120);
        At(ext,"−",9,8,33,28,()=>Require().Extrude(-(double)extrudeLength.Value,(double)extrudeFeed.Value));
        At(ext,"+",176,8,33,28,()=>Require().Extrude((double)extrudeLength.Value,(double)extrudeFeed.Value));
        extrusionReadout.Text="T0 · — мм"; extrusionReadout.AutoSize=true; extrusionReadout.Location=new Point(50,14); ext.Controls.Add(extrusionReadout);
        var future=new Label { Text="T1 · второй экструдер не установлен",AutoSize=true,ForeColor=Color.Gray,Location=new Point(10,45) }; ext.Controls.Add(future);
        ext.Controls.Add(new Label { Text="Длина, мм",AutoSize=true,Location=new Point(7,70) }); ext.Controls.Add(new Label { Text="Скор., мм/мин",AutoSize=true,Location=new Point(108,70) });
        extrudeLength.SetBounds(8,88,77,25); extrudeFeed.SetBounds(112,88,100,25); extrudeLength.ForeColor=extrudeFeed.ForeColor=Color.Black; ext.Controls.AddRange([extrudeLength,extrudeFeed]);
        var rates=Group(74);
        rates.Controls.Add(new Label { Text="Скорость %",AutoSize=true,Location=new Point(7,9) }); speed.SetBounds(95,6,65,25); rates.Controls.Add(speed);
        var sb=At(rates,"✓",177,5,34,27,()=>Live("M220 S"+speed.Value),false); connectedButtons.Add(sb);
        rates.Controls.Add(new Label { Text="Подача %",AutoSize=true,Location=new Point(7,43) }); flow.SetBounds(95,40,65,25); rates.Controls.Add(flow);
        var fb=At(rates,"✓",177,39,34,27,()=>Live("M221 S"+flow.Value),false); connectedButtons.Add(fb); speed.ForeColor=flow.ForeColor=Color.Black;
        preview=new NativePreview(); root.Controls.Add(preview,1,1);
        var lower=new Panel { Dock=DockStyle.Fill, Margin=new Padding(0,0,8,0) }; root.Controls.Add(lower,0,2);
        lower.Controls.Add(new Label { Text="Домой",AutoSize=true,Location=new Point(5,0) });
        foreach(var pair in new[]{('X',5),('Y',47),('Z',89)}) At(lower,pair.Item1.ToString(),pair.Item2,20,35,30,()=>Manual("G28 "+pair.Item1));
        At(lower,"Все",133,20,40,30,()=>Manual("G28"));
        At(lower,"OFF",178,20,53,30,async ()=> { idleSuppressed=true; await Manual("M81"); });
        input.SetBounds(4,65,167,28); input.ForeColor=Color.Black; lower.Controls.Add(input);
        var send=At(lower,"Send",176,63,55,29,SendInput); input.KeyDown+=async (_,e)=> { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; await Guard(SendInput); } };
        progressLabel.SetBounds(4,100,230,22); lower.Controls.Add(progressLabel);
        journal.BackColor=Color.FromArgb(17,30,22); journal.ForeColor=Color.FromArgb(125,235,145); journal.BorderStyle=BorderStyle.FixedSingle; root.Controls.Add(journal,1,2);
        progress.Height=9; progress.Dock=DockStyle.Fill; root.Controls.Add(progress,1,3);
        var menu=new MenuStrip { BackColor=Color.WhiteSmoke,ForeColor=Color.Black }; MainMenuStrip=menu; Controls.Add(menu);
        ToolStripMenuItem Menu(string text) { var item=new ToolStripMenuItem(text); menu.Items.Add(item); return item; }
        void Item(ToolStripMenuItem parent,string text,Func<Task> action) { parent.DropDownItems.Add(text,null,async (_,_)=>await Guard(action)); }
        var file=Menu("Файл"); Item(file,"Открыть G-code…",OpenFile); Item(file,"Запустить PrusaSlicer",LaunchSlicer);
        Item(file,"Следующий полученный файл",async ()=> { if(controller?.Active==true) throw new InvalidOperationException("Сначала завершите задание."); if(deferredImports.TryDequeue(out string? path)) await Import(path); });
        Item(file,"Выход",()=> { Close(); return Task.CompletedTask; });
        var setup=Menu("Настройки"); Item(setup,"Подключение и приложение…",SettingsDialog); Item(setup,"EEPROM Marlin…",Eeprom); Item(setup,"Быстрые скрипты…",ScriptsDialog);
        Item(setup,"Параметры 3D-просмотрщика…",()=> { preview.Send("SETTINGS"); return Task.CompletedTask; });
        var idle=new ToolStripMenuItem("Температура ожидания автоматически") { Checked=standby.Checked,CheckOnClick=true }; idle.CheckedChanged+=(_,_)=>standby.Checked=idle.Checked; setup.DropDownItems.Add(idle);
        var diagnostic=new ToolStripMenuItem("Подробный журнал") { CheckOnClick=true }; diagnostic.CheckedChanged+=(_,_)=>details.Checked=diagnostic.Checked; setup.DropDownItems.Add(diagnostic);
        var view=Menu("Вид"); Item(view,"Сброс камеры",()=> { preview.Reset(); return Task.CompletedTask; });
        foreach(string name in new[]{"Сверху","Спереди","Справа"}) Item(view,name,()=> { preview.View(name); return Task.CompletedTask; });
        Item(view,"Очистить изображение",()=> { preview.ClearImage(); return Task.CompletedTask; }); Item(view,"Вернуть модель",()=> { preview.RestoreImage(); return Task.CompletedTask; });
        var show=new ToolStripMenuItem("Показывать экструзию") { Checked=true,CheckOnClick=true }; show.CheckedChanged+=(_,_)=>preview.Send(show.Checked ? "VISIBLE 1" : "VISIBLE 0"); view.DropDownItems.Add(show);
        var follow=new ToolStripMenuItem("Следовать за головкой") { CheckOnClick=true }; follow.CheckedChanged+=(_,_)=>preview.Follow=follow.Checked; view.DropDownItems.Add(follow);
        var commands=Menu("Команды"); Item(commands,"Нагрев ожидания",Standby); Item(commands,"Весь нагрев выключить",AllHeatOff); Item(commands,"Питание включить M80",()=>Manual("M80")); Item(commands,"Питание выключить M81",async ()=> { idleSuppressed=true; await Manual("M81"); });
        Item(commands,"Скорость и подача 100%",()=>Live("M220 S100\nM221 S100")); Item(commands,"Отключить двигатели",()=>Manual("M84")); Item(commands,"Очистить журнал",()=> { journal.Clear(); return Task.CompletedTask; });
        var scriptsMenu=Menu("Скрипты"); scriptsMenu.DropDownOpening+=(_,_)=> { scriptsMenu.DropDownItems.Clear(); for(int i=0;i<3;i++) { int n=i; Item(scriptsMenu,settings.ScriptNames[n],()=>Manual(settings.Scripts[n])); } };
        var help=Menu("Справка"); Item(help,"Управление",()=> { MessageBox.Show(this,"ЛКМ — вращение камеры; ПКМ — сдвиг; колесо — масштаб.\nПодключение настраивается через меню «Настройки».\nT1 зарезервирован для будущей головки.\nПауза ждёт текущую команду, затем поднимает Z на 15 мм и паркует X/Y.","Marlin Host"); return Task.CompletedTask; });
        Item(help,"О программе",()=> { MessageBox.Show(this,"Marlin Host 0.2\nПросмотрщик Marlin Simulator / MarlinSimUI.","О программе"); return Task.CompletedTask; });
        details.CheckedChanged+=(_,_)=>verbose=details.Checked;
        timer.Tick+=async (_,_)=> { if(Interlocked.Exchange(ref uiDirty,0)!=0) UpdateUi(); await Poll(); }; timer.Start(); FormClosing+=ClosingForm;
        Log("Marlin Host 0.2. Настройки подключения — в меню. Для симулятора: TCP 127.0.0.1:7000."); UpdateUi();
    }

    private Task ScriptsDialog()
    {
        using var dialog=new Form { Text="Быстрые скрипты",Size=new Size(610,590),StartPosition=FormStartPosition.CenterParent };
        var panel=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(10) }; dialog.Controls.Add(panel);
        var names=new List<TextBox>(); var codes=new List<TextBox>();
        for(int i=0;i<3;i++) { var name=new TextBox { Width=550,Text=settings.ScriptNames[i] }; var code=new TextBox { Width=550,Height=95,Multiline=true,ScrollBars=ScrollBars.Vertical,Text=settings.Scripts[i] }; panel.Controls.Add(name); panel.Controls.Add(code); names.Add(name); codes.Add(code); }
        Button(panel,"Сохранить",()=> { for(int i=0;i<3;i++) { settings.ScriptNames[i]=names[i].Text; settings.Scripts[i]=codes[i].Text; } Save(); dialog.Close(); return Task.CompletedTask; }); dialog.ShowDialog(this); return Task.CompletedTask;
    }

    internal async Task SaveSnapshot(string path)
    {
        for(int i=0;i<120 && preview.SceneCount==0 && job is not null;i++) await Task.Delay(100);
        await Task.Delay(300);
        string native=Path.ChangeExtension(path,".native.bmp");
        preview.Snapshot(native);
        for(int i=0;i<40 && !File.Exists(native);i++) await Task.Delay(100);
        using var bitmap=new Bitmap(Width,Height); DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));
        if(File.Exists(native))
        {
            using var image=Image.FromFile(native); using var graphics=Graphics.FromImage(bitmap);
            Point screen=preview.PointToScreen(Point.Empty); graphics.DrawImage(image,new Rectangle(screen.X-Left,screen.Y-Top,preview.Width,preview.Height));
        }
        bitmap.Save(path);
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(2, 7, 2, 0) };
    private static NumericUpDown Num(decimal value, decimal min, decimal max, int decimals = 0) => new() { Width = 75, Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals > 0 ? .1m : 1 };
    private static FlowLayoutPanel Row() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2), MinimumSize = new Size(0, 34) };
    private Button Button(Control parent, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 31, Padding = new Padding(3, 0, 3, 0), ForeColor=Color.Black, BackColor=Color.WhiteSmoke, UseVisualStyleBackColor=false };
        b.Click += async (_, _) => await Guard(action); parent.Controls.Add(b); return b;
    }
    private void IdleButton(Control p, string t, Func<Task> a) => idleButtons.Add(Button(p, t, a));
    private void ConnectedButton(Control p, string t, Func<Task> a) => connectedButtons.Add(Button(p, t, a));
    private PrintController Require() => controller is not null && link?.Connected == true ? controller : throw new IOException("Подключите принтер.");
    private async Task Guard(Func<Task> action) { try { await action(); } catch (Exception ex) { Log("Ошибка: " + ex.Message); } finally { UpdateUi(); } }
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) action(); })); }
    private void Log(string text) { if (journal.TextLength > 100000) journal.Clear(); journal.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n"); journal.ScrollToCaret(); }
    private void Save()
    {
        settings.Transport = transport.Text; settings.Address = address.Text.Trim(); settings.Port = (int)port.Value; settings.Com = com.Text; settings.Baud = (int)baud.Value;
        settings.FanIndex=(int)fanIndex.Value;
        try { settings.Save(); } catch (IOException ex) { Log("Настройки не сохранены: " + ex.Message); }
    }
    private void UpdateUi()
    {
        bool connected = link?.Connected == true, active = controller?.Active == true;
        com.Visible = baud.Visible = transport.Text == "COM"; address.Visible = port.Visible = transport.Text == "TCP";
        transport.Enabled = address.Enabled = port.Enabled = com.Enabled = baud.Enabled = !connected && !connecting;
        connectButton.Text = connected ? "Отключить" : "Подключить"; connectButton.Enabled = !connecting && !active;
        foreach (var b in idleButtons) b.Enabled = connected && !active;
        foreach (var b in connectedButtons) b.Enabled = connected;
        printButton.Enabled = connected && !active && job is not null;
        pauseButton.Enabled = connected && controller?.State is JobState.Printing or JobState.Paused;
        pauseButton.Text = controller?.State == JobState.Paused ? "Продолжить" : "Пауза";
        cancelButton.Enabled = connected && active && controller?.State != JobState.Cancelling;
        stopButton.Enabled = connected; nozzleOn.Enabled=bedOn.Enabled=fanOn.Enabled=connected;
        input.Enabled = connected && !active;
        stateLabel.Text = !connected ? "Нет связи" : "Подключён · " + StateName(controller?.State ?? JobState.Idle);
        stateLabel.BackColor=connected ? Color.DarkGreen : Color.FromArgb(76,76,76);
        if (connected && controller?.State == JobState.Paused && !controller.Parked) stateLabel.Text = "Подключён · пауза без парковки";
        fileLabel.Text = job is null ? "Файл не загружен" : Path.GetFileName(job.FileName);
        if (job is not null)
        {
            int sent = hasRun ? Math.Min(controller?.Progress ?? 0, job.Commands.Length) : 0;
            progress.Value = job.Commands.Length > 0 ? (int)(sent * 100L / job.Commands.Length) : 0;
            progressLabel.Text = $"{sent:N0} / {job.Commands.Length:N0} команд";
            preview.Progress = sent; preview.Invalidate();
        }
    }
    private static string StateName(JobState s) => s switch { JobState.Idle => "готов", JobState.Printing => "печать", JobState.Pausing => "ожидание паузы", JobState.Paused => "пауза, головка припаркована", JobState.Resuming => "возврат к печати", JobState.Cancelling => "отмена после текущей команды", JobState.Completed => "завершено", JobState.Cancelled => "отменено", _ => "ошибка — проверьте журнал" };

    private async Task ToggleConnection()
    {
        if (link?.Connected == true) { await Disconnect(); return; }
        connecting = true; UpdateUi();
        try
        {
            if (link is not null) await link.DisposeAsync();
            var next = new PrinterLink(); link = next;
            next.Received += line =>
            {
                if(verbose || line.StartsWith("X:") || line.Contains("T:") || line.Contains("T0:") || line.Contains("B:") ||
                    line.StartsWith("Error:") || line.StartsWith("!!") || line.Contains("Unknown command") || line.Contains("cold extrusion") ||
                    line.Contains("_min:") || line.Contains("_max:"))
                    Ui(() => { if (ReferenceEquals(link, next)) Receive(line); });
            };
            next.Sent += line => { if(verbose) Ui(() => Log("→ " + line)); };
            next.Lost += reason => Ui(() => { idleSuppressed = true; Log("Связь остановлена: " + reason); UpdateUi(); });
            if (transport.Text == "TCP") await next.ConnectTcp(address.Text.Trim(), (int)port.Value);
            else await next.ConnectSerial(com.Text.Trim(), (int)baud.Value);
            controller = new PrintController(next);
            controller.Message += text => Ui(() => Log(text)); controller.Changed += () => Interlocked.Exchange(ref uiDirty,1);
            await controller.Initialize();
            if (controller.MaxX is > 0 && controller.MaxY is > 0) { preview.BedX = controller.MaxX.Value; preview.BedY = controller.MaxY.Value; }
            idleSuppressed = false; Save(); Log("Подключено. Marlin готов.");
            if (settings.AutoStandby) await Standby();
        }
        catch { if (link is not null) await link.DisposeAsync(); link = null; controller = null; throw; }
        finally { connecting = false; UpdateUi(); }
    }
    private async Task Disconnect()
    {
        if (controller?.Active == true) throw new InvalidOperationException("Сначала отмените задание или используйте аварийный стоп.");
        if (link is not null) await link.DisposeAsync();
        link = null; controller = null; Log("Отключено."); UpdateUi();
    }
    private void Receive(string line)
    {
        var t = Regex.Match(line, @"(?:^|\s)T(?:0)?:\s*([-\d.]+)\s*/\s*([-\d.]+)");
        var b = Regex.Match(line, @"(?:^|\s)B:\s*([-\d.]+)\s*/\s*([-\d.]+)");
        if (t.Success) { lastNozzle = double.Parse(t.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); temperatures.Text = $"Сопло {lastNozzle:0.0} / {t.Groups[2].Value} °C\n" + (b.Success ? $"Стол {b.Groups[1].Value} / {b.Groups[2].Value} °C" : $"Стол {lastBed:0.0} °C"); }
        syncingHeaters=true;
        if(t.Success) { nozzleReadout.Text=t.Groups[1].Value+" / "+t.Groups[2].Value+" °C"; nozzleOn.Checked=double.Parse(t.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture)>0; }
        if(b.Success) { bedReadout.Text=b.Groups[1].Value+" / "+b.Groups[2].Value+" °C"; bedOn.Checked=double.Parse(b.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture)>0; }
        syncingHeaters=false;
        var limit=Regex.Match(line,@"^(x_min|x_max|y_min|y_max|z_min|z_max):\s*(\w+)",RegexOptions.IgnoreCase);
        if(limit.Success && endstopLabels.TryGetValue(limit.Groups[1].Value.ToLowerInvariant(),out var led)) led.BackColor=limit.Groups[2].Value.Equals("TRIGGERED",StringComparison.OrdinalIgnoreCase) ? Color.Firebrick : Color.DarkGreen;
        var extruded=Regex.Match(line,@"^X:.*?E:\s*([-\d.]+)"); if(extruded.Success) extrusionReadout.Text="T0 · "+extruded.Groups[1].Value+" мм";
        if (b.Success) lastBed = double.Parse(b.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var p = Regex.Match(line, @"^X:\s*([-\d.]+)\s*Y:\s*([-\d.]+)\s*Z:\s*([-\d.]+)");
        if (p.Success) { preview.Head = new Vector3(float.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(p.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), float.Parse(p.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)); preview.Invalidate(); }
        if (details.Checked || line.StartsWith("Error:") || line.Contains("Unknown command") || line.Contains("cold extrusion") || line.StartsWith("!!")) Log("← " + line);
    }
    private async Task Poll()
    {
        if (polling || connecting || closing || link?.Connected != true || controller is null) return;
        polling = true;
        try
        {
            bool slow=Environment.TickCount64>=nextSlowPoll;
            await controller.Manual(slow ? "M105\nM114\nM119" : "M114", true);
            if(slow) nextSlowPoll=Environment.TickCount64+2000;
        }
        catch (Exception ex) { if (link?.Connected == true) Log(ex.Message); }
        finally { polling = false; UpdateUi(); }
    }
    private async Task Live(string command) { Log("Команда: " + command); await Require().Manual(command, true); }
    private async Task Manual(string command) { Log("Ручная команда: " + command.Replace('\n', ' ')); await Require().Manual(command); }
    private async Task SendInput()
    {
        string command = input.Text.Trim(); if (command.Length == 0) return;
        await Manual(command); if (!input.Items.Contains(command)) input.Items.Insert(0, command); input.Text = "";
    }
    private async Task Standby()
    {
        if (Require().Active) throw new InvalidOperationException("Режим ожидания доступен вне задания.");
        idleSuppressed = false;
        await Manual($"M140 S{settings.StandbyBed}\nM104 S{settings.StandbyNozzle}");
        Log("Поддержание температуры ожидания включено.");
    }
    private async Task AllHeatOff() { idleSuppressed = true; await Live("M104 S0\nM140 S0"); }
    private async Task Emergency()
    {
        idleSuppressed = true; Log("АВАРИЙНЫЙ СТОП M112. Перед продолжением перезапустите принтер.");
        await Require().Emergency(); UpdateUi();
    }
    private async Task OpenFile() { using var dialog = new OpenFileDialog { Filter = "G-code|*.gcode;*.gco;*.g;*.gc" }; if (dialog.ShowDialog(this) == DialogResult.OK) await Import(dialog.FileName); }
    public async Task Import(string path)
    {
        if (controller?.Active == true)
        {
            deferredImports.Enqueue(path); pendingNotice = "Получен новый файл: " + Path.GetFileName(path) + ". Он доступен после текущего задания."; Log(pendingNotice); return;
        }
        long generation = ++importGeneration;
        try
        {
            var loaded = await Task.Run(() => PrintJob.Load(path));
            if (generation != importGeneration) return;
            if (controller?.Active == true) { deferredImports.Enqueue(path); Log("Новый файл отложен до завершения печати."); return; }
            job = loaded; hasRun = false; preview.Progress = 0; preview.Job = loaded;
            Log($"Загружен {Path.GetFileName(path)} · {job.Commands.Length:N0} команд. Запуск — кнопкой «Печать».");
            if (job.HasArcs) Log("ВНИМАНИЕ: дуги G2/G3 в предварительном просмотре показаны хордами; на принтер передаются исходные дуги.");
            if (deferredImports.Count > 0) Log("Есть отложенные файлы; откройте их через меню загрузки после задания.");
            Activate(); UpdateUi();
        }
        catch (Exception ex) { Log("Файл не загружен: " + ex.Message); }
    }
    private async Task StartPrint()
    {
        if (job is null) return;
        var current = Require(); if (current.Active) return;
        idleSuppressed = false; hasRun = true; preview.Progress=0; preview.RestoreImage(); Log("Печать: " + job.FileName);
        running = current.Run(job); UpdateUi();
        try
        {
            await running; Log("Задание: " + StateName(current.State));
            if (current.State == JobState.Completed && settings.AutoStandby && !idleSuppressed) await Standby();
        }
        finally
        {
            running = null; UpdateUi();
            if (!current.Active && deferredImports.TryDequeue(out string? path)) await Import(path);
        }
    }
    private Task LaunchSlicer()
    {
        if (!File.Exists(settings.Slicer)) throw new IOException("Укажите путь к PrusaSlicer в настройках.");
        Process.Start(new ProcessStartInfo(settings.Slicer) { UseShellExecute = true })?.Dispose(); return Task.CompletedTask;
    }

    private Task SettingsDialog()
    {
        using var dialog = new Form { Text = "Настройки Marlin Host", Size = new Size(800, 660), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(15), AutoScroll = true }; dialog.Controls.Add(panel);
        panel.Controls.Add(Label("Подключение к принтеру"));
        var connectionRow=Row();
        var mode=new ComboBox { Width=70,DropDownStyle=ComboBoxStyle.DropDownList }; mode.Items.AddRange(["TCP","COM"]); mode.SelectedItem=transport.Text;
        var host=new TextBox { Width=140,Text=address.Text }; var tcp=Num(port.Value,1,65535);
        var serial=new ComboBox { Width=110,Text=com.Text }; serial.Items.AddRange(SerialPort.GetPortNames()); var rate=Num(baud.Value,1200,1000000); rate.Width=100;
        connectionRow.Controls.AddRange([mode,host,tcp,serial,rate]); connectionRow.Enabled=link?.Connected!=true; panel.Controls.Add(connectionRow);
        void ShowTransport() { host.Visible=tcp.Visible=mode.Text=="TCP"; serial.Visible=rate.Visible=mode.Text=="COM"; }
        mode.SelectedIndexChanged+=(_,_)=>ShowTransport(); ShowTransport();
        panel.Controls.Add(Label("Для изменения связи сначала отключите принтер. COM: 8N1, без flow control."));
        panel.Controls.Add(Label("Путь к PrusaSlicer"));
        var slicer = new TextBox { Width = 720, Text = settings.Slicer }; panel.Controls.Add(slicer);
        Button(panel, "Выбрать программу…", () => { using var file = new OpenFileDialog { Filter = "Программа|*.exe" }; if (file.ShowDialog(dialog) == DialogResult.OK) slicer.Text = file.FileName; return Task.CompletedTask; });
        panel.Controls.Add(Label("Температуры ожидания: сопло / стол, °C"));
        var standbyRow = Row(); var sn = Num(Math.Clamp(settings.StandbyNozzle, 0, 300), 0, 300); var sb = Num(Math.Clamp(settings.StandbyBed, 0, 130), 0, 130); standbyRow.Controls.AddRange([sn, sb]); panel.Controls.Add(standbyRow);
        var fanRow=Row(); fanRow.Controls.Add(Label("Номер вентилятора P")); var fanNumber=Num(fanIndex.Value,0,7); fanRow.Controls.Add(fanNumber); panel.Controls.Add(fanRow);
        panel.Controls.Add(new Label { Text = "Автозагрузка после экспорта: добавьте эту строку ПОСЛЕДНЕЙ в PrusaSlicer:\nНастройки печати → Параметры вывода → Скрипты постобработки.\nСохраните профиль печати. Используйте обычный .gcode.", AutoSize = true });
        var integration = new TextBox { Width = 720, ReadOnly = true, Text = $"\"{Application.ExecutablePath}\" --from-slicer" }; panel.Controls.Add(integration);
        Button(panel, "Скопировать строку", () => { Clipboard.SetText(integration.Text); return Task.CompletedTask; });
        panel.Controls.Add(new Label { Text = "После успешного экспорта файл загрузится автоматически. Печать сама не запускается.\nПри активном задании новый файл откладывается до его завершения.\nВторая головка будет добавлена в следующей версии.", AutoSize = true });
        Button(panel, "Сохранить", () => { transport.SelectedItem=mode.Text; address.Text=host.Text; port.Value=tcp.Value; com.Text=serial.Text; baud.Value=rate.Value; fanIndex.Value=fanNumber.Value; settings.Slicer = slicer.Text.Trim(); settings.StandbyNozzle = (int)sn.Value; settings.StandbyBed = (int)sb.Value; Save(); dialog.Close(); return Task.CompletedTask; });
        dialog.ShowDialog(this); return Task.CompletedTask;
    }

    private async Task Eeprom()
    {
        var current = Require(); if (current.Active) return;
        using var dialog = new Form { Text = "Параметры Marlin / EEPROM", Size = new Size(1000, 700), StartPosition = FormStartPosition.CenterParent };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) }; layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 130)); dialog.Controls.Add(layout);
        var actions = Row(); layout.Controls.Add(actions, 0, 0);
        var raw = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) }; layout.Controls.Add(raw, 0, 2);
        var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = "Параметр / единицы", ReadOnly = true, FillWeight = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Поле", ReadOnly = true, FillWeight = 15 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Значение", FillWeight = 25 });
        layout.Controls.Add(grid, 0, 1);
        async Task Read()
        {
            var lines = await current.Manual("M503 S0"); raw.Text = string.Join(Environment.NewLine, lines); grid.Rows.Clear(); eepromRows.Clear();
            foreach (string line in lines)
            {
                string command = GCode.Clean(Regex.Replace(line, @"^echo:\s*", "", RegexOptions.IgnoreCase));
                string op = GCode.Op(command);
                if (!EepromNames.TryGetValue(op, out string? name)) continue;
                foreach (Match word in Regex.Matches(command[op.Length..], @"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))"))
                {
                    char key = word.Groups[1].Value[0]; if (key == 'T' && op is "M301" or "M200" or "M900" or "M906" or "M913" or "M914") continue;
                    int row = grid.Rows.Add($"{op} · {name}", key.ToString(), word.Groups[2].Value);
                    eepromRows.Add((op, command, row, key));
                }
            }
            Log("EEPROM: текущие параметры прочитаны. Неизвестные строки сохранены в отчёте ниже.");
        }
        Button(actions, "Прочитать", Read);
        Button(actions, "Применить в RAM", async () =>
        {
            grid.EndEdit();
            var commands = new List<string>();
            foreach (var group in eepromRows.GroupBy(r => r.Raw))
            {
                string command = group.Key;
                foreach (var item in group)
                {
                    string value = Convert.ToString(grid.Rows[item.Row].Cells["Value"].Value)?.Replace(',', '.') ?? "";
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed)) throw new IOException("Некорректное число: " + value);
                    command = Regex.Replace(command, $@"(?<![A-Z]){item.Key}\s*[-+]?(?:\d+\.?\d*|\.\d+)", item.Key + GCode.N(parsed));
                }
                if (command != group.Key) commands.Add(command);
            }
            if (commands.Count == 0) { Log("EEPROM: изменений нет."); return; }
            await current.Manual(string.Join('\n', commands)); Log("Параметры применены в RAM. Для постоянного хранения нажмите «Сохранить M500»."); await Read();
        });
        Button(actions, "Сохранить M500", async () => { var reply = await current.Manual("M500"); raw.Text = string.Join(Environment.NewLine, reply); Log("Ответ M500: " + string.Join(" · ", reply)); });
        Button(actions, "Загрузить M501", async () => { await current.Manual("M501"); await Read(); });
        Button(actions, "Заводские M502", async () => { if (MessageBox.Show(dialog, "Восстановить заводские значения в RAM? EEPROM изменится только после M500.", "Подтверждение", MessageBoxButtons.YesNo) == DialogResult.Yes) { await current.Manual("M502"); await Read(); } });
        await Read(); dialog.ShowDialog(this); eepromRows.Clear();
    }
    private static readonly Dictionary<string, string> EepromNames = new()
    {
        ["M92"] = "Шаги на мм", ["M201"] = "Макс. ускорения, мм/с²", ["M203"] = "Макс. скорости, мм/с", ["M204"] = "Ускорения, мм/с²", ["M205"] = "Расширенные параметры движения", ["M206"] = "Смещение нуля, мм", ["M207"] = "Откат", ["M208"] = "Возврат после отката", ["M301"] = "PID сопла", ["M304"] = "PID стола", ["M851"] = "Смещение датчика, мм", ["M900"] = "Linear Advance", ["M906"] = "Ток драйверов, мА", ["M913"] = "Порог Hybrid", ["M914"] = "Sensorless", ["M200"] = "Объёмная экструзия", ["M666"] = "Коррекция концевиков, мм", ["M420"] = "Выравнивание стола"
    };
    private async void ClosingForm(object? sender, FormClosingEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true; if (closing) return;
        if (controller?.Active == true && MessageBox.Show(this, "Выполняется задание. Аварийно остановить принтер и закрыть приложение?", "Закрытие", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        closing = true; timer.Stop(); Save();
        try
        {
            if (controller?.Active == true && link?.Connected == true) await controller.Emergency();
            if (link is not null) await link.DisposeAsync();
            if (running is not null) try { await running; } catch (Exception) { }
        }
        catch (Exception ex) { Log(ex.Message); }
        closeReady = true; Close();
    }
}
