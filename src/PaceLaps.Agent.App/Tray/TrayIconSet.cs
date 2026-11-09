using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PaceLaps.Agent.Hosting.Tray;
using Application = System.Windows.Application;

namespace PaceLaps.Agent.App.Tray;

/// <summary>
/// The tray icons, drawn at start from the PaceLaps mark (Assets/tray-*.png): a badge in the state's colour (grey,
/// blue, amber, green, red). Sleeping fades the mark; a problem's badge carries an exclamation mark, so it doesn't rely
/// on colour alone. The icon handles live as long as the program.
/// </summary>
internal sealed class TrayIconSet : IDisposable
{
    private static readonly int[] MarkSizes = [16, 20, 24, 32, 40, 48];
    private readonly Dictionary<TrayIcon, Icon> _icons = [];

    /// <param name="size">The tray's icon size in pixels (16 at 100 % scaling).</param>
    public TrayIconSet(int size)
    {
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

    public Icon this[TrayIcon kind] => _icons[kind];

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private static Bitmap Mark(int size)
    {
        var pick = MarkSizes.FirstOrDefault(s => s >= size, MarkSizes[^1]);
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/tray-{pick}.png"))!.Stream;
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
            var box = new RectangleF(size - badge, size - badge, badge - 0.5f, badge - 0.5f);
            using var fill = new SolidBrush(color);
            using var edge = new Pen(Color.FromArgb(0x09, 0x09, 0x0B), Math.Max(1f, size / 16f));
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
