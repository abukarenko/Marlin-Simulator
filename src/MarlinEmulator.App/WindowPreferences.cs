using System.Text.Json;

namespace MarlinEmulator.App;

internal sealed class WindowPreferences
{
    public string Transport { get; set; } = "COM";
    public string Port { get; set; } = "";
    public string Baud { get; set; } = "115200";
    public int TcpPort { get; set; } = 7000;
    public int Width { get; set; } = 1060;
    public int Height { get; set; } = 680;
    public int? Left { get; set; }
    public int? Top { get; set; }
    public bool Maximized { get; set; }
    private static string PathName => Path.Combine(AppContext.BaseDirectory, "data", "ui-settings.json");
    public static WindowPreferences Load()
    {
        try { return JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(PathName)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(PathName + ".tmp", PathName, true);
    }
}
