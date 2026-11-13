using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace MarlinHost;

internal sealed class NativePreview : Panel
{
    private Process? process;
    private PrintJob? job;
    private Vector3? head;
    private bool follow;
    private readonly Label notice = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.LightGray, Text = "Запуск 3D-просмотрщика…" };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PrintJob? Job { get => job; set { job = value; if (IsHandleCreated) LoadScene(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Progress { get => progress; set { if(progress==value) return; progress=value; Send("PROGRESS "+value); } }
    private int progress;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Vector3? Head { get => head; set { head = value; if (value is Vector3 p) Send($"HEAD {GCode.N(p.X)} {GCode.N(p.Y)} {GCode.N(p.Z)}"); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double BedX { get; set; } = 220;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double BedY { get; set; } = 220;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Follow { get => follow; set { follow = value; Send(value ? "FOLLOW 1" : "FOLLOW 0"); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SceneCount => Volatile.Read(ref sceneCount);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RendererStats => Volatile.Read(ref rendererStats);
    private string rendererStats="";
    public NativePreview() { Dock = DockStyle.Fill; BackColor = Color.FromArgb(115, 142, 154); Controls.Add(notice); }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        string exe = Path.Combine(AppContext.BaseDirectory, "viewer", "MarlinViewer.exe");
        if (!File.Exists(exe)) exe = Path.GetFullPath("dist/host/viewer/MarlinViewer.exe");
        if (!File.Exists(exe)) { notice.Text = "Не найден 3D-просмотрщик. Выполните сборку Marlin Host."; return; }
        string data = Path.Combine(HostSettings.Data, "viewer"); Directory.CreateDirectory(data);
        var info = new ProcessStartInfo(exe) { WorkingDirectory = data, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--host-viewer"); info.ArgumentList.Add(Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.Environment["MARLIN_VIEWER_EMBEDDED"]="1";
        try
        {
            process = Process.Start(info) ?? throw new IOException("Не удалось запустить 3D-просмотрщик.");
            process.StandardInput.AutoFlush = true;
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data?.StartsWith("VIEWER_SCENE") == true) Interlocked.Increment(ref sceneCount);
                if (args.Data?.StartsWith("VIEWER_STATS") == true) Volatile.Write(ref rendererStats,args.Data);
            };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            notice.Visible = false; LoadScene();
        }
        catch (Exception ex) { notice.Text = ex.Message; notice.Visible = true; }
    }
    private int sceneCount;
    public void Send(string command)
    {
        try { if (process is { HasExited: false }) process.StandardInput.WriteLine(command); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { notice.Visible = true; notice.Text = "3D-просмотрщик остановлен: " + ex.Message; }
    }
    private void LoadScene()
    {
        if (job is null || process is not { HasExited: false }) return;
        string folder = Path.Combine(HostSettings.Data, "viewer"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".scene");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write((uint)job.Segments.Count);
            writer.Write(job.FilamentColor.R/255f); writer.Write(job.FilamentColor.G/255f); writer.Write(job.FilamentColor.B/255f);
            foreach (var segment in job.Segments)
            {
                writer.Write(segment.From.X); writer.Write(segment.From.Y); writer.Write(segment.From.Z);
                writer.Write(segment.To.X); writer.Write(segment.To.Y); writer.Write(segment.To.Z);
                writer.Write((byte)((segment.Extruding ? 1 : 0) | (segment.Outline ? 2 : 0)));
                writer.Write(segment.Command);
            }
        }
        Send("SCENE " + path);
        Send("PROGRESS "+progress);
        if (head is Vector3 p) Head = p;
    }
    public void Reset() => Send("RESET");
    public void View(string view) => Send(view switch { "Сверху" => "TOP", "Спереди" => "FRONT", "Справа" => "RIGHT", _ => "RESET" });
    public void ClearImage() => Send("CLEAR");
    public void RestoreImage() => LoadScene();
    public void Snapshot(string path) => Send("SHOT " + path);
    protected override void Dispose(bool disposing)
    {
        if (disposing && process is not null)
        {
            try { Send("QUIT"); if (!process.WaitForExit(2500)) process.Kill(true); } catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
        base.Dispose(disposing);
    }
}
