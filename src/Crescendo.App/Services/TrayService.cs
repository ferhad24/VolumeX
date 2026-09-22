using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Crescendo.ViewModels;

namespace Crescendo.Services;

/// <summary>
/// Notification-area icon and menu.
/// </summary>
/// <remarks>
/// WPF has no tray API, so this is the one place WinForms is used. The icon is
/// composed at runtime from the embedded mark so it can carry state: a
/// multiplier badge while boosting, desaturated when the engine is off.
/// </remarks>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly ToolStripMenuItem _toggleItem;
    private Icon? _currentIcon;
    private int _lastRenderedPercent = -1;
    private bool _lastRenderedEnabled;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayService(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        _toggleItem = new ToolStripMenuItem("Boost on/off", null, (_, _) => _viewModel.ToggleEngineCommand.Execute(null));

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("Open VolumeX", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("100%", null, (_, _) => SetBoost(100)));
        menu.Items.Add(new ToolStripMenuItem("200%", null, (_, _) => SetBoost(200)));
        menu.Items.Add(new ToolStripMenuItem("300%", null, (_, _) => SetBoost(300)));
        menu.Items.Add(new ToolStripMenuItem("500%", null, (_, _) => SetBoost(500)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "VolumeX"
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        // Clicking a notification (e.g. "update available") opens the window.
        _icon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke();

        Refresh();
    }

    private void SetBoost(int percent)
    {
        _viewModel.BoostPercent = percent;
        if (!_viewModel.EngineEnabled) _viewModel.EngineEnabled = true;
        Refresh();
    }

    /// <summary>Redraws the icon and tooltip if anything visible changed.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        int percent = (int)_viewModel.BoostPercent;
        bool enabled = _viewModel.EngineEnabled;

        _toggleItem.Text = enabled ? "Turn boost off" : "Turn boost on";
        _icon.Text = enabled
            ? $"VolumeX — {percent}%"
            : "VolumeX — off";

        if (percent == _lastRenderedPercent && enabled == _lastRenderedEnabled) return;
        _lastRenderedPercent = percent;
        _lastRenderedEnabled = enabled;

        Icon fresh = RenderIcon(percent, enabled);
        _icon.Icon = fresh;

        // Only destroy the previous handle after the new one is in place,
        // otherwise the shell briefly has nothing to draw.
        _currentIcon?.Dispose();
        _currentIcon = fresh;
    }

    /// <summary>
    /// Builds the tray image: the Crescendo mark, with a small multiplier badge
    /// once the boost is above 100%.
    /// </summary>
    /// <remarks>
    /// The mark carries the brand, the badge carries the state. Greying the mark
    /// when the engine is off is clearer at 16 px than any glyph change would be.
    /// </remarks>
    private static Icon RenderIcon(int percent, bool enabled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            Image mark = MarkImage;
            if (enabled)
            {
                g.DrawImage(mark, new Rectangle(0, 0, 32, 32));
            }
            else
            {
                // Luminance-weighted desaturation, then dimmed: an "off" icon has
                // to be obviously inactive without becoming invisible.
                var matrix = new ColorMatrix(
                [
                    [0.30f, 0.30f, 0.30f, 0, 0],
                    [0.59f, 0.59f, 0.59f, 0, 0],
                    [0.11f, 0.11f, 0.11f, 0, 0],
                    [0, 0, 0, 0.55f, 0],
                    [0, 0, 0, 0, 1]
                ]);
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(matrix);
                g.DrawImage(mark, new Rectangle(0, 0, 32, 32),
                    0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
            }

            if (enabled && percent > 100)
            {
                string label = percent >= 1000 ? "9+" : $"{percent / 100}x";
                using var badge = new SolidBrush(Color.FromArgb(240, 34, 211, 238));
                g.FillEllipse(badge, new Rectangle(14, 14, 18, 18));

                using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.FromArgb(6, 19, 26));
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(label, font, textBrush, new RectangleF(14, 14, 18, 18), format);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Image? _markImage;

    /// <summary>
    /// The mark, loaded once from the embedded resource.
    /// </summary>
    private static Image MarkImage
    {
        get
        {
            if (_markImage is not null) return _markImage;

            try
            {
                var uri = new Uri("pack://application:,,,/crescendo.png", UriKind.Absolute);
                System.Windows.Resources.StreamResourceInfo info =
                    System.Windows.Application.GetResourceStream(uri);
                _markImage = Image.FromStream(info.Stream);
            }
            catch (Exception)
            {
                // A missing resource must not take the tray icon down with it.
                var fallback = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(fallback))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var brush = new SolidBrush(Color.FromArgb(34, 211, 238));
                    g.FillEllipse(brush, 1, 1, 30, 30);
                }
                _markImage = fallback;
            }

            return _markImage;
        }
    }

    public void ShowMessage(string title, string body)
    {
        if (_disposed) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
