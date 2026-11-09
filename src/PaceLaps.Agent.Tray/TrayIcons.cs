using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The program's icon (`Assets/pacelaps-agent.ico`, the PaceLaps mark as on pacelaps.com) and the tray icons drawn
/// from it at start: the mark with a badge in the state's colour (grey, blue, amber, green, red). Sleeping fades the
/// mark; a problem's badge carries an exclamation mark, so it doesn't rely on colour alone. The icon handles live as
/// long as the program.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private static readonly int[] MarkSizes = [16, 20, 24, 32, 40, 48];
    private readonly Dictionary<TrayIcon, Icon> _icons = [];

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var mark = Mark(size);
        foreach (var (kind, color) in new[]
                 {
                     (TrayIcon.Sleeping, Color.FromArgb(0x8A, 0x8F, 0x98)),
                     (TrayIcon.Busy, Color.FromArgb(0x2F, 0x7F, 0xE0)),
                     (TrayIcon.Ready, Color.FromArgb(0xE0, 0xA1, 0x1B)),
                     (TrayIcon.Active, Color.FromArgb(0x2E, 0xA0, 0x4F)),
                     (TrayIcon.Problem, Color.FromArgb(0xD6, 0x33, 0x33)),
                 })
        {
            _icons[kind] = Draw(mark, size, color, faded: kind == TrayIcon.Sleeping, exclamation: kind == TrayIcon.Problem);
        }
    }

    /// <summary>The program's icon for every window's title bar and the taskbar.</summary>
    public static Icon Program { get; } = LoadProgramIcon();

    public Icon this[TrayIcon kind] => _icons[kind];

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private static Icon LoadProgramIcon()
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("PaceLaps.Agent.Tray.Assets.pacelaps-agent.ico")
            ?? throw new InvalidOperationException("The program icon is missing from the build.");
        return new Icon(stream);
    }

    /// <summary>The mark drawn for this size: the nearest rendered size at or above it, scaled down if needed.</summary>
    private static Bitmap Mark(int size)
    {
        var pick = MarkSizes.FirstOrDefault(s => s >= size, MarkSizes[^1]);
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream($"PaceLaps.Agent.Tray.Assets.tray-{pick}.png")
            ?? throw new InvalidOperationException("A tray image is missing from the build.");
        using var source = new Bitmap(stream);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, size, size);
        return bitmap;
    }

    private static Icon Draw(Bitmap mark, int size, Color color, bool faded, bool exclamation)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (faded)
            {
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f });
                g.DrawImage(mark, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                g.DrawImage(mark, 0, 0, size, size);
            }

            // The badge in the bottom right corner, with a dark ring so it reads on light and dark taskbars.
            var badge = MathF.Round(size * 0.5f);
            var ring = Math.Max(1f, size / 16f);
            var box = new RectangleF(size - badge, size - badge, badge - 0.5f, badge - 0.5f);
            using var fill = new SolidBrush(color);
            using var edge = new Pen(Color.FromArgb(0x09, 0x09, 0x0B), ring);
            g.FillEllipse(fill, box);
            g.DrawEllipse(edge, box);
            if (exclamation)
            {
                using var white = new SolidBrush(Color.White);
                var cx = box.X + box.Width / 2;
                var w = Math.Max(1.5f, badge / 6f);
                g.FillRectangle(white, cx - w / 2, box.Y + badge * 0.2f, w, badge * 0.38f);
                g.FillRectangle(white, cx - w / 2, box.Y + badge * 0.66f, w, w);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
