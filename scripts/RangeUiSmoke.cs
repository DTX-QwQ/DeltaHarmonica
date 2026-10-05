using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DeltaHarmonica.App.Controls;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeltaHarmonica.App;

// Only the copied smoke project compiles this file. No native input is sent.
public sealed class UiSmokeInputSink : IInputSink
{
    public static UiSmokeInputSink Instance { get; } = new();
    public int ChordCalls;
    public void SendChord(InputChord chord) => Interlocked.Increment(ref ChordCalls);
    public void SetChord(InputChord chord, IReadOnlyList<int> retriggerKeys) => Interlocked.Increment(ref ChordCalls);
    public void ReleaseAll() { }
}

public sealed class UiSmokeAudioSink : IPreviewAudioSink
{
    public static UiSmokeAudioSink Instance { get; } = new();
    public int LastNoteCount;
    public int ChordCalls;
    public void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys)
    {
        Interlocked.Exchange(ref LastNoteCount, notes.Count);
        Interlocked.Increment(ref ChordCalls);
    }
    public void ReleaseAll() => Interlocked.Exchange(ref LastNoteCount, 0);
}

public static class RangeUiSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Checks = [];
    private static readonly List<string> Images = [];
    private static string OutputDirectory = "";
    private static string Phase = "";
    private static string Step = "initializing";
    private static MainWindow Window = null!;
    private static int Failed;
    private static string FixtureA => Path.Combine(OutputDirectory, "fixtures", "leading-and-trailing-silence.mid");
    private static string FixtureB => Path.Combine(OutputDirectory, "fixtures", "independent-second-song.mid");
    private static string DenseFixture => Path.Combine(OutputDirectory, "fixtures", "dense-note-markers.mid");
    private static string ShortFixture => Path.Combine(OutputDirectory, "fixtures", "sub-ten-millisecond-note.mid");
    private static string EmptyFixture => Path.Combine(OutputDirectory, "fixtures", "empty-note-track.mid");
    private static PlaybackTimeline Timeline => Control<PlaybackTimeline>("TimelineSlider");
    private static PlaybackEngine Player => Field<PlaybackEngine>("_player");

    public static async Task RunAsync(MainWindow window)
    {
        Window = window;
        OutputDirectory = Environment.GetEnvironmentVariable("DELTA_RANGE_SMOKE_OUTPUT") ?? throw new InvalidOperationException("Missing smoke output directory.");
        Phase = Environment.GetEnvironmentVariable("DELTA_RANGE_SMOKE_PHASE") ?? "edit";
        Exception? failure = null;
        try
        {
            await WaitForAsync(() => Field<bool>("_initialized"));
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            if (Phase == "edit") await EditPhaseAsync(); else await RestartPhaseAsync();
            Check("no-native-chord-input", UiSmokeInputSink.Instance.ChordCalls == 0, $"native chord calls={UiSmokeInputSink.Instance.ChordCalls}");
        }
        catch (Exception exception)
        {
            failure = exception;
            Check("unhandled-test-error", false, exception.ToString());
            try { await InvokeTaskAsync("StopPlaybackAsync"); } catch { }
        }
        finally
        {
            File.WriteAllText(Path.Combine(OutputDirectory, $"result-{Phase}.json"), JsonSerializer.Serialize(new
            {
                Passed = Failed == 0 && failure is null, Phase, Step, ProcessId = Environment.ProcessId,
                Executable = Environment.ProcessPath, SettingsDirectory = Field<SettingsStore>("_store").DirectoryPath,
                Input = "No-op recording input/audio sinks; global hotkeys disabled; two independent processes", Checks, Images,
                Error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
    }

    private static async Task EditPhaseAsync()
    {
        SetStep("load-fixtures");
        Directory.CreateDirectory(Path.GetDirectoryName(FixtureA)!);
        WriteMidi(FixtureA, 12, [(2, 2.5, 60), (3, 3.5, 62), (5, 8, 64)]);
        WriteMidi(FixtureB, 10, [(1, 1.5, 65), (3, 6, 67)]);
        // Keep each duration above one MIDI tick after rounding; zero-tick note-offs would
        // precede their note-ons and accidentally turn this into a sustained-note fixture.
        WriteMidi(DenseFixture, 10, Enumerable.Range(0, 4000).Select(i => (1 + i * .002, 1.0015 + i * .002, 60 + i % 8)).ToArray());
        WriteMidi(ShortFixture, 12, [(2, 2.003, 60)]);
        WriteMidi(EmptyFixture, 10, []);
        await InvokeTaskAsync("AddMidiPathsAsync", (object)new[] { FixtureA, FixtureB, DenseFixture, ShortFixture, EmptyFixture });
        await WaitForAsync(() => Field<PlaybackPlan?>("_plan") is not null);
        CheckRange("initial-full-range", 0, 12);

        SetStep("auto-trim-and-markers");
        InvokeVoid("AutoTrimRange_Click", Control<Button>("AutoTrimRangeButton"), new RoutedEventArgs());
        CheckRange("trim-leading-and-trailing-silence", 2, 8);
        var plan = Field<PlaybackPlan>("_plan");
        Check("trim-covers-final-sustained-note", Near(Player.EndPosition.TotalSeconds, plan.Chords.Max(c => c.End.TotalSeconds)), $"trim end={Player.EndPosition}; last chord end={plan.Chords.Max(c => c.End)}");
        Check("auto-trim-status-explains-range", Control<TextBlock>("RangeSummary").Text.Length > 0, Control<TextBlock>("RangeSummary").Text);
        await Task.Delay(125);
        await CaptureRootAsync("trim-and-note-markers.png");
        CheckTimelineGeometry("trim-marker-geometry");
        CheckNoteGeometry("note-markers-represent-playable-plan", 3);
        var noteData = TimelineField<XamlPath>("_allNotes").Data;
        var clipBefore = TimelineField<RectangleGeometry>("_selectedNoteClip").Rect;
        Timeline.Value = 4;
        Check("position-update-reuses-note-geometry", ReferenceEquals(noteData, TimelineField<XamlPath>("_allNotes").Data), "Playback cursor updates do not rebuild the note geometry.");

        SetStep("manual-range-and-mapping");
        EditRange(2.25, 7.75);
        CheckRange("manual-range-applied", 2.25, 7.75);
        Check("range-edit-reuses-note-geometry-and-updates-clip", ReferenceEquals(noteData, TimelineField<XamlPath>("_allNotes").Data) && TimelineField<RectangleGeometry>("_selectedNoteClip").Rect != clipBefore, "Range edits update the selected-note clip without rebuilding paths.");
        Control<Slider>("SpeedSlider").Value = 1.75;
        CheckRange("speed-keeps-absolute-range", 2.25, 7.75);
        Control<NumberBox>("TransposeBox").Value = 1;
        await Task.Delay(200);
        CheckRange("mapping-rebuild-keeps-range", 2.25, 7.75);
        Control<NumberBox>("TransposeBox").Value = 0;
        await Task.Delay(200);

        SetStep("playing-and-paused-edits");
        await InvokeTaskAsync("StartAsync", true, false, false);
        await WaitForAsync(() => Player.State == PlaybackState.Playing);
        var start = Handle("_startHandle");
        var end = Handle("_endHandle");
        Check("range-controls-disabled-playing", !Timeline.IsRangeEnabled && !start.IsEnabled && !end.IsEnabled && !Control<Button>("AutoTrimRangeButton").IsEnabled && !Control<Button>("ResetRangeButton").IsEnabled, $"rangeEnabled={Timeline.IsRangeEnabled}; start={start.IsEnabled}; end={end.IsEnabled}");
        start.SetValue(3);
        InvokeVoid("AutoTrimRange_Click", Control<Button>("AutoTrimRangeButton"), new RoutedEventArgs());
        CheckRange("active-range-edit-rejected", 2.25, 7.75);
        Player.Seek(TimeSpan.FromSeconds(7));
        await WaitForAsync(() => UiSmokeAudioSink.Instance.LastNoteCount > 0);
        Check("long-note-preview-remains-active-before-trim-end", UiSmokeAudioSink.Instance.LastNoteCount > 0 && Player.Position.TotalSeconds < 7.75, $"position={Player.Position}; active preview notes={UiSmokeAudioSink.Instance.LastNoteCount}");
        Player.Pause();
        await Task.Delay(100);
        Check("paused-range-controls-enabled", Player.State == PlaybackState.Paused && Timeline.IsRangeEnabled && start.IsEnabled && end.IsEnabled && Control<Button>("AutoTrimRangeButton").IsEnabled, $"state={Player.State}; rangeEnabled={Timeline.IsRangeEnabled}");
        EditRange(2.5, 7.5);
        CheckRange("paused-range-edit-applied", 2.5, 7.5);
        await InvokeTaskAsync("StopPlaybackAsync");
        Check("stop-returns-to-edited-start", Near(Player.Position.TotalSeconds, 2.5), $"position={Player.Position}");
        EditRange(2.25, 7.75);

        SetStep("independent-song-ranges");
        EditRange(2.125, 7.875);
        await SelectSongAsync(1, 10);
        CheckRange("second-song-initial-full-range", 0, 10);
        var flushed = Field<SettingsStore>("_store").Load();
        Check("switch-flushes-first-song-range", flushed.SongRanges.TryGetValue(FixtureA, out var firstSaved) && Near(firstSaved.StartSeconds, 2.125) && firstSaved.EndSeconds is { } firstEnd && Near(firstEnd, 7.875), "Selection change flushes the pending range write.");
        InvokeVoid("AutoTrimRange_Click", Control<Button>("AutoTrimRangeButton"), new RoutedEventArgs());
        CheckRange("second-song-auto-trim", 1, 6);
        EditRange(1.25, 5.75);
        await SelectSongAsync(0, 12);
        CheckRange("first-song-restores-own-range", 2.125, 7.875);
        EditRange(2.25, 7.75);
        await SelectSongAsync(1, 10);
        CheckRange("second-song-restores-own-range", 1.25, 5.75);
        InvokeVoid("ResetRange_Click", Control<Button>("ResetRangeButton"), new RoutedEventArgs());
        CheckRange("reset-restores-full-song", 0, 10);
        await SelectSongAsync(0, 12);
        var resetSaved = Field<SettingsStore>("_store").Load();
        Check("full-end-reset-persists-null-end", resetSaved.SongRanges.TryGetValue(FixtureB, out var fullSaved) && fullSaved.StartSeconds == 0 && fullSaved.EndSeconds is null, "Full-song end is saved as null to track the MIDI duration.");
        await SelectSongAsync(1, 10);
        CheckRange("reset-persists-on-switch", 0, 10);
        EditRange(1.25, 5.75);

        SetStep("dense-marker-layout");
        await SelectSongAsync(2, 10);
        await Task.Delay(200);
        Check("dense-fixture-has-thousands-of-playable-chords", Field<PlaybackPlan>("_plan").Chords.Count >= 3500, $"playable chords={Field<PlaybackPlan>("_plan").Chords.Count}");
        CheckTimelineGeometry("dense-timeline-arranged");
        CheckNoteGeometry("dense-notes-render-with-bounded-geometry", 4000);
        var denseData = TimelineField<XamlPath>("_allNotes").Data;
        Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 940));
        await Task.Delay(150);
        Check("resize-rebuilds-note-geometry", !ReferenceEquals(denseData, TimelineField<XamlPath>("_allNotes").Data), "Resizing changes the geometry scale.");
        CheckNoteGeometry("resized-note-geometry-matches-range", 4000);
        Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 940));
        await Task.Delay(150);
        await CaptureRootAsync("dense-note-markers.png");
        await SelectSongAsync(3, 12);
        InvokeVoid("AutoTrimRange_Click", Control<Button>("AutoTrimRangeButton"), new RoutedEventArgs());
        var shortStart = Handle("_startHandle");
        var shortEnd = Handle("_endHandle");
        Check("sub-ten-ms-range-keeps-valid-handle-bounds", Timeline.RangeEnd - Timeline.RangeStart is > 0 and < .01 && shortStart.CurrentValue <= shortStart.Maximum && shortStart.CurrentValue >= shortStart.Minimum && shortEnd.CurrentValue <= shortEnd.Maximum && shortEnd.CurrentValue >= shortEnd.Minimum, $"range={Timeline.RangeStart:F9}..{Timeline.RangeEnd:F9}; start bounds={shortStart.Minimum:F9}..{shortStart.Maximum:F9}; end bounds={shortEnd.Minimum:F9}..{shortEnd.Maximum:F9}");
        await SelectSongAsync(4, 10);
        Check("empty-plan-disables-auto-trim-and-clears-notes", !Control<Button>("AutoTrimRangeButton").IsEnabled && ((PathGeometry)TimelineField<XamlPath>("_allNotes").Data).Figures.Count == 0 && ((PathGeometry)TimelineField<XamlPath>("_allOnsets").Data).Figures.Count == 0, "Empty playable plans clear marker geometry and disable auto trim.");
        await SelectSongAsync(0, 12);
        CheckRange("first-range-after-dense-song", 2.25, 7.75);
        await Task.Delay(650);
        var settings = Field<SettingsStore>("_store").Load();
        Check("debounced-range-settings-written", settings.SongRanges.TryGetValue(FixtureA, out var saved) && Near(saved.StartSeconds, 2.25) && saved.EndSeconds is { } savedEnd && Near(savedEnd, 7.75), $"saved ranges={settings.SongRanges.Count}");
        InvokeVoid("ResetRangeControls");
        InvokeVoid("RebuildPlan", false);
        CheckRange("rebuild-after-reset-restores-remembered-range", 2.25, 7.75);
        await CaptureRootAsync("edited-ranges-before-restart.png");
        SetStep("close-flushes-pending-edit");
        EditRange(2.375, 7.625);
        CheckRange("last-edit-before-close", 2.375, 7.625);
        // No delay: Window.Close must flush this edit before the 350 ms debounce.
    }

    private static async Task RestartPhaseAsync()
    {
        SetStep("reload-persisted-playlist");
        await WaitForAsync(() => Control<ListView>("PlaylistList").Items.Count == 5 && Field<PlaybackPlan?>("_plan") is not null);
        CheckRange("fresh-process-restores-close-flushed-first-range", 2.375, 7.625);
        Check("fresh-process-restores-speed", Near(Player.Speed, 1.75), $"speed={Player.Speed}");
        await SelectSongAsync(1, 10);
        CheckRange("fresh-process-restores-second-range", 1.25, 5.75);
        await SelectSongAsync(0, 12);
        CheckRange("restart-switch-keeps-first-range", 2.375, 7.625);
        await Task.Delay(100);
        CheckTimelineGeometry("restarted-timeline-arranged");
        await CaptureRootAsync("ranges-after-real-process-restart.png");
    }

    private static void EditRange(double start, double end)
    {
        // Use the real handle event path; SetRange alone is programmatic synchronization.
        Handle("_endHandle").SetValue(end);
        Handle("_startHandle").SetValue(start);
    }
    private static TimelineHandle Handle(string name) => (TimelineHandle)typeof(PlaybackTimeline).GetField(name, PrivateInstance)!.GetValue(Timeline)!;
    private static T TimelineField<T>(string name) => (T)typeof(PlaybackTimeline).GetField(name, PrivateInstance)!.GetValue(Timeline)!;
    private static async Task SelectSongAsync(int index, double duration)
    {
        Control<ListView>("PlaylistList").SelectedIndex = index;
        await WaitForAsync(() => Control<ListView>("PlaylistList").SelectedIndex == index && !Field<bool>("_switchingSong") && Field<string?>("_planSongPath") == ((SongEntry)Control<ListView>("PlaylistList").SelectedItem).Path && Near(Player.Duration.TotalSeconds, duration));
        await Task.Delay(100);
    }
    private static void CheckRange(string name, double start, double end) => Check(name,
        Near(Timeline.RangeStart, start) && Near(Timeline.RangeEnd, end) && Near(Player.StartPosition.TotalSeconds, start) && Near(Player.EndPosition.TotalSeconds, end),
        $"timeline={Timeline.RangeStart:F6}..{Timeline.RangeEnd:F6}; engine={Player.StartPosition.TotalSeconds:F6}..{Player.EndPosition.TotalSeconds:F6}; expected={start:F6}..{end:F6}");
    private static void CheckTimelineGeometry(string name)
    {
        var root = Control<Grid>("RootGrid");
        root.UpdateLayout();
        var origin = Timeline.TransformToVisual(root).TransformPoint(new());
        Check(name, Timeline.ActualWidth > 100 && Timeline.ActualHeight >= 62 && origin.X >= 0 && origin.X + Timeline.ActualWidth <= root.ActualWidth + 1,
            $"timeline={Timeline.ActualWidth:F1}x{Timeline.ActualHeight:F1}; origin={origin.X:F1},{origin.Y:F1}; root={root.ActualWidth:F1}x{root.ActualHeight:F1}");
    }
    private static void CheckNoteGeometry(string name, int sourceNoteCount)
    {
        var all = TimelineField<XamlPath>("_allNotes");
        var selected = TimelineField<XamlPath>("_selectedNotes");
        var onsets = TimelineField<XamlPath>("_allOnsets");
        var selectedOnsets = TimelineField<XamlPath>("_selectedOnsets");
        var notes = (PathGeometry)all.Data;
        var starts = (PathGeometry)onsets.Data;
        var width = Timeline.ActualWidth - 68;
        var clip = TimelineField<RectangleGeometry>("_selectedNoteClip").Rect;
        var expectedStart = 34 + Timeline.RangeStart / Timeline.Maximum * width;
        var expectedWidth = (Timeline.RangeEnd - Timeline.RangeStart) / Timeline.Maximum * width;
        Check(name, notes.Figures.Count > 0 && starts.Figures.Count > 0 && notes.Figures.Count <= 8192 && starts.Figures.Count <= Math.Min(8192, Math.Ceiling(width)) && !ReferenceEquals(all.Data, selected.Data) && !ReferenceEquals(onsets.Data, selectedOnsets.Data) && SameGeometry(notes, (PathGeometry)selected.Data) && SameGeometry(starts, (PathGeometry)selectedOnsets.Data) && !ReferenceEquals(selected.Clip, selectedOnsets.Clip) && selected.Clip.Rect == selectedOnsets.Clip.Rect && !all.IsHitTestVisible && !selected.IsHitTestVisible && !onsets.IsHitTestVisible && !selectedOnsets.IsHitTestVisible && Math.Abs(clip.X - expectedStart) < .01 && Math.Abs(clip.Width - expectedWidth) < .01,
            $"source notes={sourceNoteCount}; duration figures={notes.Figures.Count}; onset figures={starts.Figures.Count}; rail width={width:F2}; selection clip={clip}");
    }
    private static bool SameGeometry(PathGeometry first, PathGeometry second) => first.Figures.Count == second.Figures.Count && first.Figures.Zip(second.Figures).All(pair => pair.First.StartPoint == pair.Second.StartPoint && pair.First.Segments.Count == pair.Second.Segments.Count && pair.First.Segments.Zip(pair.Second.Segments).All(segments => ((LineSegment)segments.First).Point == ((LineSegment)segments.Second).Point));
    private static async Task CaptureRootAsync(string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Control<Grid>("RootGrid"));
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[checked((int)pixels.Length)];
        using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var pngReader = new DataReader(stream.GetInputStreamAt(0));
        await pngReader.LoadAsync(checked((uint)stream.Size));
        var png = new byte[checked((int)stream.Size)];
        pngReader.ReadBytes(png);
        var path = Path.Combine(OutputDirectory, name);
        await File.WriteAllBytesAsync(path, png);
        Images.Add(path);
    }
    private static void WriteMidi(string path, double duration, IReadOnlyList<(double Start, double End, int Pitch)> notes)
    {
        var events = notes.SelectMany(n => new[] { (Tick: (int)Math.Round(n.Start * 960), On: true, n.Pitch), (Tick: (int)Math.Round(n.End * 960), On: false, n.Pitch) }).OrderBy(e => e.Tick).ThenBy(e => e.On).ToArray();
        using var track = new MemoryStream();
        int previous = 0;
        foreach (var item in events)
        {
            WriteVariable(track, item.Tick - previous); previous = item.Tick;
            track.Write([(byte)(item.On ? 0x90 : 0x80), (byte)item.Pitch, (byte)(item.On ? 96 : 0)]);
        }
        WriteVariable(track, (int)Math.Round(duration * 960) - previous);
        track.Write([0xff, 0x2f, 0]);
        using var midi = File.Create(path);
        midi.Write([0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xe0, 0x4d, 0x54, 0x72, 0x6b]);
        int length = checked((int)track.Length);
        midi.Write([(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        track.Position = 0; track.CopyTo(midi);
    }
    private static void WriteVariable(Stream stream, int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7f) };
        while ((value >>= 7) > 0) bytes.Insert(0, (byte)((value & 0x7f) | 0x80));
        stream.Write(bytes.ToArray());
    }
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .00001;
    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out at {Step} after {timeoutMs} ms.");
            await Task.Delay(25);
        }
    }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(Window) ?? (object?)default(T))!;
    private static T Control<T>(string name) => Field<T>(name);
    private static async Task InvokeTaskAsync(string name, params object?[] arguments) => await (Task)typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, arguments)!;
    private static void InvokeVoid(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, arguments);
    private static void Check(string name, bool passed, string detail)
    {
        Checks.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) Failed++;
        WriteProgress();
    }
    private static void SetStep(string name) { Step = name; WriteProgress(); }
    private static void WriteProgress() => File.WriteAllText(Path.Combine(OutputDirectory, $"progress-{Phase}.json"), JsonSerializer.Serialize(new { Phase, Step, ProcessId = Environment.ProcessId, Checks = Checks.Count, Failed }, new JsonSerializerOptions { WriteIndented = true }));
}
