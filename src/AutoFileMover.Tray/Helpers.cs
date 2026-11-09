using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using AutoFileMover.Core;
using Microsoft.Win32;

namespace AutoFileMover.Tray;

/// <summary>Draws the tray icon at runtime (coloured disc + arrow) so there is no .ico to ship.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<Color, Icon> Cache = new();

    public static Icon For(EngineState state) => Create(state switch
    {
        EngineState.Idle => Color.FromArgb(46, 160, 67),
        EngineState.Waiting => Color.FromArgb(9, 105, 218),
        EngineState.Problem => Color.FromArgb(207, 34, 46),
        EngineState.NotConfigured or EngineState.Blocked => Color.FromArgb(191, 135, 0),
        _ => Color.FromArgb(110, 118, 129),
    });

    private static Icon Create(Color color)
    {
        if (Cache.TryGetValue(color, out var cached))
            return cached;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 1, 1, 30, 30);
            Point[] arrow = [new(7, 13), new(16, 13), new(16, 7), new(26, 16), new(16, 25), new(16, 19), new(7, 19)];
            g.FillPolygon(Brushes.White, arrow);
        }

        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        Cache[color] = icon;
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal static class ServiceControl
{
    /// <summary>Null when the service is not installed.</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts or stops the service through an elevated sc.exe (shows a UAC prompt).</summary>
    public static void RunElevated(string verb)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("sc.exe", $"{verb} {AppPaths.ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt.
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoFileMover";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal static class Shell
{
    public static void Open(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
        }
    }
}
