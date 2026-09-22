using System.Collections;
using System.Windows;
using System.Windows.Media;
using Crescendo.ViewModels;

namespace Crescendo.Controls;

/// <summary>
/// Draws the combined frequency response of the tone shelves and the ten EQ
/// bands.
/// </summary>
/// <remarks>
/// This is not a decorative spline through the slider tops. It evaluates the
/// same RBJ biquad transfer functions the APO runs, at 48 kHz, so the curve
/// shows what the filters genuinely do — including how neighbouring bands add
/// where their skirts overlap, which a naive interpolation hides.
/// </remarks>
public sealed class EqCurve : FrameworkElement
{
    private const double MinFrequency = 20.0;
    private const double MaxFrequency = 20000.0;
    private const double RangeDb = 15.0;
    private const double SampleRate = 48000.0;

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(
        nameof(Bands), typeof(IEnumerable), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bumped by the view model whenever a gain changes, to force a redraw.</summary>
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(EqCurve),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BassDbProperty = DependencyProperty.Register(
        nameof(BassDb), typeof(double), typeof(EqCurve),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrebleDbProperty = DependencyProperty.Register(
        nameof(TrebleDb), typeof(double), typeof(EqCurve),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurveBrushProperty = DependencyProperty.Register(
        nameof(CurveBrush), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Bands
    {
        get => (IEnumerable?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public double BassDb
    {
        get => (double)GetValue(BassDbProperty);
        set => SetValue(BassDbProperty, value);
    }

    public double TrebleDb
    {
        get => (double)GetValue(TrebleDbProperty);
        set => SetValue(TrebleDbProperty, value);
    }

    public Brush CurveBrush
    {
        get => (Brush)GetValue(CurveBrushProperty);
        set => SetValue(CurveBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        DrawGrid(dc, width, height);

        // A flat EQ has no filters, but the flat line is still the answer and
        // an empty panel reads as broken.
        List<Filter> filters = BuildFilters();

        const int steps = 220;
        var points = new Point[steps + 1];

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            double frequency = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, t);

            double totalDb = 0;
            foreach (Filter filter in filters)
                totalDb += filter.ResponseDb(frequency);

            double y = height / 2 - (totalDb / RangeDb) * (height / 2 - 6);
            points[i] = new Point(t * width, Math.Clamp(y, 2, height - 2));
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();

        if (FillBrush is not null)
        {
            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(0, height / 2), true, true);
                context.PolyLineTo(points, false, false);
                context.LineTo(new Point(width, height / 2), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(FillBrush, null, area);
        }

        var pen = new Pen(CurveBrush, 2.0)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private void DrawGrid(DrawingContext dc, double width, double height)
    {
        var pen = new Pen(GridBrush, 1.0);
        pen.Freeze();

        // 0 dB line, drawn slightly stronger than the decade marks.
        var zeroPen = new Pen(GridBrush, 1.0) { DashStyle = new DashStyle([4, 4], 0) };
        zeroPen.Freeze();
        dc.DrawLine(zeroPen, new Point(0, height / 2), new Point(width, height / 2));

        foreach (double frequency in new[] { 100.0, 1000.0, 10000.0 })
        {
            double t = Math.Log(frequency / MinFrequency) / Math.Log(MaxFrequency / MinFrequency);
            double x = t * width;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
    }

    private List<Filter> BuildFilters()
    {
        var filters = new List<Filter>();

        if (Math.Abs(BassDb) > 0.01)
            filters.Add(Filter.LowShelf(100, 0.707, BassDb));

        if (Math.Abs(TrebleDb) > 0.01)
            filters.Add(Filter.HighShelf(8000, 0.707, TrebleDb));

        if (Bands is not null)
        {
            foreach (object? item in Bands)
            {
                if (item is not BandViewModel band) continue;
                if (Math.Abs(band.GainDb) < 0.01) continue;
                filters.Add(Filter.Peaking(band.FrequencyHz, 1.41, band.GainDb));
            }
        }

        return filters;
    }

    /// <summary>
    /// A single biquad, evaluated on the unit circle to get its magnitude
    /// response. Mirrors the coefficient maths in <c>dsp/Biquad.h</c>.
    /// </summary>
    private readonly struct Filter(double b0, double b1, double b2, double a1, double a2)
    {
        private readonly double _b0 = b0, _b1 = b1, _b2 = b2, _a1 = a1, _a2 = a2;

        public double ResponseDb(double frequency)
        {
            double w = 2.0 * Math.PI * frequency / SampleRate;
            double cos1 = Math.Cos(-w), sin1 = Math.Sin(-w);
            double cos2 = Math.Cos(-2 * w), sin2 = Math.Sin(-2 * w);

            double numeratorReal = _b0 + _b1 * cos1 + _b2 * cos2;
            double numeratorImaginary = _b1 * sin1 + _b2 * sin2;
            double denominatorReal = 1.0 + _a1 * cos1 + _a2 * cos2;
            double denominatorImaginary = _a1 * sin1 + _a2 * sin2;

            double numerator = Math.Sqrt(numeratorReal * numeratorReal + numeratorImaginary * numeratorImaginary);
            double denominator = Math.Sqrt(denominatorReal * denominatorReal + denominatorImaginary * denominatorImaginary);

            if (denominator < 1e-12) return 0;
            return 20.0 * Math.Log10(Math.Max(numerator / denominator, 1e-12));
        }

        public static Filter Peaking(double frequency, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w = 2 * Math.PI * frequency / SampleRate;
            double alpha = Math.Sin(w) / (2 * q);
            double cos = Math.Cos(w);

            double a0 = 1 + alpha / a;
            return new Filter(
                (1 + alpha * a) / a0,
                (-2 * cos) / a0,
                (1 - alpha * a) / a0,
                (-2 * cos) / a0,
                (1 - alpha / a) / a0);
        }

        public static Filter LowShelf(double frequency, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w = 2 * Math.PI * frequency / SampleRate;
            double cos = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt((a + 1 / a) * (1 / q - 1) + 2);
            double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1) + (a - 1) * cos + twoSqrtAAlpha;
            return new Filter(
                (a * ((a + 1) - (a - 1) * cos + twoSqrtAAlpha)) / a0,
                (2 * a * ((a - 1) - (a + 1) * cos)) / a0,
                (a * ((a + 1) - (a - 1) * cos - twoSqrtAAlpha)) / a0,
                (-2 * ((a - 1) + (a + 1) * cos)) / a0,
                ((a + 1) + (a - 1) * cos - twoSqrtAAlpha) / a0);
        }

        public static Filter HighShelf(double frequency, double q, double gainDb)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w = 2 * Math.PI * frequency / SampleRate;
            double cos = Math.Cos(w);
            double alpha = Math.Sin(w) / 2 * Math.Sqrt((a + 1 / a) * (1 / q - 1) + 2);
            double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1) - (a - 1) * cos + twoSqrtAAlpha;
            return new Filter(
                (a * ((a + 1) + (a - 1) * cos + twoSqrtAAlpha)) / a0,
                (-2 * a * ((a - 1) + (a + 1) * cos)) / a0,
                (a * ((a + 1) + (a - 1) * cos - twoSqrtAAlpha)) / a0,
                (2 * ((a - 1) - (a + 1) * cos)) / a0,
                ((a + 1) - (a - 1) * cos - twoSqrtAAlpha) / a0);
        }
    }
}
