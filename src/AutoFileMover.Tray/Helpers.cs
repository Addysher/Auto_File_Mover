using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using AutoFileMover.Core;
using Microsoft.Win32;

namespace AutoFileMover.Tray;

/// <summary>
/// The app's artwork, drawn in code so the .ico, the window icon and the coloured status
/// tray icons all come from one design: a rounded tile, a white job folder, and an arrow into it.
/// Regenerate assets\AutoFileMover.ico with: AutoFileMover.exe --export-icon assets\AutoFileMover.ico
/// </summary>
internal static class IconArt
{
    public static readonly Color Brand = Color.FromArgb(29, 95, 196);
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static Icon? _appIcon;

    /// <summary>Multi-size brand icon for windows and dialogs.</summary>
    public static Icon AppIcon => _appIcon ??= new Icon(new MemoryStream(BuildIco(Brand, IcoSizes)));

    public static Bitmap Render(int size, Color tile)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        RectangleF R(float x, float y, float w, float h) => new(x * s, y * s, w * s, h * s);

        // Tile with a gentle top-to-bottom gradient.
        var tileRect = size <= 20 ? R(0.0f, 0.0f, 1.0f, 1.0f) : R(0.03f, 0.03f, 0.94f, 0.94f);
        using (var path = RoundedRect(tileRect, s * 0.22f))
        using (var brush = new LinearGradientBrush(tileRect, Shade(tile, 0.18f), Shade(tile, -0.12f), LinearGradientMode.Vertical))
        {
            g.FillPath(brush, path);
        }

        // Folder: tab + body, white.
        using (var folder = new GraphicsPath())
        {
            folder.AddPath(RoundedRect(R(0.17f, 0.25f, 0.30f, 0.16f), s * 0.045f), false);
            folder.AddPath(RoundedRect(R(0.17f, 0.33f, 0.66f, 0.44f), s * 0.07f), false);
            folder.FillMode = FillMode.Winding;
            g.FillPath(Brushes.White, folder);
        }

        // Arrow into the folder, in the tile's darker shade.
        using (var arrowBrush = new SolidBrush(Shade(tile, -0.10f)))
        {
            g.FillRectangle(arrowBrush, R(0.28f, 0.50f, 0.25f, 0.11f));
            g.FillPolygon(arrowBrush, new[]
            {
                new PointF(0.50f * s, 0.405f * s),
                new PointF(0.73f * s, 0.555f * s),
                new PointF(0.50f * s, 0.705f * s),
            });
        }

        return bmp;
    }

    /// <summary>Creates an icon at one exact pixel size (for the tray).</summary>
    public static Icon CreateIcon(Color tile, int size)
    {
        using var bmp = Render(size, tile);
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Builds a .ico file: 32-bit DIB frames for small sizes, PNG for 256 px (the standard layout).</summary>
    public static byte[] BuildIco(Color tile, int[] sizes)
    {
        var frames = sizes.Select(size =>
        {
            using var bmp = Render(size, tile);
            return size >= 256 ? EncodePng(bmp) : EncodeDib(bmp);
        }).ToList();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1); // icon
        w.Write((ushort)sizes.Length);

        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);   // palette
            w.Write((byte)0);   // reserved
            w.Write((ushort)1); // planes
            w.Write((ushort)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            w.Write(frame);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Preview sheet (all sizes on light and dark backgrounds) — used to eyeball the design.</summary>
    public static Bitmap RenderPreview()
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];
        var colors = new[] { Brand, Color.FromArgb(46, 160, 67), Color.FromArgb(9, 105, 218), Color.FromArgb(207, 34, 46), Color.FromArgb(110, 118, 129) };
        var width = sizes.Sum() + 20 * (sizes.Length + 1);
        var rowsHeight = 256 + 40;
        var trayRow = 24 * 2 + 30;
        var sheet = new Bitmap(width, rowsHeight * 2 + trayRow * 2);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        var backgrounds = new[] { Color.FromArgb(243, 243, 243), Color.FromArgb(32, 32, 32) };
        for (var b = 0; b < 2; b++)
        {
            var top = b * (rowsHeight + trayRow);
            using (var bg = new SolidBrush(backgrounds[b]))
                g.FillRectangle(bg, 0, top, width, rowsHeight + trayRow);

            var x = 20;
            foreach (var size in sizes)
            {
                using var icon = Render(size, Brand);
                g.DrawImage(icon, x, top + 20 + (256 - size));
                x += size + 20;
            }

            // Status colours at tray size, shown 2x so the pixels are visible.
            x = 20;
            foreach (var c in colors)
            {
                using var small = Render(16, c);
                g.DrawImage(small, new Rectangle(x, top + rowsHeight + 10, 32, 32));
                using var mid = Render(24, c);
                g.DrawImage(mid, new Rectangle(x + 44, top + rowsHeight + 2, 48, 48));
                x += 120;
            }
        }

        return sheet;
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] EncodeDib(Bitmap bmp)
    {
        int n = bmp.Width;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);        // BITMAPINFOHEADER
        w.Write(n);
        w.Write(n * 2);     // XOR + AND masks
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);         // BI_RGB
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);

        for (var y = n - 1; y >= 0; y--) // bottom-up BGRA
        {
            for (var x = 0; x < n; x++)
            {
                var p = bmp.GetPixel(x, y);
                w.Write(p.B);
                w.Write(p.G);
                w.Write(p.R);
                w.Write(p.A);
            }
        }

        var maskStride = (n + 31) / 32 * 4; // AND mask: all zero (alpha does the work)
        w.Write(new byte[maskStride * n]);
        w.Flush();
        return ms.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Lighten (amount &gt; 0) or darken (amount &lt; 0) a colour.</summary>
    private static Color Shade(Color c, float amount)
    {
        int Mix(int v) => amount >= 0
            ? (int)Math.Round(v + (255 - v) * amount)
            : (int)Math.Round(v * (1 + amount));
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Status-coloured tray icons, rendered at the system's small-icon size (DPI aware).</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<Color, Icon> Cache = new();

    public static Icon For(EngineState state) => Create(state switch
    {
        EngineState.Idle => Color.FromArgb(46, 160, 67),
        EngineState.Waiting => Color.FromArgb(9, 105, 218),
        // Red = anything that needs attention (a problem, missing settings, another copy running).
        EngineState.Problem or EngineState.NotConfigured or EngineState.Blocked => Color.FromArgb(207, 34, 46),
        _ => Color.FromArgb(110, 118, 129),
    });

    private static Icon Create(Color color)
    {
        if (!Cache.TryGetValue(color, out var icon))
            Cache[color] = icon = IconArt.CreateIcon(color, Math.Max(16, SystemInformation.SmallIconSize.Width));
        return icon;
    }
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
