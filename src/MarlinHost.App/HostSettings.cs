using System.Text.Json;

namespace MarlinHost;

public sealed class HostSettings
{
    public string Transport { get; set; } = "TCP";
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7000;
    public string Com { get; set; } = "";
    public int Baud { get; set; } = 115200;
    public int FanIndex { get; set; }
    public string Slicer { get; set; } = "";
    public bool AutoStandby { get; set; }
    public int StandbyNozzle { get; set; } = 100;
    public int StandbyBed { get; set; } = 50;
    public double Yaw { get; set; } = -0.8;
    public double Pitch { get; set; } = 0.8;
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public bool Travel { get; set; } = true;
    public bool Extrusion { get; set; } = true;
    public float LineWidth { get; set; } = 2;
    public string[] ScriptNames { get; set; } = ["Домой", "Отключить моторы", "Сигнал"];
    public string[] Scripts { get; set; } = ["G28", "M84", "M300 S880 P200"];
    public static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    public static HostSettings Load()
    {
        try { return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(Path.Combine(Data, "host-settings.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Data);
        string path = Path.Combine(Data, "host-settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
