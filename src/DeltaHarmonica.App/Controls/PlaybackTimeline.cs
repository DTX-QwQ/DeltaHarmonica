using DeltaHarmonica.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Color = Windows.UI.Color;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeltaHarmonica.App.Controls;

/// <summary>A shared MIDI-time rail for playback position and the two range boundaries.</summary>
public sealed class PlaybackTimeline : UserControl
{
    private const double RailInset = 34;
    private const double RailTop = 30;
    private const double NotesTop = 46;
    private const double NotesHeight = 16;
    private readonly Canvas _surface = new() { Height = 72, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border _rail = Bar(0x35, 0x41, 0x4B, 8);
    private readonly Border _selection = Bar(0x36, 0x99, 0x7D, 8);
    private readonly Border _played = Bar(0x63, 0xE6, 0xBE, 8);
    private readonly Border _noteRail = Bar(0x25, 0x31, 0x3B, NotesHeight);
    private readonly XamlPath _allNotes = NotePath(0x63, 0xE6, 0xBE, .28);
    private readonly XamlPath _selectedNotes = NotePath(0x63, 0xE6, 0xBE, 1);
    private readonly XamlPath _allOnsets = NotePath(0xC8, 0xFF, 0xEF, .28);
    private readonly XamlPath _selectedOnsets = NotePath(0xC8, 0xFF, 0xEF, 1);
    private readonly RectangleGeometry _selectedNoteClip = new();
    private readonly RectangleGeometry _selectedOnsetClip = new();
    private readonly Border _startLine = Bar(0x63, 0xE6, 0xBE, 43);
    private readonly Border _endLine = Bar(0x63, 0xE6, 0xBE, 43);
    private readonly TimelineHandle _startHandle;
    private readonly TimelineHandle _endHandle;
    private readonly TimelineHandle _positionHandle;
    private double _maximum = 1, _rangeStart, _rangeEnd, _value;
    private bool _rangeEnabled, _seekEnabled;
    private TimelineHandle? _dragHandle;
    private uint? _dragPointer;
    private double _dragOffset;
    private NoteInterval[] _noteIntervals = [];
    private bool _noteGeometryDirty = true;
    private double _noteGeometryWidth = -1;
    private double _noteClipStart = double.NaN, _noteClipEnd = double.NaN;

    public event EventHandler? RangeChanged;
    public event EventHandler? PositionChanged;

    public PlaybackTimeline()
    {
        IsTabStop = false;
        Content = _surface;
        _startHandle = new(this, TimelinePart.Start, "开始演奏位置", "起");
        _endHandle = new(this, TimelinePart.End, "结束演奏位置", "止");
        _positionHandle = new(this, TimelinePart.Position, "播放位置", null);
        _selectedNotes.Clip = _selectedNoteClip;
        _selectedOnsets.Clip = _selectedOnsetClip;
        foreach (var element in new FrameworkElement[] { _rail, _selection, _played, _noteRail, _allNotes, _selectedNotes, _allOnsets, _selectedOnsets, _startLine, _endLine, _startHandle, _endHandle, _positionHandle })
            _surface.Children.Add(element);
        foreach (var handle in new[] { _startHandle, _endHandle, _positionHandle })
        {
            handle.PointerPressed += Handle_PointerPressed;
            handle.PointerMoved += Handle_PointerMoved;
            handle.PointerReleased += Handle_PointerReleased;
            handle.PointerCanceled += Handle_PointerReleased;
            handle.PointerCaptureLost += (_, _) => EndDrag();
        }
        _surface.PointerPressed += Surface_PointerPressed;
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerReleased += Surface_PointerReleased;
        _surface.PointerCanceled += Surface_PointerReleased;
        _surface.PointerCaptureLost += (_, _) => EndDrag();
        SizeChanged += (_, _) => UpdateVisuals();
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => UpdateAvailability());
        UpdateAvailability();
    }

    public double Maximum
    {
        get => _maximum;
        set
        {
            var maximum = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_maximum != maximum) _noteGeometryDirty = true;
            _maximum = maximum;
            SetRange(_rangeStart, _rangeEnd);
            Value = _value;
        }
    }
    public double RangeStart => _rangeStart;
    public double RangeEnd => _rangeEnd;
    public double MinimumRange { get; set; } = .01;
    public double Value
    {
        get => _value;
        set
        {
            // Playback snapshots must not pull a captured seek marker away from the pointer.
            if (_dragPointer.HasValue && _dragHandle?.Part == TimelinePart.Position) return;
            var previous = _value;
            _value = double.IsFinite(value) ? Math.Clamp(value, 0, _maximum) : 0;
            UpdateVisuals();
            _positionHandle.RaiseValueChanged(previous, _value);
        }
    }
    public bool IsRangeEnabled
    {
        get => _rangeEnabled;
        set { _rangeEnabled = value; UpdateAvailability(); }
    }
    public bool IsSeekEnabled
    {
        get => _seekEnabled;
        set { _seekEnabled = value; UpdateAvailability(); }
    }

    /// <summary>Displays playable chord durations on the same MIDI-time scale as the range handles.</summary>
    public void SetNoteIntervals(IReadOnlyList<ScheduledChord> chords)
    {
        ArgumentNullException.ThrowIfNull(chords);
        // Copy once when the plan changes. Playback snapshots only move existing visuals.
        _noteIntervals = chords.Where(chord => chord.Duration > TimeSpan.Zero && chord.Notes.Count > 0)
            .Select(chord => new NoteInterval(chord.Start.TotalSeconds,
                chord.Start.TotalSeconds + chord.Duration.TotalSeconds, Math.Min(7, chord.Notes.Count),
                chord.RetriggerKeys.Count > 0)).ToArray();
        _noteGeometryDirty = true;
        UpdateVisuals();
    }

    /// <summary>Programmatic synchronization never seeks or raises a range edit.</summary>
    public void SetRange(double start, double end)
    {
        var previousStart = _rangeStart;
        var previousEnd = _rangeEnd;
        _rangeEnd = double.IsFinite(end) ? Math.Clamp(end, 0, _maximum) : _maximum;
        _rangeStart = double.IsFinite(start) ? Math.Clamp(start, 0, _rangeEnd) : 0;
        UpdateVisuals();
        _startHandle.RaiseValueChanged(previousStart, _rangeStart);
        _endHandle.RaiseValueChanged(previousEnd, _rangeEnd);
    }

    internal double GetValue(TimelinePart part) => part switch { TimelinePart.Start => _rangeStart, TimelinePart.End => _rangeEnd, _ => _value };
    internal double GetMinimum(TimelinePart part) => part switch { TimelinePart.End => Math.Min(_maximum, _rangeStart + Gap), TimelinePart.Position => _rangeStart, _ => 0 };
    internal double GetMaximum(TimelinePart part) => part switch { TimelinePart.Start => Math.Max(0, _rangeEnd - Gap), TimelinePart.Position => _rangeEnd, _ => _maximum };
    private double Gap => Math.Min(_maximum, Math.Max(0, MinimumRange));

    internal void SetHandleValue(TimelinePart part, double value)
    {
        if (!IsEnabled || !double.IsFinite(value) || (part == TimelinePart.Position ? !_seekEnabled : !_rangeEnabled)) return;
        value = Math.Clamp(value, GetMinimum(part), GetMaximum(part));
        if (value == GetValue(part)) return;
        if (part == TimelinePart.Position)
        {
            var previous = _value;
            _value = value;
            UpdateVisuals();
            _positionHandle.RaiseValueChanged(previous, value);
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SetRange(part == TimelinePart.Start ? value : _rangeStart, part == TimelinePart.End ? value : _rangeEnd);
            RangeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateAvailability()
    {
        _startHandle.IsEnabled = _endHandle.IsEnabled = IsEnabled && _rangeEnabled;
        _positionHandle.IsEnabled = IsEnabled && _seekEnabled;
        if (_dragHandle is { IsEnabled: false }) CancelDrag();
        _surface.Opacity = IsEnabled ? 1 : .45;
        _startHandle.Opacity = _endHandle.Opacity = _rangeEnabled ? 1 : .55;
        _positionHandle.Opacity = _seekEnabled ? 1 : .65;
    }

    private double RailWidth => Math.Max(0, _surface.ActualWidth - RailInset * 2);
    private double X(double seconds) => RailInset + (_maximum > 0 ? Math.Clamp(seconds / _maximum, 0, 1) * RailWidth : 0);
    private double Seconds(double x) => RailWidth > 0 ? Math.Clamp((x - RailInset) / RailWidth, 0, 1) * _maximum : 0;
    private void UpdateVisuals()
    {
        PlaceBar(_rail, RailInset, RailWidth, RailTop);
        PlaceBar(_selection, X(_rangeStart), Math.Max(0, X(_rangeEnd) - X(_rangeStart)), RailTop);
        PlaceBar(_played, X(_rangeStart), Math.Max(0, X(Math.Clamp(_value, _rangeStart, _rangeEnd)) - X(_rangeStart)), RailTop);
        PlaceBar(_noteRail, RailInset, RailWidth, NotesTop);
        UpdateNoteVisuals();
        PlaceBar(_startLine, X(_rangeStart) - 1, 2, 22);
        PlaceBar(_endLine, X(_rangeEnd) - 1, 2, 22);
        // Tabs face outwards so both remain draggable even for a sub-pixel selected interval.
        Canvas.SetLeft(_startHandle, X(_rangeStart) - _startHandle.Width);
        Canvas.SetLeft(_endHandle, X(_rangeEnd));
        Canvas.SetLeft(_positionHandle, X(_value) - _positionHandle.Width / 2);
        Canvas.SetTop(_positionHandle, 26);
        ToolTipService.SetToolTip(_startHandle, $"开始 · {TimeSpan.FromSeconds(_rangeStart):mm\\:ss\\.fff}");
        ToolTipService.SetToolTip(_endHandle, $"结束 · {TimeSpan.FromSeconds(_rangeEnd):mm\\:ss\\.fff}");
    }

    private void UpdateNoteVisuals()
    {
        var width = RailWidth;
        if (_noteGeometryDirty || _noteGeometryWidth != width)
        {
            RebuildNoteGeometry(width);
            _noteGeometryDirty = false;
            _noteGeometryWidth = width;
        }
        var start = X(_rangeStart);
        var end = X(_rangeEnd);
        if (_noteClipStart != start || _noteClipEnd != end)
        {
            // Each WinUI visual owns its geometry; range edits only move the two clips.
            _selectedNoteClip.Rect = new Rect(start, NotesTop, Math.Max(0, end - start), NotesHeight);
            _selectedOnsetClip.Rect = _selectedNoteClip.Rect;
            _noteClipStart = start;
            _noteClipEnd = end;
        }
    }

    private void RebuildNoteGeometry(double width)
    {
        var notes = new PathGeometry();
        var onsets = new PathGeometry { FillRule = FillRule.Nonzero };
        if (width > 0 && _maximum > 0 && _noteIntervals.Length > 0)
        {
            // Bucket by displayed pixel, bounding the geometry even for very large MIDI files.
            // Difference arrays make plan traversal linear rather than visiting every covered pixel.
            var columns = Math.Min(8192, Math.Max(1, (int)Math.Ceiling(width)));
            var counts = new int[columns + 1];
            var starts = new bool[columns];
            foreach (var interval in _noteIntervals)
            {
                if (interval.End <= 0 || interval.Start >= _maximum) continue;
                var first = Math.Clamp((int)Math.Floor(Math.Max(0, interval.Start) / _maximum * columns), 0, columns - 1);
                var last = Math.Clamp((int)Math.Ceiling(Math.Min(_maximum, interval.End) / _maximum * columns), first + 1, columns);
                counts[first] += interval.Count;
                counts[last] -= interval.Count;
                if (interval.HasOnset && interval.Start >= 0) starts[first] = true;
            }
            var columnWidth = width / columns;
            var active = 0;
            var runStart = 0;
            var previous = 0;
            for (var column = 0; column <= columns; column++)
            {
                if (column < columns) active += counts[column];
                var count = column == columns ? 0 : Math.Min(7, active);
                if (count != previous)
                {
                    if (previous > 0)
                    {
                        var height = 4 + previous * 1.5;
                        AddRectangle(notes, RailInset + runStart * columnWidth, NotesTop + NotesHeight - height,
                            (column - runStart) * columnWidth, height);
                    }
                    runStart = column;
                    previous = count;
                }
                if (column < columns && starts[column])
                {
                    var x = RailInset + column * columnWidth;
                    AddRectangle(onsets, x, NotesTop + 1, Math.Min(1.5, RailInset + width - x), NotesHeight - 1);
                }
            }
        }
        _allNotes.Data = notes;
        _selectedNotes.Data = CopyGeometry(notes);
        _allOnsets.Data = onsets;
        _selectedOnsets.Data = CopyGeometry(onsets);
    }

    private static PathGeometry CopyGeometry(PathGeometry source)
    {
        var copy = new PathGeometry { FillRule = source.FillRule };
        foreach (var figure in source.Figures)
        {
            var clone = new PathFigure { StartPoint = figure.StartPoint, IsClosed = figure.IsClosed, IsFilled = figure.IsFilled };
            foreach (var segment in figure.Segments.Cast<LineSegment>())
                clone.Segments.Add(new LineSegment { Point = segment.Point });
            copy.Figures.Add(clone);
        }
        return copy;
    }

    private static void AddRectangle(PathGeometry geometry, double x, double y, double width, double height)
    {
        var figure = new PathFigure { StartPoint = new Point(x, y), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(x + width, y) });
        figure.Segments.Add(new LineSegment { Point = new Point(x + width, y + height) });
        figure.Segments.Add(new LineSegment { Point = new Point(x, y + height) });
        geometry.Figures.Add(figure);
    }

    private readonly record struct NoteInterval(double Start, double End, int Count, bool HasOnset);

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (TimelineHandle)sender;
        if (!handle.IsEnabled || !e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
        handle.Focus(FocusState.Pointer);
        if (!handle.CapturePointer(e.Pointer)) return;
        _dragHandle = handle;
        _dragPointer = e.Pointer.PointerId;
        // Preserve where the user grabbed the tab so a drag starts without a jump.
        _dragOffset = e.GetCurrentPoint(_surface).Position.X - X(GetValue(handle.Part));
        e.Handled = true;
    }
    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId || _dragHandle is null) return;
        SetHandleValue(_dragHandle.Part, Seconds(e.GetCurrentPoint(_surface).Position.X - _dragOffset));
        e.Handled = true;
    }
    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        ((TimelineHandle)sender).ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }
    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_surface);
        if (!IsEnabled || !_seekEnabled || point.Position.Y < 26 || !point.Properties.IsLeftButtonPressed) return;
        _positionHandle.Focus(FocusState.Pointer);
        if (!_surface.CapturePointer(e.Pointer)) return;
        _dragHandle = _positionHandle;
        _dragPointer = e.Pointer.PointerId;
        _dragOffset = 0;
        SetHandleValue(TimelinePart.Position, Seconds(e.GetCurrentPoint(_surface).Position.X));
        e.Handled = true;
    }
    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e) => Handle_PointerMoved(sender, e);
    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointer != e.Pointer.PointerId) return;
        _surface.ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }
    private void EndDrag() { _dragHandle = null; _dragPointer = null; }
    private void CancelDrag()
    {
        var handle = _dragHandle;
        EndDrag();
        handle?.ReleasePointerCaptures();
        _surface.ReleasePointerCaptures();
    }
    private static Border Bar(byte r, byte g, byte b, double height) => new()
    {
        Height = height, CornerRadius = new CornerRadius(4), IsHitTestVisible = false,
        Background = new SolidColorBrush(Color.FromArgb(255, r, g, b))
    };
    private static XamlPath NotePath(byte r, byte g, byte b, double opacity) => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(255, r, g, b)), Opacity = opacity,
        IsHitTestVisible = false, Stretch = Stretch.None
    };
    private static void PlaceBar(Border bar, double left, double width, double top)
    {
        bar.Width = width;
        Canvas.SetLeft(bar, left);
        Canvas.SetTop(bar, top);
    }
}

internal enum TimelinePart { Start, End, Position }

/// <summary>Focusable native handle with keyboard and UI Automation RangeValue support.</summary>
internal sealed class TimelineHandle : UserControl
{
    private readonly PlaybackTimeline _timeline;
    internal TimelinePart Part { get; }
    internal double CurrentValue => _timeline.GetValue(Part);
    internal double Minimum => _timeline.GetMinimum(Part);
    internal double Maximum => _timeline.GetMaximum(Part);
    internal double SmallChange => Part == TimelinePart.Position ? 1 : .01;
    internal double LargeChange => 5;
    public TimelineHandle(PlaybackTimeline timeline, TimelinePart part, string name, string? caption)
    {
        _timeline = timeline;
        Part = part;
        Width = 34;
        Height = caption is null ? 34 : 26;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        XYFocusKeyboardNavigation = Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode.Disabled;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, "方向键微调，Page Up / Page Down 调整 5 秒，Home / End 移到允许的边界。");
        var hitArea = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        if (caption is null)
        {
            hitArea.Children.Add(new Border { Width = 2, Height = 39, Background = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Top });
            hitArea.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Colors.White), Stroke = new SolidColorBrush(Color.FromArgb(255, 0x20, 0x2B, 0x32)), StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Top });
        }
        else
        {
            hitArea.Children.Add(new Border
            {
                Width = 26, Height = 24, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(255, 0x63, 0xE6, 0xBE)),
                HorizontalAlignment = part == TimelinePart.Start ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = new TextBlock { Text = caption, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x1D, 0x24)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        }
        Content = hitArea;
        KeyDown += (_, e) =>
        {
            var value = e.Key switch
            {
                VirtualKey.Left or VirtualKey.Down => CurrentValue - SmallChange,
                VirtualKey.Right or VirtualKey.Up => CurrentValue + SmallChange,
                VirtualKey.PageDown => CurrentValue - LargeChange,
                VirtualKey.PageUp => CurrentValue + LargeChange,
                VirtualKey.Home => Minimum,
                VirtualKey.End => Maximum,
                _ => double.NaN
            };
            if (!double.IsFinite(value)) return;
            SetValue(value);
            e.Handled = true;
        };
    }
    internal void SetValue(double value) => _timeline.SetHandleValue(Part, value);
    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineHandleAutomationPeer(this);
    internal void RaiseValueChanged(double previous, double current)
    {
        if (previous != current && FrameworkElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, previous, current);
    }
}

internal sealed class TimelineHandleAutomationPeer(TimelineHandle owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    protected override string GetClassNameCore() => "Slider";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
    public bool IsReadOnly => !owner.IsEnabled;
    public double LargeChange => owner.LargeChange;
    public double Maximum => owner.Maximum;
    public double Minimum => owner.Minimum;
    public double SmallChange => owner.SmallChange;
    public double Value => owner.CurrentValue;
    public void SetValue(double value)
    {
        if (!owner.IsEnabled) throw new InvalidOperationException("当前无法调整此位置。");
        if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
        owner.SetValue(value);
    }
}
