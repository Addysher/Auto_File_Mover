namespace AutoFileMover.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AutoFileMover.Tray", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Auto File Mover is already running — look for its icon in the system tray.",
                "Auto File Mover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        var minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayContext(minimized));
    }
}
