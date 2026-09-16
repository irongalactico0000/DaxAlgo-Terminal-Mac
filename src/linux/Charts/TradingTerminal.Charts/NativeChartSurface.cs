using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TradingTerminal.Charts;

/// <summary>
/// Deterministic, dependency-free Avalonia renderer for the Charts payload. It covers the same four
/// price styles, volume, SMA/EMA, RSI, MACD, crosshair, zoom and pan behavior as the bundled
/// Lightweight Charts page, while remaining native on macOS.
/// </summary>
public sealed class NativeChartSurface : Control
{
    private const double AxisWidth = 66;
    private const double TimeAxisHeight = 22;

    private static readonly IBrush BackgroundBrush = Brush("#0A0A0A");
    private static readonly IBrush TextBrush = Brush("#D1D4DC");
    private static readonly IBrush DimTextBrush = Brush("#787B86");
    private static readonly IBrush UpBrush = Brush("#26A69A");
    private static readonly IBrush DownBrush = Brush("#EF5350");
    private static readonly IBrush UpVolumeBrush = Brush("#6026A69A");
    private static readonly IBrush DownVolumeBrush = Brush("#60EF5350");
    private static readonly IBrush AreaBrush = Brush("#3842A5F5");
    private static readonly IBrush AmberBrush = Brush("#E0A000");
    private static readonly IBrush ObservationSelectionBrush = Brush("#3342A5F5");
    private static readonly IBrush OutcomeSelectionBrush = Brush("#33E0A000");
    private static readonly IPen UpPen = Pen("#26A69A", 1);
    private static readonly IPen DownPen = Pen("#EF5350", 1);
    private static readonly IPen GridPen = Pen("#161616", 1);
    private static readonly IPen BorderPen = Pen("#2A2A2A", 1);
    private static readonly IPen CrosshairPen = Pen("#666666", 1, dash: true);
    private static readonly IPen LinePen = Pen("#42A5F5", 1.5);
    private static readonly IPen SmaPen = Pen("#42A5F5", 1.1);
    private static readonly IPen EmaPen = Pen("#E0A000", 1.1);
    private static readonly IPen BollingerMidPen = Pen("#AB47BC", 1.0);
    private static readonly IPen BollingerBandPen = Pen("#AB47BC", 0.85);
    private static readonly IPen RsiPen = Pen("#AB47BC", 1.1);
    private static readonly IPen MacdPen = Pen("#42A5F5", 1.1);
    private static readonly IPen VwapPen = Pen("#26C6DA", 1.2);
    private static readonly IPen StochKPen = Pen("#42A5F5", 1.1);
    private static readonly IPen StochDPen = Pen("#E0A000", 1.1);
    private static readonly IPen AtrPen = Pen("#66BB6A", 1.1);
    private static readonly IPen AdxPen = Pen("#EF5350", 1.1);
    private static readonly IPen CustomPen = Pen("#90CAF9", 1.0);
    private static readonly IPen SignalPen = Pen("#E0A000", 1.1);
    private static readonly IPen ObservationSelectionPen = Pen("#8042A5F5", 1);
    private static readonly IPen OutcomeSelectionPen = Pen("#80E0A000", 1);
    private static readonly IPen RsiUpperPen = Pen("#80EF5350", 1, dash: true);
    private static readonly IPen RsiLowerPen = Pen("#8026A69A", 1, dash: true);
    private static readonly Typeface Mono = new("Cascadia Mono, SFMono-Regular, Menlo, Consolas, monospace");

    private ChartSnapshot? _snapshot;
    private string _message = "Awaiting chart data…";
    private int _visibleCount;
    private int _rightOffset;
    private Point? _cursor;
    private bool _dragging;
    private Point _dragOrigin;
    private int _dragOffset;
    private bool _selecting;
    private Point _selectionAnchor;
    private Point _selectionCurrent;
    private ChartInteractionMode _interactionMode;
    private ChartTimeRange? _observationRange;
    private ChartTimeRange? _outcomeRange;
    private Rect _lastPricePane;
    private double _lastPriceMin;
    private double _lastPriceMax = 1;
    private double? _draftStopPrice;
    private double? _draftTargetPrice;

    private static readonly IPen DraftStopPen = new Pen(new SolidColorBrush(Color.Parse("#EF5350")), 1.2);
    private static readonly IPen DraftTargetPen = new Pen(new SolidColorBrush(Color.Parse("#26A69A")), 1.2);

    public NativeChartSurface()
    {
        ClipToBounds = true;
        DoubleTapped += (_, _) => FitContent();
    }

    /// <summary>Controls only left-drag. Pan remains the default chart interaction.</summary>
    public ChartInteractionMode InteractionMode
    {
        get => _interactionMode;
        set
        {
            if (_interactionMode == value) return;
            _interactionMode = value;
            _dragging = false;
            _selecting = false;
            InvalidateVisual();
        }
    }

    public ChartTimeRange? ObservationRange
    {
        get => _observationRange;
        set { _observationRange = value; InvalidateVisual(); }
    }

    public ChartTimeRange? OutcomeRange
    {
        get => _outcomeRange;
        set { _outcomeRange = value; InvalidateVisual(); }
    }

    public event EventHandler<ChartRangeSelectedEventArgs>? ResearchRangeSelected;
    public event EventHandler<ChartPriceClickedEventArgs>? PriceClicked;

    public double? DraftStopPrice
    {
        get => _draftStopPrice;
        set { _draftStopPrice = value; InvalidateVisual(); }
    }

    public double? DraftTargetPrice
    {
        get => _draftTargetPrice;
        set { _draftTargetPrice = value; InvalidateVisual(); }
    }

    public ChartSnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            _visibleCount = value?.Candles.Length ?? 0;
            _rightOffset = 0;
            InvalidateVisual();
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            _message = value ?? string.Empty;
            InvalidateVisual();
        }
    }

    /// <summary>Splices a forming candle exactly as Lightweight Charts' <c>series.update</c> does.</summary>
    public void UpdateCandle(ChartCandle candle)
    {
        if (_snapshot is not { } snapshot)
            return;

        var source = snapshot.Candles;
        ChartCandle[] candles;
        if (source.Length > 0 && source[^1].Time == candle.Time)
        {
            candles = (ChartCandle[])source.Clone();
            candles[^1] = candle;
        }
        else
        {
            candles = new ChartCandle[source.Length + 1];
            Array.Copy(source, candles, source.Length);
            candles[^1] = candle;
            if (_visibleCount == source.Length)
                _visibleCount = candles.Length;
        }

        _snapshot = snapshot with { Candles = candles };
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        if (_snapshot is not { Candles.Length: > 0 } snapshot)
        {
            DrawMessage(context, string.IsNullOrWhiteSpace(_message) ? "Awaiting chart data…" : _message);
            return;
        }

        var candles = snapshot.Candles;
        var (start, end) = VisibleRange(candles.Length);
        if (end <= start)
            return;

        var chartWidth = Math.Max(1, Bounds.Width - AxisWidth);
        var contentHeight = Math.Max(1, Bounds.Height - TimeAxisHeight);
        var indicatorCount = (snapshot.Rsi is { Length: > 0 } ? 1 : 0) +
                             (snapshot.StochasticK is { Length: > 0 } ? 1 : 0) +
                             (snapshot.Macd is { Length: > 0 } ? 1 : 0) +
                             (snapshot.Atr is { Length: > 0 } ? 1 : 0) +
                             (snapshot.Adx is { Length: > 0 } ? 1 : 0) +
                             (snapshot.CustomSeries?.Count(static item => item.OscillatorPane && item.Points.Length > 0) ?? 0);
        var priceFraction = indicatorCount switch
        {
            0 => 1d,
            1 => 0.72d,
            2 => 0.58d,
            _ => 0.48d,
        };
        var pricePane = new Rect(0, 0, chartWidth, Math.Max(80, contentHeight * priceFraction));
        var indicatorHeight = indicatorCount == 0 ? 0 : (contentHeight - pricePane.Height) / indicatorCount;
        var nextTop = pricePane.Bottom;

        var (priceMin, priceMax) = PriceRange(snapshot, start, end);
        _lastPricePane = pricePane;
        _lastPriceMin = priceMin;
        _lastPriceMax = priceMax;
        DrawGrid(context, pricePane, priceMin, priceMax, candles, start, end, drawTimeLabels: indicatorCount == 0);
        DrawVolume(context, snapshot, pricePane, start, end);
        DrawPrice(context, snapshot, pricePane, priceMin, priceMax, start, end);
        DrawLineSeries(context, snapshot.Sma, candles, pricePane, priceMin, priceMax, start, end, SmaPen);
        DrawLineSeries(context, snapshot.Ema, candles, pricePane, priceMin, priceMax, start, end, EmaPen);
        DrawLineSeries(context, snapshot.Vwap, candles, pricePane, priceMin, priceMax, start, end, VwapPen);
        DrawLineSeries(context, snapshot.BollingerMid, candles, pricePane, priceMin, priceMax, start, end, BollingerMidPen);
        DrawLineSeries(context, snapshot.BollingerUpper, candles, pricePane, priceMin, priceMax, start, end, BollingerBandPen);
        DrawLineSeries(context, snapshot.BollingerLower, candles, pricePane, priceMin, priceMax, start, end, BollingerBandPen);
        if (snapshot.CustomSeries is { } customs)
        {
            foreach (var custom in customs.Where(static item => !item.OscillatorPane))
                DrawLineSeries(context, custom.Points, candles, pricePane, priceMin, priceMax, start, end, CustomPen);
        }

        Rect? rsiPane = null;
        Rect? macdPane = null;
        var lastPaneBottom = pricePane.Bottom;
        if (snapshot.Rsi is { Length: > 0 })
        {
            rsiPane = new Rect(0, nextTop, chartWidth, indicatorHeight);
            DrawOscillatorPane(context, snapshot.Rsi, null, candles, rsiPane.Value, start, end, "RSI 14", RsiPen, null, 0, 100);
            nextTop += indicatorHeight;
            lastPaneBottom = rsiPane.Value.Bottom;
        }
        if (snapshot.StochasticK is { Length: > 0 })
        {
            var stochPane = new Rect(0, nextTop, chartWidth, indicatorHeight);
            DrawOscillatorPane(context, snapshot.StochasticK, snapshot.StochasticD, candles, stochPane, start, end,
                "Stoch 14·3", StochKPen, StochDPen, 0, 100);
            nextTop += indicatorHeight;
            lastPaneBottom = stochPane.Bottom;
        }
        if (snapshot.Macd is { Length: > 0 })
        {
            macdPane = new Rect(0, nextTop, chartWidth, indicatorHeight);
            DrawMacd(context, snapshot.Macd, candles, macdPane.Value, start, end);
            nextTop += indicatorHeight;
            lastPaneBottom = macdPane.Value.Bottom;
        }
        if (snapshot.Atr is { Length: > 0 })
        {
            var atrPane = new Rect(0, nextTop, chartWidth, indicatorHeight);
            var atrMax = snapshot.Atr.Max(static point => point.Value);
            DrawOscillatorPane(context, snapshot.Atr, null, candles, atrPane, start, end, "ATR 14", AtrPen, null, 0, Math.Max(atrMax, 1e-6));
            nextTop += indicatorHeight;
            lastPaneBottom = atrPane.Bottom;
        }
        if (snapshot.Adx is { Length: > 0 })
        {
            var adxPane = new Rect(0, nextTop, chartWidth, indicatorHeight);
            DrawOscillatorPane(context, snapshot.Adx, null, candles, adxPane, start, end, "ADX 14", AdxPen, null, 0, 100);
            nextTop += indicatorHeight;
            lastPaneBottom = adxPane.Bottom;
        }
        if (snapshot.CustomSeries is { } customOsc)
        {
            foreach (var custom in customOsc.Where(static item => item.OscillatorPane && item.Points.Length > 0))
            {
                var pane = new Rect(0, nextTop, chartWidth, indicatorHeight);
                var max = custom.Id.Contains("atr", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(custom.Points.Max(static point => point.Value), 1e-6)
                    : 100d;
                DrawOscillatorPane(context, custom.Points, null, candles, pane, start, end, custom.Label, CustomPen, null, 0, max);
                nextTop += indicatorHeight;
                lastPaneBottom = pane.Bottom;
            }
        }

        DrawResearchRanges(context, snapshot, start, end, lastPaneBottom);
        DrawDraftLevels(context, pricePane, priceMin, priceMax);

        DrawCrosshairAndLegend(context, snapshot, candles, pricePane, rsiPane, macdPane,
            priceMin, priceMax, start, end);

        if (!string.IsNullOrWhiteSpace(_message))
            DrawMessage(context, _message);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        _cursor = point;
        if (_selecting)
        {
            _selectionCurrent = point;
        }
        else if (_dragging && _snapshot is { Candles.Length: > 0 } snapshot)
        {
            var width = Math.Max(1, Bounds.Width - AxisWidth);
            var bars = Math.Max(1, _visibleCount);
            var deltaBars = (int)Math.Round((point.X - _dragOrigin.X) / width * bars);
            _rightOffset = Math.Clamp(_dragOffset + deltaBars, 0,
                Math.Max(0, snapshot.Candles.Length - bars));
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging && !_selecting)
            _cursor = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        if ((InteractionMode is ChartInteractionMode.PlaceStop or ChartInteractionMode.PlaceTarget) &&
            TryMapPrice(point, out var price))
        {
            PriceClicked?.Invoke(this, new ChartPriceClickedEventArgs(price));
            e.Handled = true;
            return;
        }

        if (InteractionMode == ChartInteractionMode.SelectResearchRange &&
            _snapshot is { Candles.Length: > 0 })
        {
            _selecting = true;
            _selectionAnchor = point;
            _selectionCurrent = point;
        }
        else
        {
            _dragging = true;
            _dragOrigin = point;
            _dragOffset = _rightOffset;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting && _snapshot is { Candles.Length: > 0 } snapshot)
        {
            _selectionCurrent = e.GetPosition(this);
            var (start, end) = VisibleRange(snapshot.Candles.Length);
            var range = ChartRangeSelectionMapper.Map(
                snapshot.Candles,
                start,
                end,
                Math.Max(1, Bounds.Width - AxisWidth),
                _selectionAnchor.X,
                _selectionCurrent.X);
            ResearchRangeSelected?.Invoke(this, new ChartRangeSelectedEventArgs(range));
        }
        _selecting = false;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_snapshot is not { Candles.Length: > 0 } snapshot)
            return;

        var current = Math.Clamp(_visibleCount <= 0 ? snapshot.Candles.Length : _visibleCount,
            1, snapshot.Candles.Length);
        var next = e.Delta.Y > 0 ? (int)Math.Round(current * 0.80) : (int)Math.Round(current * 1.25);
        _visibleCount = Math.Clamp(next, Math.Min(20, snapshot.Candles.Length), snapshot.Candles.Length);
        _rightOffset = Math.Clamp(_rightOffset, 0, Math.Max(0, snapshot.Candles.Length - _visibleCount));
        e.Handled = true;
        InvalidateVisual();
    }

    private void FitContent()
    {
        _visibleCount = _snapshot?.Candles.Length ?? 0;
        _rightOffset = 0;
        InvalidateVisual();
    }

    private (int Start, int End) VisibleRange(int total)
    {
        var count = Math.Clamp(_visibleCount <= 0 ? total : _visibleCount, 1, total);
        var offset = Math.Clamp(_rightOffset, 0, Math.Max(0, total - count));
        var end = total - offset;
        return (Math.Max(0, end - count), end);
    }

    private void DrawDraftLevels(DrawingContext context, Rect pricePane, double min, double max)
    {
        DrawLevel(_draftStopPrice, DraftStopPen, "STOP");
        DrawLevel(_draftTargetPrice, DraftTargetPen, "TARGET");

        void DrawLevel(double? price, IPen pen, string label)
        {
            if (price is not { } value || value < min || value > max || pricePane.Height <= 0)
                return;
            var y = Y(value, pricePane, min, max);
            context.DrawLine(pen, new Point(pricePane.Left, y), new Point(pricePane.Right, y));
            context.DrawText(
                new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    10,
                    TextBrush),
                new Point(pricePane.Left + 4, Math.Clamp(y - 12, pricePane.Top, pricePane.Bottom - 12)));
        }
    }

    private bool TryMapPrice(Point point, out decimal price) =>
        ChartPriceMapper.TryMap(
            point.X,
            point.Y,
            _lastPricePane.Left,
            _lastPricePane.Top,
            _lastPricePane.Width,
            _lastPricePane.Height,
            _lastPriceMin,
            _lastPriceMax,
            out price);

    private void DrawResearchRanges(
        DrawingContext context,
        ChartSnapshot snapshot,
        int visibleStart,
        int visibleEndExclusive,
        double bottom)
    {
        DrawRange(ObservationRange, ObservationSelectionBrush, ObservationSelectionPen);
        DrawRange(OutcomeRange, OutcomeSelectionBrush, OutcomeSelectionPen);

        if (_selecting)
        {
            ChartTimeRange preview;
            try
            {
                preview = ChartRangeSelectionMapper.Map(
                    snapshot.Candles,
                    visibleStart,
                    visibleEndExclusive,
                    Math.Max(1, Bounds.Width - AxisWidth),
                    _selectionAnchor.X,
                    _selectionCurrent.X);
            }
            catch (ArgumentException)
            {
                return;
            }

            var selectingOutcome = ObservationRange is not null;
            DrawRange(
                preview,
                selectingOutcome ? OutcomeSelectionBrush : ObservationSelectionBrush,
                selectingOutcome ? OutcomeSelectionPen : ObservationSelectionPen);
        }

        void DrawRange(ChartTimeRange? range, IBrush fill, IPen border)
        {
            if (range is null) return;
            var first = LowerBound(snapshot.Candles, range.StartUtc.ToUnixTimeSeconds());
            var afterLast = LowerBound(snapshot.Candles, range.EndUtcExclusive.ToUnixTimeSeconds());
            first = Math.Clamp(first, visibleStart, visibleEndExclusive);
            afterLast = Math.Clamp(afterLast, visibleStart, visibleEndExclusive);
            if (afterLast <= first) return;

            var width = Math.Max(1, Bounds.Width - AxisWidth);
            var count = visibleEndExclusive - visibleStart;
            var left = (first - visibleStart) / (double)count * width;
            var right = (afterLast - visibleStart) / (double)count * width;
            var rect = new Rect(left, 0, Math.Max(1, right - left), Math.Max(1, bottom));
            context.FillRectangle(fill, rect);
            context.DrawRectangle(null, border, rect);
        }
    }

    private static (double Min, double Max) PriceRange(ChartSnapshot snapshot, int start, int end)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var priceOnly = snapshot.ChartType is "Line" or "Area";
        for (var i = start; i < end; i++)
        {
            var candle = snapshot.Candles[i];
            Include(priceOnly ? candle.Close : candle.Low, ref min, ref max);
            Include(priceOnly ? candle.Close : candle.High, ref min, ref max);
        }

        Include(snapshot.Sma, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        Include(snapshot.Ema, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        Include(snapshot.Vwap, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        Include(snapshot.BollingerMid, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        Include(snapshot.BollingerUpper, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        Include(snapshot.BollingerLower, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        if (snapshot.CustomSeries is { } customs)
        {
            foreach (var custom in customs.Where(static item => !item.OscillatorPane))
                Include(custom.Points, snapshot.Candles[start].Time, snapshot.Candles[end - 1].Time, ref min, ref max);
        }
        if (!double.IsFinite(min) || !double.IsFinite(max))
            return (0, 1);
        if (max <= min)
            max = min + Math.Max(1, Math.Abs(min) * 0.01);
        var pad = (max - min) * 0.06;
        return (min - pad, max + pad);
    }

    private static void Include(double value, ref double min, ref double max)
    {
        if (!double.IsFinite(value)) return;
        min = Math.Min(min, value);
        max = Math.Max(max, value);
    }

    private static void Include(ChartLinePoint[]? points, long firstTime, long lastTime,
        ref double min, ref double max)
    {
        if (points is null) return;
        var index = LowerBound(points, firstTime);
        while (index < points.Length && points[index].Time <= lastTime)
        {
            Include(points[index].Value, ref min, ref max);
            index++;
        }
    }

    private static void DrawGrid(DrawingContext context, Rect pane, double min, double max,
        ChartCandle[] candles, int start, int end, bool drawTimeLabels)
    {
        context.DrawLine(BorderPen, pane.TopRight, pane.BottomRight);
        for (var i = 0; i <= 4; i++)
        {
            var y = pane.Top + pane.Height * i / 4d;
            context.DrawLine(GridPen, new Point(pane.Left, y), new Point(pane.Right, y));
            var value = max - (max - min) * i / 4d;
            DrawText(context, value.ToString("0.####", CultureInfo.InvariantCulture),
                new Point(pane.Right + 5, Math.Clamp(y - 7, pane.Top, pane.Bottom - 14)), DimTextBrush, 10);
        }

        for (var i = 1; i < 7; i++)
        {
            var x = pane.Left + pane.Width * i / 7d;
            context.DrawLine(GridPen, new Point(x, pane.Top), new Point(x, pane.Bottom));
        }
        if (drawTimeLabels)
            DrawTimeAxis(context, pane, candles, start, end);
    }

    private static void DrawTimeAxis(DrawingContext context, Rect pane, ChartCandle[] candles, int start, int end)
    {
        context.DrawLine(BorderPen, pane.BottomLeft, pane.BottomRight);
        var span = candles[end - 1].Time - candles[start].Time;
        for (var i = 0; i <= 5; i++)
        {
            var index = Math.Clamp(start + (int)Math.Round((end - start - 1) * i / 5d), start, end - 1);
            var x = X(index, pane, start, end);
            var time = DateTimeOffset.FromUnixTimeSeconds(candles[index].Time).ToLocalTime();
            var label = span >= TimeSpan.FromDays(2).TotalSeconds ? time.ToString("MMM d") : time.ToString("HH:mm");
            DrawText(context, label, new Point(Math.Clamp(x - 20, 0, pane.Right - 40), pane.Bottom + 4), DimTextBrush, 9);
        }
    }

    private static void DrawVolume(DrawingContext context, ChartSnapshot snapshot, Rect pane, int start, int end)
    {
        if (snapshot.Volume.Length == 0) return;
        var max = 0d;
        for (var i = start; i < Math.Min(end, snapshot.Volume.Length); i++)
            max = Math.Max(max, snapshot.Volume[i].Value);
        if (max <= 0) return;

        var height = pane.Height * 0.18;
        var width = Math.Max(1, Math.Min(14, pane.Width / Math.Max(1, end - start) * 0.7));
        for (var i = start; i < Math.Min(end, snapshot.Volume.Length); i++)
        {
            var volume = snapshot.Volume[i];
            var h = Math.Max(1, height * volume.Value / max);
            var x = X(i, pane, start, end);
            var brush = snapshot.Candles[i].Close >= snapshot.Candles[i].Open ? UpVolumeBrush : DownVolumeBrush;
            context.FillRectangle(brush, new Rect(x - width / 2, pane.Bottom - h, width, h));
        }
    }

    private static void DrawPrice(DrawingContext context, ChartSnapshot snapshot, Rect pane,
        double min, double max, int start, int end)
    {
        switch (snapshot.ChartType)
        {
            case "Bars":
                DrawBars(context, snapshot.Candles, pane, min, max, start, end);
                break;
            case "Line":
                DrawCloseLine(context, snapshot.Candles, pane, min, max, start, end, fill: false);
                break;
            case "Area":
                DrawCloseLine(context, snapshot.Candles, pane, min, max, start, end, fill: true);
                break;
            default:
                DrawCandles(context, snapshot.Candles, pane, min, max, start, end);
                break;
        }
    }

    private static void DrawCandles(DrawingContext context, ChartCandle[] candles, Rect pane,
        double min, double max, int start, int end)
    {
        var width = Math.Max(1, Math.Min(14, pane.Width / Math.Max(1, end - start) * 0.64));
        for (var i = start; i < end; i++)
        {
            var candle = candles[i];
            var x = X(i, pane, start, end);
            var up = candle.Close >= candle.Open;
            var brush = up ? UpBrush : DownBrush;
            var pen = up ? UpPen : DownPen;
            context.DrawLine(pen, new Point(x, Y(candle.High, pane, min, max)),
                new Point(x, Y(candle.Low, pane, min, max)));
            var openY = Y(candle.Open, pane, min, max);
            var closeY = Y(candle.Close, pane, min, max);
            var top = Math.Min(openY, closeY);
            var height = Math.Max(1, Math.Abs(openY - closeY));
            context.FillRectangle(brush, new Rect(x - width / 2, top, width, height));
        }
    }

    private static void DrawBars(DrawingContext context, ChartCandle[] candles, Rect pane,
        double min, double max, int start, int end)
    {
        var tick = Math.Max(1.5, Math.Min(6, pane.Width / Math.Max(1, end - start) * 0.32));
        for (var i = start; i < end; i++)
        {
            var candle = candles[i];
            var x = X(i, pane, start, end);
            var pen = candle.Close >= candle.Open ? UpPen : DownPen;
            context.DrawLine(pen, new Point(x, Y(candle.High, pane, min, max)),
                new Point(x, Y(candle.Low, pane, min, max)));
            context.DrawLine(pen, new Point(x - tick, Y(candle.Open, pane, min, max)),
                new Point(x, Y(candle.Open, pane, min, max)));
            context.DrawLine(pen, new Point(x, Y(candle.Close, pane, min, max)),
                new Point(x + tick, Y(candle.Close, pane, min, max)));
        }
    }

    private static void DrawCloseLine(DrawingContext context, ChartCandle[] candles, Rect pane,
        double min, double max, int start, int end, bool fill)
    {
        if (end - start < 2) return;
        var line = new PathFigure { StartPoint = new Point(X(start, pane, start, end), Y(candles[start].Close, pane, min, max)) };
        for (var i = start + 1; i < end; i++)
            line.Segments!.Add(new LineSegment { Point = new Point(X(i, pane, start, end), Y(candles[i].Close, pane, min, max)) });
        var geometry = new PathGeometry();
        geometry.Figures!.Add(line);

        if (fill)
        {
            var area = new PathFigure { StartPoint = new Point(X(start, pane, start, end), pane.Bottom), IsClosed = true };
            area.Segments!.Add(new LineSegment { Point = line.StartPoint });
            for (var i = start + 1; i < end; i++)
                area.Segments!.Add(new LineSegment { Point = new Point(X(i, pane, start, end), Y(candles[i].Close, pane, min, max)) });
            area.Segments!.Add(new LineSegment { Point = new Point(X(end - 1, pane, start, end), pane.Bottom) });
            var fillGeometry = new PathGeometry();
            fillGeometry.Figures!.Add(area);
            context.DrawGeometry(AreaBrush, null, fillGeometry);
        }
        context.DrawGeometry(null, LinePen, geometry);
    }

    private static void DrawLineSeries(DrawingContext context, ChartLinePoint[]? points,
        ChartCandle[] candles, Rect pane, double min, double max, int start, int end, IPen pen)
    {
        if (points is not { Length: > 1 }) return;
        var firstTime = candles[start].Time;
        var lastTime = candles[end - 1].Time;
        var pointIndex = LowerBound(points, firstTime);
        Point? previous = null;
        var lastCandleIndex = -2;
        while (pointIndex < points.Length && points[pointIndex].Time <= lastTime)
        {
            var point = points[pointIndex++];
            var candleIndex = FindNearestCandle(candles, point.Time, start, end);
            if (candleIndex < start || candleIndex >= end)
            {
                previous = null;
                lastCandleIndex = -2;
                continue;
            }

            var p = new Point(X(candleIndex, pane, start, end), Y(point.Value, pane, min, max));
            // Only stroke adjacent candles — never chord across gaps or backward jumps.
            if (previous is { } from && candleIndex == lastCandleIndex + 1)
                context.DrawLine(pen, from, p);

            previous = p;
            lastCandleIndex = candleIndex;
        }
    }

    /// <summary>
    /// Map an indicator sample onto a visible candle. Exact match preferred; otherwise nearest
    /// candle within one step so minor timestamp skew cannot skip most points and leave
    /// long diagonal chords between the few that remain.
    /// </summary>
    private static int FindNearestCandle(ChartCandle[] candles, long time, int start, int end)
    {
        var exact = FindExact(candles, time);
        if (exact >= start && exact < end) return exact;

        var lo = LowerBound(candles, time);
        var best = -1;
        var bestDelta = long.MaxValue;
        foreach (var candidate in new[] { lo - 1, lo, lo + 1 })
        {
            if (candidate < start || candidate >= end) continue;
            var delta = Math.Abs(candles[candidate].Time - time);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        if (best < 0) return -1;
        // Reject matches farther than ~1.5× the local bar spacing (avoids weekend chords).
        var span = end - start;
        if (span >= 2)
        {
            var typical = Math.Abs(candles[Math.Min(end - 1, start + 1)].Time - candles[start].Time);
            if (typical > 0 && bestDelta > typical + typical / 2)
                return -1;
        }

        return best;
    }

    private static void DrawOscillatorPane(
        DrawingContext context,
        ChartLinePoint[] primary,
        ChartLinePoint[]? secondary,
        ChartCandle[] candles,
        Rect pane,
        int start,
        int end,
        string title,
        IPen primaryPen,
        IPen? secondaryPen,
        double min,
        double max)
    {
        context.DrawLine(BorderPen, pane.TopLeft, pane.TopRight);
        context.DrawLine(BorderPen, pane.TopRight, pane.BottomRight);
        if (max <= 100.0001 && min >= -0.0001)
        {
            var y70 = Y(70, pane, min, max);
            var y50 = Y(50, pane, min, max);
            var y30 = Y(30, pane, min, max);
            context.DrawLine(RsiUpperPen, new Point(0, y70), new Point(pane.Right, y70));
            context.DrawLine(GridPen, new Point(0, y50), new Point(pane.Right, y50));
            context.DrawLine(RsiLowerPen, new Point(0, y30), new Point(pane.Right, y30));
        }

        DrawText(context, title, new Point(8, pane.Top + 5), DimTextBrush, 10);
        DrawLineSeries(context, primary, candles, pane, min, max, start, end, primaryPen);
        if (secondary is { Length: > 0 } && secondaryPen is not null)
            DrawLineSeries(context, secondary, candles, pane, min, max, start, end, secondaryPen);
        DrawTimeAxis(context, pane, candles, start, end);
    }

    private static void DrawRsi(DrawingContext context, ChartLinePoint[] rsi, ChartCandle[] candles,
        Rect pane, int start, int end, bool drawTimeLabels)
    {
        DrawOscillatorPane(context, rsi, null, candles, pane, start, end, "RSI 14", RsiPen, null, 0, 100);
    }

    private static void DrawMacd(DrawingContext context, MacdPoint[] points, ChartCandle[] candles,
        Rect pane, int start, int end)
    {
        context.DrawLine(BorderPen, pane.TopLeft, pane.TopRight);
        context.DrawLine(BorderPen, pane.TopRight, pane.BottomRight);
        var firstTime = candles[start].Time;
        var lastTime = candles[end - 1].Time;
        var pointIndex = LowerBound(points, firstTime);
        var extent = 0d;
        for (var i = pointIndex; i < points.Length && points[i].Time <= lastTime; i++)
            extent = Math.Max(extent, Math.Max(Math.Abs(points[i].Hist), Math.Max(Math.Abs(points[i].Macd), Math.Abs(points[i].Signal))));
        extent = extent <= 0 ? 1 : extent * 1.12;
        var zero = Y(0, pane, -extent, extent);
        context.DrawLine(GridPen, new Point(0, zero), new Point(pane.Right, zero));
        DrawText(context, "MACD 12·26·9", new Point(8, pane.Top + 5), DimTextBrush, 10);

        var width = Math.Max(1, Math.Min(12, pane.Width / Math.Max(1, end - start) * 0.60));
        PathFigure? macdFigure = null;
        PathFigure? signalFigure = null;
        for (var i = pointIndex; i < points.Length && points[i].Time <= lastTime; i++)
        {
            var point = points[i];
            var candleIndex = FindExact(candles, point.Time);
            if (candleIndex < start || candleIndex >= end) continue;
            var x = X(candleIndex, pane, start, end);
            var histY = Y(point.Hist, pane, -extent, extent);
            context.FillRectangle(point.Hist >= 0 ? UpVolumeBrush : DownVolumeBrush,
                new Rect(x - width / 2, Math.Min(zero, histY), width, Math.Max(1, Math.Abs(histY - zero))));
            AddPoint(ref macdFigure, new Point(x, Y(point.Macd, pane, -extent, extent)));
            AddPoint(ref signalFigure, new Point(x, Y(point.Signal, pane, -extent, extent)));
        }
        DrawFigure(context, macdFigure, MacdPen);
        DrawFigure(context, signalFigure, SignalPen);
        DrawTimeAxis(context, pane, candles, start, end);
    }

    private void DrawCrosshairAndLegend(DrawingContext context, ChartSnapshot snapshot, ChartCandle[] candles,
        Rect pricePane, Rect? rsiPane, Rect? macdPane, double min, double max, int start, int end)
    {
        var selected = end - 1;
        if (_cursor is { } cursor && cursor.X >= 0 && cursor.X <= pricePane.Right)
        {
            selected = Math.Clamp(start + (int)Math.Floor(cursor.X / Math.Max(1, pricePane.Width) * (end - start)), start, end - 1);
            var x = X(selected, pricePane, start, end);
            var bottom = macdPane?.Bottom ?? rsiPane?.Bottom ?? pricePane.Bottom;
            context.DrawLine(CrosshairPen, new Point(x, 0), new Point(x, bottom));
            if (cursor.Y >= pricePane.Top && cursor.Y <= pricePane.Bottom)
                context.DrawLine(CrosshairPen, new Point(0, cursor.Y), new Point(pricePane.Right, cursor.Y));
        }

        var candle = candles[selected];
        var valueBrush = candle.Close >= candle.Open ? UpBrush : DownBrush;
        DrawText(context, $"{snapshot.Symbol}  {snapshot.Timeframe}", new Point(12, 8), AmberBrush, 12);
        DrawText(context,
            $"O {candle.Open:0.####}   H {candle.High:0.####}   L {candle.Low:0.####}   C {candle.Close:0.####}",
            new Point(12, 26), valueBrush, 11);

        if (_cursor is { } p && p.Y >= pricePane.Top && p.Y <= pricePane.Bottom)
        {
            var price = max - (p.Y - pricePane.Top) / Math.Max(1, pricePane.Height) * (max - min);
            DrawText(context, price.ToString("0.####", CultureInfo.InvariantCulture),
                new Point(pricePane.Right + 5, Math.Clamp(p.Y - 7, pricePane.Top, pricePane.Bottom - 14)), TextBrush, 10);
        }
    }

    private void DrawMessage(DrawingContext context, string message)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        var lines = message.Replace("\r", string.Empty).Split('\n');
        var y = Math.Max(20, Bounds.Height / 2 - lines.Length * 10);
        for (var i = 0; i < lines.Length; i++)
        {
            var brush = i == 0 ? TextBrush : DimTextBrush;
            var size = i == 0 ? 15 : 12;
            var text = new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
            context.DrawText(text, new Point(Math.Max(12, (Bounds.Width - text.Width) / 2), y));
            y += i == 0 ? 25 : 20;
        }
    }

    private static void AddPoint(ref PathFigure? figure, Point point)
    {
        if (figure is null) figure = new PathFigure { StartPoint = point };
        else figure.Segments!.Add(new LineSegment { Point = point });
    }

    private static void DrawFigure(DrawingContext context, PathFigure? figure, IPen pen)
    {
        if (figure?.Segments is not { Count: > 0 }) return;
        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);
        context.DrawGeometry(null, pen, geometry);
    }

    private static double X(int index, Rect pane, int start, int end) =>
        pane.Left + (index - start + 0.5) / Math.Max(1, end - start) * pane.Width;

    private static double Y(double value, Rect pane, double min, double max) =>
        pane.Bottom - (value - min) / Math.Max(double.Epsilon, max - min) * pane.Height;

    private static int LowerBound(ChartLinePoint[] points, long time)
    {
        var lo = 0;
        var hi = points.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (points[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(ChartCandle[] points, long time)
    {
        var lo = 0;
        var hi = points.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (points[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(MacdPoint[] points, long time)
    {
        var lo = 0;
        var hi = points.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (points[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int FindExact(ChartCandle[] candles, long time)
    {
        var lo = 0;
        var hi = candles.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var candidate = candles[mid].Time;
            if (candidate == time) return mid;
            if (candidate < time) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        context.DrawText(formatted, point);
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private static IPen Pen(string color, double width, bool dash = false) =>
        new Pen(Brush(color), width, dash ? new DashStyle(new[] { 3d, 3d }, 0) : null);
}
