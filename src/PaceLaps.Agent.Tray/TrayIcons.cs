using System.Drawing.Drawing2D;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The tray icons, drawn at start (no image files): a filled circle per state. Their five icon handles live as long as
/// the program.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayIcon, Icon> _icons = [];

    public TrayIcons()
    {
        foreach (var (kind, color) in new[]
                 {
                     (TrayIcon.Sleeping, Color.FromArgb(0x8A, 0x8F, 0x98)),
                     (TrayIcon.Busy, Color.FromArgb(0x2F, 0x7F, 0xE0)),
                     (TrayIcon.Ready, Color.FromArgb(0xE0, 0xA1, 0x1B)),
                     (TrayIcon.Active, Color.FromArgb(0x2E, 0xA0, 0x4F)),
                     (TrayIcon.Problem, Color.FromArgb(0xD6, 0x33, 0x33)),
                 })
        {
            _icons[kind] = Draw(color);
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

    private static Icon Draw(Color color)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            using var ring = new Pen(Color.FromArgb(0x30, 0x30, 0x30), Math.Max(1, size / 16f));
            var pad = size / 8f;
            g.FillEllipse(fill, pad, pad, size - 2 * pad, size - 2 * pad);
            g.DrawEllipse(ring, pad, pad, size - 2 * pad, size - 2 * pad);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
