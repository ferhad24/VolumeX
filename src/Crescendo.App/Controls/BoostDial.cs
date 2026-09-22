using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Crescendo.Controls;

/// <summary>
/// The circular boost control.
/// </summary>
/// <remarks>
/// Drawn directly rather than composed from shapes: the arc, ticks, thumb and
/// readout all derive from one value, and rendering them together keeps the
/// geometry exact at any DPI without a stack of transforms to keep in sync.
///
/// Dragging is vertical rather than rotational. Chasing a knob around its centre
/// is imprecise with a mouse; pulling up and down maps one axis to one value and
/// is what every audio tool that gets this right does.
/// </remarks>
public sealed class BoostDial : FrameworkElement
{
    private const double StartAngle = 135.0;   // degrees, clockwise from +x
    private const double SweepAngle = 270.0;

    private Point _dragOrigin;
    private double _dragStartValue;
    private bool _dragging;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BoostDial),
        new FrameworkPropertyMetadata(100.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(BoostDial),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(BoostDial),
        new FrameworkPropertyMetadata(500.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BoostDial),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(BoostDial),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BoostDial),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(BoostDial),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(BoostDial),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubtextBrushProperty = DependencyProperty.Register(
        nameof(SubtextBrush), typeof(Brush), typeof(BoostDial),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush SubtextBrush
    {
        get => (Brush)GetValue(SubtextBrushProperty);
        set => SetValue(SubtextBrushProperty, value);
    }

    public BoostDial()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var dial = (BoostDial)d;
        double value = (double)baseValue;
        return Math.Clamp(value, dial.Minimum, dial.Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BoostDial)d).InvalidateVisual();

    // ---------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _dragOrigin = e.GetPosition(this);
        _dragStartValue = Value;
        CaptureMouse();
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        Point position = e.GetPosition(this);
        double travel = _dragOrigin.Y - position.Y;

        // 240 px of travel covers the whole range; holding Shift quarters that
        // for fine adjustment, which matters when 5% is audible.
        double pixelsForFullRange = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 960 : 240;
        double delta = travel / pixelsForFullRange * (Maximum - Minimum);

        Value = Math.Round(_dragStartValue + delta);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 25;
        Value = Math.Round((Value + Math.Sign(e.Delta) * step) / step) * step;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 25;

        switch (e.Key)
        {
            case Key.Up or Key.Right: Value += step; e.Handled = true; break;
            case Key.Down or Key.Left: Value -= step; e.Handled = true; break;
            case Key.Home: Value = Minimum; e.Handled = true; break;
            case Key.End: Value = Maximum; e.Handled = true; break;
        }
    }

    // ---------------------------------------------------------------- render

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double thickness = Math.Max(10, size * 0.075);
        double radius = size / 2 - thickness / 2 - 8;
        if (radius <= 0) return;

        double fraction = (Maximum - Minimum) <= 0 ? 0 : (Value - Minimum) / (Maximum - Minimum);
        fraction = Math.Clamp(fraction, 0, 1);

        // A transparent hit area, otherwise only the painted pixels take input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        DrawTicks(dc, centre, radius + thickness / 2 + 6);

        var trackPen = new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, BuildArc(centre, radius, StartAngle, SweepAngle));

        if (fraction > 0.001)
        {
            Brush arcBrush = ArcBrush;
            if (!IsActive)
            {
                // Inactive still shows the value, just without the accent shouting.
                arcBrush = TrackBrush;
            }

            var valuePen = new Pen(arcBrush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, valuePen, BuildArc(centre, radius, StartAngle, SweepAngle * fraction));

            DrawThumb(dc, centre, radius, fraction, thickness, arcBrush);
        }

        DrawReadout(dc, centre, size);
    }

    private void DrawTicks(DrawingContext dc, Point centre, double radius)
    {
        var pen = new Pen(SubtextBrush, 1.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        // One tick per 100%: 100, 200, 300, 400, 500.
        const int divisions = 4;
        for (int i = 0; i <= divisions; i++)
        {
            double angle = StartAngle + SweepAngle * i / divisions;
            Point inner = PolarToPoint(centre, radius, angle);
            Point outer = PolarToPoint(centre, radius + 6, angle);
            dc.DrawLine(pen, inner, outer);
        }
    }

    private void DrawThumb(DrawingContext dc, Point centre, double radius, double fraction,
        double thickness, Brush brush)
    {
        double angle = StartAngle + SweepAngle * fraction;
        Point position = PolarToPoint(centre, radius, angle);

        // A soft halo keeps the thumb legible where it overlaps the filled arc.
        var glow = new RadialGradientBrush(
            Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
        dc.DrawEllipse(glow, null, position, thickness * 1.1, thickness * 1.1);
        dc.DrawEllipse(brush, null, position, thickness * 0.34, thickness * 0.34);
    }

    private void DrawReadout(DrawingContext dc, Point centre, double size)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typefaceValue = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceCaption = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var value = new FormattedText(
            $"{Value:0}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typefaceValue, size * 0.20, TextBrush, dpi);

        var caption = new FormattedText(
            Caption ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typefaceCaption, size * 0.058, SubtextBrush, dpi);

        double totalHeight = value.Height + caption.Height + 2;
        double top = centre.Y - totalHeight / 2;

        dc.DrawText(value, new Point(centre.X - value.Width / 2, top));
        dc.DrawText(caption, new Point(centre.X - caption.Width / 2, top + value.Height + 2));
    }

    private static Geometry BuildArc(Point centre, double radius, double startAngle, double sweep)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            Point start = PolarToPoint(centre, radius, startAngle);
            Point end = PolarToPoint(centre, radius, startAngle + sweep);

            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0,
                sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Angles are measured clockwise from the positive x-axis, so 135 degrees is
    /// the lower-left of the dial and the sweep ends at its lower-right.
    /// </summary>
    private static Point PolarToPoint(Point centre, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new Point(
            centre.X + radius * Math.Cos(radians),
            centre.Y + radius * Math.Sin(radians));
    }
}
