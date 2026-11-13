using System.Globalization;

namespace MarlinEmulator.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        ApplicationConfiguration.Initialize();
        using var window = new MainForm(!args.Contains("--smoke-ui"));
        if (args.Contains("--smoke-ui"))
            window.Shown += (_, _) => window.BeginInvoke(new Action(window.Close));
        Application.Run(window);
    }
}
