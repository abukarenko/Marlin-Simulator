using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using MarlinEmulator.Core;

namespace MarlinEmulator.App;

internal sealed class MainForm : Form
{
    private readonly RichTextBox journal = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(24, 29, 38), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 10) };
    private readonly TextBox command = new() { Width = 400, PlaceholderText = "G-code, например M115" };
    private readonly ComboBox ports = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox baudRates = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox connectionType = new() { Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown tcpPort = new() { Width = 100, Minimum = 1, Maximum = 65535, Value = 7000 };
    private readonly Label connectionHint = new() { AutoSize = true, Padding = new Padding(5, 6, 0, 0) };
    private readonly Label status = new() { AutoSize = true, Text = "Прошивка не подключена", Padding = new Padding(8) };
    private readonly List<Control> localControls = [];
    private readonly Button bridgeButton;
    private FirmwareSession? session;
    private SerialBridge? bridge;
    private TcpConnection? tcpConnection;
    private ResetServer? resetServer;
    private bool ExternalConnected => bridge is not null || tcpConnection is not null;
    private CancellationTokenSource? printing;
    private Process? firmware;
    private bool busy;
    private bool closing;
    private bool closeReady;
    private bool changingPort;
    private string? connectedPort;
    private readonly bool persistSettings;
    private readonly System.Windows.Forms.Timer settingsTimer = new() { Interval = 500 };
    private bool restoringSettings;

    public MainForm(bool persistSettings = true)
    {
        this.persistSettings = persistSettings;
        Text = "Marlin Emulator · 2.1.2.9";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Size = new Size(1060, 680);
        MinimumSize = new Size(850, 500);
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        Controls.Add(layout);
        var menu = new MenuStrip();
        var settings = new ToolStripMenuItem("Settings");
        settings.DropDownItems.Add("Reset Default", null, (_, _) => { RestoreSettings(new WindowPreferences()); SaveSettings(); });
        menu.Items.Add(settings); Controls.Add(menu); MainMenuStrip = menu;
        FlowLayoutPanel Row(int index)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            layout.Controls.Add(row, 0, index);
            return row;
        }
        Button Add(FlowLayoutPanel row, string text, Func<Task> action, bool local = false)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 32 };
            button.Click += async (_, _) => await Guard(action);
            row.Controls.Add(button);
            if (local) localControls.Add(button);
            return button;
        }
        var serial = Row(0);
        serial.Controls.Add(new Label { Text = "Подключение:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        connectionType.Items.AddRange(new object[] { "Локально", "COM", "TCP" });
        connectionType.SelectedIndex = 0;
        serial.Controls.Add(connectionType);
        serial.Controls.Add(ports);
        ports.DropDown += (_, _) => RefreshPorts();
        RefreshPorts();
        baudRates.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200", "250000", "500000" });
        baudRates.SelectedItem = "115200";
        serial.Controls.Add(baudRates);
        serial.Controls.Add(tcpPort);
        bridgeButton = Add(serial, "Подключить", ToggleBridge);
        serial.Controls.Add(connectionHint);
        connectionType.SelectedIndexChanged += (_, _) => UpdateControls();
        var actions = Row(1);
        Add(actions, "Home (G28)", () => Send("G28"), true);
        Add(actions, "Температуры", () => Send("M105"), true);
        Add(actions, "Позиция", () => Send("M114"), true);
        Add(actions, "Настройки", () => Send("M503"), true);
        Add(actions, "Открыть G-code", PrintFile, true);
        Add(actions, "Аварийный стоп", EmergencyStop);
        Add(actions, "Очистить журнал", () => { journal.Clear(); return Task.CompletedTask; });
        layout.Controls.Add(journal, 0, 2);
        var input = Row(3);
        input.Controls.Add(command);
        localControls.Add(command);
        Add(input, "Отправить", SendInput, true);
        command.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Guard(SendInput); } };
        layout.Controls.Add(status, 0, 4);
        Log("Marlin 2.1.2.9 · Cartesian · 220 × 220 × 250 мм · 1 экструдер + нагреваемый стол");
        Log("Для печати файла выберите «Локально» и нажмите «Запустить». COM/TCP — для внешней программы.");
        FormClosing += OnClosing;
        UpdateControls();
        if (persistSettings) RestoreSettings(WindowPreferences.Load());
        settingsTimer.Tick += (_, _) => { settingsTimer.Stop(); SaveSettings(); };
        void Changed(object? sender, EventArgs args) { if (!closing && !restoringSettings && persistSettings) { settingsTimer.Stop(); settingsTimer.Start(); } }
        connectionType.SelectedIndexChanged += Changed; ports.SelectedIndexChanged += Changed;
        baudRates.SelectedIndexChanged += Changed; tcpPort.ValueChanged += Changed;
        Move += Changed; Resize += Changed;
    }

    private void RestoreSettings(WindowPreferences settings)
    {
        restoringSettings = true;
        try
        {
            if (firmware is not { HasExited: false } && !ExternalConnected)
            {
                connectionType.SelectedItem = connectionType.Items.Contains(settings.Transport) ? settings.Transport : "Локально";
                if (ports.Items.Contains(settings.Port)) ports.SelectedItem = settings.Port;
                if (baudRates.Items.Contains(settings.Baud)) baudRates.SelectedItem = settings.Baud;
                tcpPort.Value = Math.Clamp(settings.TcpPort, 1, 65535);
            }
            WindowState = FormWindowState.Normal;
            Size = new Size(Math.Clamp(settings.Width, 850, 7680), Math.Clamp(settings.Height, 500, 4320));
            if (settings.Left is int x && settings.Top is int y && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x,y,Width,Height))))
            { StartPosition = FormStartPosition.Manual; Location = new Point(x,y); }
            if (settings.Maximized) WindowState = FormWindowState.Maximized;
        }
        finally { restoringSettings = false; UpdateControls(); }
    }

    private void SaveSettings()
    {
        if (!persistSettings || restoringSettings || IsDisposed) return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try
        {
            new WindowPreferences { Transport=connectionType.Text, Port=ports.Text, Baud=baudRates.Text,
                TcpPort=(int)tcpPort.Value, Left=bounds.Left, Top=bounds.Top, Width=bounds.Width, Height=bounds.Height,
                Maximized=WindowState==FormWindowState.Maximized }.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Не удалось сохранить настройки: " + ex.Message); }
    }

    private void RefreshPorts()
    {
        var selected = ports.Text;
        ports.Items.Clear();
        ports.Items.AddRange(SerialPort.GetPortNames().Order().Cast<object>().ToArray());
        if (ports.Items.Contains(selected)) ports.SelectedItem = selected;
        else if (ports.Items.Count > 0) ports.SelectedIndex = 0;
    }

    private async Task StartFirmware()
    {
        if (ExternalConnected || session?.IsConnected == true) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "firmware", "MarlinSimulator.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Сначала выполните scripts\\Build.ps1: файл прошивки ещё не собран.", exe);
        if (firmware is not { HasExited: false })
        {
            firmware?.Dispose();
            firmware = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true })
                ?? throw new IOException("Не удалось запустить симулятор.");
            var started = firmware;
            started.EnableRaisingEvents = true;
            started.Exited += (_, _) => Ui(() => { if (ReferenceEquals(firmware, started)) _ = Guard(SimulatorExited); });
            Log("Принтер запускается…");
        }
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (closing) throw new OperationCanceledException();
            if (firmware?.HasExited == true) throw new IOException("Окно принтера завершилось при запуске.");
            try { await Connect(); if (closing) throw new OperationCanceledException(); Log("Принтер готов к работе."); return; }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new IOException("Принтер не ответил после запуска. Повторите запуск.");
    }

    private async Task Connect()
    {
        await Disconnect();
        var next = new FirmwareSession();
        next.LineReceived += line => Ui(() => Log("← " + line));
        next.CommandSent += line => Ui(() => Log("→ " + line));
        next.Disconnected += message => Ui(() => { Log(message); UpdateControls(); });
        try { await next.ConnectAsync(); }
        catch { await next.DisposeAsync(); throw; }
        if (closing) { await next.DisposeAsync(); throw new OperationCanceledException(); }
        session = next;
        UpdateControls();
    }

    private async Task Disconnect()
    {
        printing?.Cancel();
        if (session is { } old) { session = null; await old.DisposeAsync(); }
        if (bridge is { } oldBridge) { bridge = null; await oldBridge.DisposeAsync(); }
        if (resetServer is { } oldReset) { resetServer=null; await oldReset.DisposeAsync(); }
        if (tcpConnection is { } oldTcp) { tcpConnection = null; await oldTcp.DisposeAsync(); }
        connectedPort = null;
        UpdateControls();
    }

    private async Task SimulatorExited()
    {
        await Disconnect();
        firmware?.Dispose();
        firmware = null;
        Log("Симулятор закрыт. Соединение отключено.");
        UpdateControls();
    }

    private async Task StopSimulator()
    {
        var current = firmware;
        firmware = null; // Ignore its exit callback while shutting down explicitly.
        await Disconnect();
        if (current is null) return;
        try
        {
            if (!current.HasExited)
            {
                current.CloseMainWindow();
                try { await current.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { current.Kill(entireProcessTree: true); await current.WaitForExitAsync(); }
            }
        }
        finally { current.Dispose(); UpdateControls(); }
    }

    private async Task ResetFirmwareForHost()
    {
        if(closing || changingPort)throw new IOException("Эмулятор занят сменой подключения.");
        changingPort=true; UpdateControls();
        try
        {
            var old=firmware; firmware=null;
            if(old is not null)
            {
                try { if(!old.HasExited) { old.Kill(entireProcessTree:true); await old.WaitForExitAsync(); } }
                finally { old.Dispose(); }
            }
            string exe=Path.Combine(AppContext.BaseDirectory,"firmware","MarlinSimulator.exe");
            var started=Process.Start(new ProcessStartInfo(exe) { WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=true }) ?? throw new IOException("Не удалось перезапустить прошивку.");
            firmware=started; started.EnableRaisingEvents=true;
            started.Exited+=(_,_)=>Ui(()=> { if(ReferenceEquals(firmware,started)) _=Guard(SimulatorExited); });
            for(int i=0;i<100;i++)
            {
                if(closing || started.HasExited)throw new IOException("Прошивка завершилась при сбросе.");
                try { using var probe=new TcpClient(); await probe.ConnectAsync("127.0.0.1",8099); Log("Hard Reset: прошивка перезапущена. Задание не возобновляется."); return; }
                catch(SocketException) { await Task.Delay(100); }
            }
            throw new IOException("Прошивка не открыла порт после сброса.");
        }
        finally { changingPort=false; UpdateControls(); }
    }

    private async Task ToggleBridge()
    {
        if (closing || changingPort || busy) return;
        changingPort = true; UpdateControls();
        try
        {
            if (ExternalConnected || firmware is { HasExited: false })
            {
                var oldPort = connectedPort;
                await StopSimulator();
                Log(oldPort is null ? "Локальная работа завершена. Симулятор закрыт." : $"Порт {oldPort} отключён. Симулятор закрыт.");
                return;
            }
            if (connectionType.Text == "Локально")
            {
                await StartFirmware();
                Log("Локальный режим: откройте G-code или отправьте команду.");
                return;
            }
            if (connectionType.Text == "TCP")
            {
                int number = (int)tcpPort.Value;
                if (number is 8098 or 8099 or 65535) throw new InvalidOperationException("Порт 8099 уже используется. Выберите другой TCP-порт.");
                await StartFirmware();
                await Disconnect();
                await Task.Delay(100);
                if (closing || firmware is not { HasExited: false }) throw new OperationCanceledException();
                var tcp = new TcpConnection(number);
                tcp.Message += text => Ui(() => Log(text));
                try { tcp.Start(); }
                catch
                {
                    await tcp.DisposeAsync();
                    throw;
                }
                tcpConnection = tcp;
                resetServer=new ResetServer(number+1,()=> {
                    var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Ui(async ()=> { try { await ResetFirmwareForHost(); done.TrySetResult(); } catch(Exception ex) { Log("Hard Reset: "+ex.Message); done.TrySetException(new IOException(ex.Message,ex)); } });
                    return done.Task;
                });
                resetServer.Start();
                connectedPort = $"TCP 127.0.0.1:{number}";
                Log($"{connectedPort} — ожидание управляющей программы.");
                return;
            }
            if (ports.SelectedItem is not string port) throw new InvalidOperationException("Выберите COM-порт.");
            int baud = int.Parse(baudRates.Text);
            await StartFirmware();
            await Disconnect();
            // Let the simulator release the previous local connection.
            await Task.Delay(100);
            if (closing || firmware is not { HasExited: false }) throw new OperationCanceledException();
            var next = new SerialBridge(port, baud);
            next.Closed += message => Ui(() => { if (ReferenceEquals(bridge, next)) { Log(message); _ = Guard(StopSimulator); } });
            try { await next.StartAsync(); }
            catch
            {
                await next.DisposeAsync();
                throw;
            }
            if (closing || firmware is not { HasExited: false }) { await next.DisposeAsync(); throw new OperationCanceledException(); }
            bridge = next;
            connectedPort = port;
            Log($"Подключено: {port}, {baud} бод, 8N1.");
        }
        catch { await StopSimulator(); throw; }
        finally { changingPort = false; UpdateControls(); }
    }

    private async Task Send(string text)
    {
        if (session is not { IsConnected: true } current) throw new InvalidOperationException("Сначала запустите принтер в режиме «Локально».");
        busy = true; UpdateControls();
        try { await current.SendCommandAsync(text); }
        finally { busy = false; UpdateControls(); }
    }

    private async Task SendInput()
    {
        string text = command.Text.Trim();
        if (text.Length == 0) return;
        command.Clear();
        await Send(text);
    }

    private async Task PrintFile()
    {
        using var dialog = new OpenFileDialog { Filter = "G-code|*.gcode;*.gco;*.gc;*.g|Все файлы|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (session is not { IsConnected: true } current) return;
        printing = new CancellationTokenSource();
        busy = true; UpdateControls();
        try
        {
            Log("Печать: " + dialog.FileName);
            await current.PrintFileAsync(dialog.FileName, new Progress<int>(n => status.Text = $"Отправлено команд: {n}"), printing.Token);
            Log("Файл выполнен, движение завершено (M400).");
        }
        finally { printing.Dispose(); printing = null; busy = false; UpdateControls(); }
    }

    private async Task EmergencyStop()
    {
        if (ExternalConnected) throw new InvalidOperationException("При внешнем подключении отправьте M112 из управляющей программы или остановите принтер в окне симуляции.");
        if (session is not { IsConnected: true } current) return;
        await current.EmergencyStopAsync();
        printing?.Cancel();
        Log("Отправлен M112. Для восстановления может потребоваться перезапуск принтера.");
    }

    private void UpdateControls()
    {
        bool local = connectionType.Text == "Локально";
        bool running = firmware is { HasExited: false } || ExternalConnected;
        bool com = connectionType.Text == "COM";
        ports.Visible = baudRates.Visible = com;
        tcpPort.Visible = connectionType.Text == "TCP";
        connectionHint.Text = local ? "Печать G-code без внешней программы" : com ? "8N1 · физический или виртуальный порт" : "127.0.0.1 · порт для управляющей программы";
        foreach (var control in localControls) control.Enabled = session?.IsConnected == true && !busy && !changingPort && !ExternalConnected;
        bridgeButton.Text = running ? (local ? "Остановить" : "Отключить") : local ? "Запустить" : "Подключить";
        bridgeButton.Enabled = !changingPort && !busy;
        connectionType.Enabled = ports.Enabled = baudRates.Enabled = tcpPort.Enabled = !running && !changingPort && !busy;
        status.Text = tcpConnection is not null ? $"{connectedPort} · ожидание / работа с программой" : bridge is not null ? $"Подключено: {connectedPort} · {baudRates.Text} бод" : session?.IsConnected == true ? "Локальный режим · принтер готов" : "Принтер не запущен";
    }

    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) action(); }); }
    private void Log(string line)
    {
        if (journal.TextLength > 250_000) journal.Clear();
        journal.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        journal.ScrollToCaret();
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Log("Передача отменена. Уже принятые движения остаются в прошивке."); }
        catch (Exception ex) { Log("Ошибка: " + ex.Message); }
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) { e.Cancel = !closeReady; return; }
        e.Cancel = true;
        closing = true;
        settingsTimer.Stop(); SaveSettings(); settingsTimer.Dispose();
        await Guard(StopSimulator);
        closeReady = true;
        BeginInvoke(new Action(Close));
    }
}
