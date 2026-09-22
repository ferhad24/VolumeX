using System.Windows;
using System.Windows.Media;

namespace Crescendo.Controls;

public enum MeterOrientation { Vertical, Horizontal }

/// <summary>
/// A peak level bar with a held peak marker.
/// </summary>
/// <remarks>
/// The incoming value is already on a dBFS-derived scale (see
/// <c>MainViewModel.ToMeterScale</c>); this control only draws it. The peak hold
/// falls at a fixed rate rather than decaying proportionally, which is what makes
/// a brief transient stay readable long enough to notice.
/// </remarks>
public sealed class LevelMeter : FrameworkElement
{
    private double _heldPeak;
    private DateTime _heldSince = DateTime.MinValue;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(MeterOrientation), typeof(LevelMeter),
        new FrameworkPropertyMetadata(MeterOrientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowPeakHoldProperty = DependencyProperty.Register(
        nameof(ShowPeakHold), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public MeterOrientation Orientation
    {
        get => (MeterOrientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush PeakBrush
    {
        get => (Brush)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool ShowPeakHold
    {
        get => (bool)GetValue(ShowPeakHoldProperty);
        set => SetValue(ShowPeakHoldProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (LevelMeter)d;
        double value = Math.Clamp((double)e.NewValue, 0, 1);

        if (value >= meter._heldPeak)
        {
            meter._heldPeak = value;
            meter._heldSince = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - meter._heldSince) > TimeSpan.FromMilliseconds(900))
        {
            // Roughly 20 dB per second once the hold expires.
            meter._heldPeak = Math.Max(value, meter._heldPeak - 0.012);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double value = Math.Clamp(Value, 0, 1);
        double radius = CornerRadius;

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        if (value > 0.001)
        {
            Rect fill = Orientation == MeterOrientation.Vertical
                ? new Rect(0, height * (1 - value), width, height * value)
                : new Rect(0, 0, width * value, height);

            // Clip to the rounded track so the fill never squares off the corners.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
            dc.DrawRectangle(FillBrush, null, fill);
            dc.Pop();
        }

        if (ShowPeakHold && _heldPeak > 0.02)
        {
            const double markerThickness = 2.0;
            Rect marker = Orientation == MeterOrientation.Vertical
                ? new Rect(0, Math.Max(0, height * (1 - _heldPeak) - markerThickness / 2), width, markerThickness)
                : new Rect(Math.Min(width - markerThickness, width * _heldPeak), 0, markerThickness, height);

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
            dc.DrawRectangle(PeakBrush, null, marker);
            dc.Pop();
        }
    }
}
