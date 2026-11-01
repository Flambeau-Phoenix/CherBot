using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CherBot.Desktop;

public enum EngineHealth { Unknown, Healthy, Degraded, Unreachable }

/// <summary>CherBot logo with a health badge: green = running and connected, amber = an integration is down, red = engine unreachable.</summary>
internal static class TrayIcons
{
    public static Icon For(EngineHealth health) => health switch
    {
        EngineHealth.Healthy => Make(Color.FromArgb(63, 191, 127)),
        EngineHealth.Degraded => Make(Color.FromArgb(229, 169, 59)),
        EngineHealth.Unreachable => Make(Color.FromArgb(229, 83, 75)),
        _ => Make(Color.FromArgb(150, 150, 160)),
    };

    private static Icon Make(Color status)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            using var logoStream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/cherbot.png")).Stream;
            using var logo = new Bitmap(logoStream);
            g.DrawImage(logo, 0, 0, 32, 32);
            using var dot = new SolidBrush(status);
            using var ring = new Pen(Color.FromArgb(18, 19, 24), 2f);
            g.FillEllipse(dot, 18, 18, 13, 13);
            g.DrawEllipse(ring, 18, 18, 13, 13);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
