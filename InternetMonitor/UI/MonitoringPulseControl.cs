using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace InternetMonitor.UI;

/// <summary>
/// Small "monitoring is active" indicator: a rounded square that pulses like a heartbeat while
/// active, and sits static grey when monitoring is paused/off. Deliberately a simple 2D GDI+
/// animation rather than a literal 3D cube render.
/// </summary>
public sealed class MonitoringPulseControl : Control
{
    private readonly System.Windows.Forms.Timer _timer;
    private double _phase;
    private bool _isActive = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            Invalidate();
        }
    }

    public MonitoringPulseControl()
    {
        DoubleBuffered = true;
        Size = new Size(18, 18);
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) =>
        {
            if (!_isActive)
            {
                return;
            }

            _phase += 0.03;
            if (_phase > 1)
            {
                _phase -= 1;
            }

            Invalidate();
        };
        _timer.Start();
    }

    // Pausing the animation while the control isn't visible (e.g. its host window is closed or
    // minimized) avoids a continuous ~30Hz repaint cost for no visible benefit.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (!_isActive)
        {
            DrawSquare(e.Graphics, 0.55, Color.Gainsboro);
            return;
        }

        double t = _phase < 0.5 ? _phase * 2 : (1 - _phase) * 2; // triangle wave 0..1..0
        double scale = 0.45 + (0.55 * t);
        int alpha = (int)(110 + (145 * t));
        Color baseColor = Color.FromArgb(64, 133, 219);
        DrawSquare(e.Graphics, scale, Color.FromArgb(alpha, baseColor));
    }

    private void DrawSquare(Graphics g, double scale, Color color)
    {
        int size = (int)(Math.Min(Width, Height) * scale);
        int x = (Width - size) / 2;
        int y = (Height - size) / 2;
        int radius = Math.Max(2, size / 4);

        using var path = RoundedRect(new Rectangle(x, y, size, size), radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
