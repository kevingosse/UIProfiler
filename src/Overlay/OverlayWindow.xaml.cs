using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Events;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using RectangleGeometry = LiveChartsCore.SkiaSharpView.Drawing.Geometries.RectangleGeometry;

namespace UiProfiler.Overlay;

public partial class OverlayWindow
{
    private const int FreezeThreshold = 100;
    private const int BarWidth = 8;

    private Storyboard? _storyboardInProgress;
    private const int DangerThreshold = 100;
    private readonly ObservableCollection<ObservablePoint> _values = [];
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();
    private readonly Stopwatch _totalStopwatch = new();
    private long _total;
    private readonly List<long> _freezeDurations = new();

    public OverlayWindow()
    {
        DataContext = this;
        InitializeChart();
        InitializeComponent();

        var axis = (Axis)Y[0];
        axis.CustomSeparators = [0, 50, 100, 150];
        axis.LabelsAlignment = LiveChartsCore.Drawing.Align.End;
        var paint = new SolidColorPaint(SKColors.LightGray)
        {
            SKTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold),
            ZIndex = 10
        };

        axis.LabelsPaint = paint;
        axis.Position = LiveChartsCore.Measure.AxisPosition.End;
        axis.InLineNamePlacement = false;
        axis.Padding = new(-50, 20, 5, 0);

        TextThreshold.Text = $"Total UI freezes > {FreezeThreshold} ms: ";

        var caption = Environment.GetEnvironmentVariable("PROFILER_OVERLAY_CAPTION");
        if (!string.IsNullOrEmpty(caption))
        {
            TextCaption.Text = caption;
        }

        Chart.SizeChanged += Chart_SizeChanged;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };

        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var elapsed = _stopwatch.ElapsedMilliseconds;

        if (_stopwatch.IsRunning)
        {
            _stopwatch.Restart();
        }
        else
        {
            _stopwatch.Reset();
        }

        TextTotalTime.Text = _total.ToString();
        TextLast.Text = _freezeDurations.LastOrDefault().ToString();

        for (int i = _values.Count - 1; i >= 0; i--)
        {
            if (_values[i].X == 0)
            {
                _values.RemoveAt(i);
            }
            else
            {
                _values[i].X -= 1;
            }
        }

        _values.Add(new(X[0].MaxLimit, elapsed));
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var maxElements = width / BarWidth;
        var oldMaxLimit = X[0].MaxLimit;
        var offset = X[0].MaxLimit - oldMaxLimit;

        foreach (var value in _values)
        {
            value.X += offset;
        }

        X[0].MaxLimit = maxElements;
    }

    public ICartesianAxis[] X { get; set; } = [
        new Axis { MinLimit = 0, MaxLimit = 1, LabelsPaint = null, ShowSeparatorLines = false }
    ];

    public ICartesianAxis[] Y { get; set; } = [
        new Axis { MinLimit = 0, MaxLimit = 150, ShowSeparatorLines = true }
    ];

    public ISeries[]? Series { get; set; }

    public RectangularSection[] Sections { get; set; } = [
        new()
        {
            LabelSize = 15,
            LabelPaint = new SolidColorPaint(SKColors.Red)
            {
                SKTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            },
            Yj = DangerThreshold,
            Fill = new SolidColorPaint(SKColors.Red.WithAlpha(50))
        }
    ];

    private void InitializeChart()
    {
        var dangerPaint = new SolidColorPaint(SKColors.Red);

        var series = new ColumnSeries<ObservablePoint, RectangleGeometry>
        {
            Values = _values,
            Padding = 0,
            MaxBarWidth = BarWidth
        };

        series
            .OnPointMeasured(point =>
            {
                if (point.Visual is null || point.Model is null)
                {
                    return;
                }

                var isDanger = point.Model.Y > DangerThreshold;

                point.Visual.Fill = isDanger
                    ? dangerPaint
                    : null; // when null, the series fill is used // mark
            });

        Series = [series];
    }

    public void UpdateResponsiveness(bool isResponsive, long elapsedTime)
    {
        if (isResponsive)
        {
            _stopwatch.Stop();
            _totalStopwatch.Stop();

            if (elapsedTime >= FreezeThreshold)
            {
                _total += elapsedTime;
                _freezeDurations.Add(elapsedTime);
            }

            if (_storyboardInProgress != null)
            {
                _storyboardInProgress.Stop();
                _storyboardInProgress = null;
            }
        }
        else
        {
            _stopwatch.Start();
            _totalStopwatch.Restart();
            if (_storyboardInProgress == null)
            {
                _storyboardInProgress = (Storyboard)Resources["ShowStoryboard"]!;
                _storyboardInProgress.Begin();
            }
        }
    }

    public void Show(nint window)
    {
        Show();
        AdjustSize(window);
    }

    public void AdjustSize(nint window)
    {        
        // Get bounding rectangle in device coordinates
        var hr = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT rect,
            Marshal.SizeOf(typeof(NativeMethods.RECT)));

        if (hr != 0)
        {
            return;
        }

        int windowWidthPx = rect.Right - rect.Left;
        int windowHeightPx = rect.Bottom - rect.Top;

        if (windowWidthPx <= 0 || windowHeightPx <= 0)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        NativeMethods.MoveWindow(handle, rect.Left + 1, rect.Top + 1, windowWidthPx, windowHeightPx, false);
        NativeMethods.MoveWindow(handle, rect.Left, rect.Top, windowWidthPx, windowHeightPx, true);
    }
}
