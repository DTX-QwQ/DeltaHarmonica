using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaHarmonica.App.Controls;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DeltaHarmonica.App;

// Compiled only into an isolated copy of the app. The real UI and playback engine run,
// while native game input, audible preview, and global hotkeys are disabled.
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
    public int ChordCalls;
    public void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys) => Interlocked.Increment(ref ChordCalls);
    public void ReleaseAll() { }
}

public static class LibraryUiSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string RenamedTitle = "星河夜曲 · 收藏演奏版";
    private static readonly List<object> Checks = [];
    private static readonly List<string> Images = [];
    private static MainWindow Window = null!;
    private static string OutputDirectory = "";
    private static string Phase = "";
    private static string Step = "initializing";
    private static int Failed;
    private static string FixtureA => System.IO.Path.Combine(OutputDirectory, "fixtures", "first-song.mid");
    private static string FixtureB => System.IO.Path.Combine(OutputDirectory, "fixtures", "second-song.mid");
    private static string FixtureC => System.IO.Path.Combine(OutputDirectory, "fixtures", "third-song.mid");
    private static ObservableCollection<SongEntry> Songs => Field<ObservableCollection<SongEntry>>("_songs");
    private static List<SongCategory> Categories => Field<List<SongCategory>>("_categories");
    private static Dictionary<string, ListView> Lists => Field<Dictionary<string, ListView>>("_categoryLists");
    private static Dictionary<string, Expander> Expanders => Field<Dictionary<string, Expander>>("_categoryExpanders");
    private static SongEntry? Selected => Field<SongEntry?>("_selectedSong");
    private static PlaybackEngine Player => Field<PlaybackEngine>("_player");
    private static PlaybackTimeline Timeline => Field<PlaybackTimeline>("TimelineSlider");

    public static async Task RunAsync(MainWindow window)
    {
        Window = window;
        OutputDirectory = Environment.GetEnvironmentVariable("DELTA_LIBRARY_SMOKE_OUTPUT") ?? throw new InvalidOperationException("Missing smoke output directory.");
        Phase = Environment.GetEnvironmentVariable("DELTA_LIBRARY_SMOKE_PHASE") ?? "edit";
        if (Phase == "interactive")
        {
            await WaitForAsync(() => Field<bool>("_initialized") && !Field<bool>("_restoringLibrary"));
            void Log(string message) => File.AppendAllText(System.IO.Path.Combine(OutputDirectory, "interactive-events.log"), $"{DateTimeOffset.Now:O} {message}\n");
            var root = Field<Grid>("RootGrid");
            root.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
                Log($"released order={DescribeOrder()}; title={Selected?.DisplayName}; state={Player.State}; position={Player.Position}")), true);
            File.WriteAllText(System.IO.Path.Combine(OutputDirectory, "interactive-ready.json"), JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Songs = Songs.Count, SettingsDirectory = Field<SettingsStore>("_store").DirectoryPath }));
            return;
        }
        Exception? failure = null;
        try
        {
            await WaitForAsync(() => Field<bool>("_initialized") && !Field<bool>("_restoringLibrary"));
            Field<CheckBox>("AutoNextCheck").IsChecked = false;
            if (Phase == "edit") await EditPhaseAsync(); else await RestartPhaseAsync();
            Check("no-native-game-input", UiSmokeInputSink.Instance.ChordCalls == 0, $"Native chord calls={UiSmokeInputSink.Instance.ChordCalls}");
        }
        catch (Exception exception)
        {
            failure = exception;
            Check("unhandled-test-error", false, exception.ToString());
            try { await InvokeTaskAsync("StopPlaybackAsync"); } catch { }
        }
        finally
        {
            File.WriteAllText(System.IO.Path.Combine(OutputDirectory, $"result-{Phase}.json"), JsonSerializer.Serialize(new
            {
                Passed = Failed == 0 && failure is null, Phase, Step, ProcessId = Environment.ProcessId,
                Executable = Environment.ProcessPath, SettingsDirectory = Field<SettingsStore>("_store").DirectoryPath,
                Scope = "Real WinUI controls, layout geometry, engine, isolated store, and process restart. In-window pointer dragging is verified separately; this harness checks shared song/category movement, ordering and persistence logic.",
                Checks, Images, Error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
        }
    }

    private static async Task EditPhaseAsync()
    {
        SetStep("fixtures-and-empty-state");
        Check("rename-disabled-without-song", !Field<Button>("RenameSongButton").IsEnabled, "The rename button is unavailable when the library is empty.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FixtureA)!);
        WriteMidi(FixtureA, 30, [(1, 29, 60), (3, 4, 64)]);
        WriteMidi(FixtureB, 25, [(2, 23, 62)]);
        WriteMidi(FixtureC, 20, [(1, 18, 65)]);
        await InvokeTaskAsync("AddMidiPathsAsync", (object)new[] { FixtureA, FixtureB, FixtureC });
        await WaitForAsync(() => Selected?.Path == FixtureA && Field<PlaybackPlan?>("_plan") is not null);
        var a = Songs.Single(song => song.Path == FixtureA);
        var b = Songs.Single(song => song.Path == FixtureB);
        var c = Songs.Single(song => song.Path == FixtureC);
        var originalSong = a.Song;
        var originalHash = SHA256.HashData(File.ReadAllBytes(FixtureA));
        var defaultCategory = Categories.Single(category => category.Id == "");
        Check("import-uses-default-category", defaultCategory.Songs.SequenceEqual(new[] { a, b, c }), "Imported songs start in 未分类 in import order.");
        Check("elevation-compatible-drag-controls", Lists.Values.All(list => !list.CanDragItems && !list.CanReorderItems) && Field<Canvas>("LibraryDragOverlay") is not null, "Row handles use pointer capture without the elevated-app limitation of system drag/drop.");
        Invoke("AutoTrimRange_Click", Field<Button>("AutoTrimRangeButton"), new RoutedEventArgs());
        var plan = Field<PlaybackPlan>("_plan");

        SetStep("rename-title-and-data");
        var renamed = (bool)Invoke("RenameSong", a, "  " + RenamedTitle + "  ")!;
        await Task.Delay(75);
        Check("rename-updates-now-playing-and-library", renamed && a.DisplayName == RenamedTitle && Field<TextBlock>("SongTitle").Text == RenamedTitle, $"entry={a.DisplayName}; title={Field<TextBlock>("SongTitle").Text}");
        Field<Grid>("RootGrid").UpdateLayout();
        Check("rename-updates-rendered-library-text", Lists[""].ContainerFromItem(a) is ListViewItem item && Descendants(item).OfType<TextBlock>().Any(label => label.Text == RenamedTitle), "The visible ListView item updates its text binding after renaming.");
        Check("rename-preserves-midi-and-plan", a.Path == FixtureA && ReferenceEquals(a.Song, originalSong) && ReferenceEquals(Field<PlaybackPlan>("_plan"), plan) && SHA256.HashData(File.ReadAllBytes(FixtureA)).SequenceEqual(originalHash), "Renaming preserves the file, parsed MIDI object, and loaded playback plan.");
        Check("rename-preserves-song-range", Near(Timeline.RangeStart, 1) && Near(Timeline.RangeEnd, 29), $"range={Timeline.RangeStart}..{Timeline.RangeEnd}");
        var rejected = !(bool)Invoke("RenameSong", a, "   ")!;
        Check("blank-rename-rejected", rejected && a.DisplayName == RenamedTitle, "A blank title leaves the current song name intact.");
        CheckRenameButtonGeometry();

        SetStep("create-categories");
        var nocturnes = (SongCategory?)Invoke("CreateCategory", "夜曲收藏");
        var empty = (SongCategory?)Invoke("CreateCategory", "待练习");
        Check("category-creation-visible", nocturnes is not null && empty is not null && Categories.Count == 3 && Expanders.ContainsKey(nocturnes!.Id) && Lists.ContainsKey(empty!.Id), "Two user categories have rendered expanders and list controls.");
        if (nocturnes is null || empty is null) throw new InvalidOperationException("Category creation failed.");
        Check("category-headers-show-names", Categories.All(category => ((Grid)((Border)Expanders[category.Id].Header).Child).Children.OfType<TextBlock>().Any(label => label.Text.Contains(category.Name))), "Each expander header includes its category name.");
        Check("empty-category-has-drop-target", Expanders[empty.Id].Header is Border, "An empty category header is available to the shared pointer hit testing.");

        SetStep("reorder-during-preview");
        await InvokeTaskAsync("StartAsync", true, false, false);
        await WaitForAsync(() => Player.State == PlaybackState.Playing && UiSmokeAudioSink.Instance.ChordCalls > 0);
        var position = Player.Position;
        Invoke("MoveSong", c, defaultCategory, 0);
        await Task.Delay(100);
        Check("within-category-reorder-updates-playlist", Songs.SequenceEqual(new[] { c, a, b }), DescribeOrder());
        CheckPlaybackUnchanged("reorder-retains-active-preview", a, plan, position);

        SetStep("cross-category-move-and-selection");
        position = Player.Position;
        Invoke("MoveSong", a, nocturnes, 0);
        Check("cross-category-move-retains-song", a.CategoryId == nocturnes.Id && nocturnes.Songs.SequenceEqual(new[] { a }) && defaultCategory.Songs.SequenceEqual(new[] { c, b }), DescribeOrder());
        CheckPlaybackUnchanged("cross-category-move-retains-active-preview", a, plan, position);
        CheckSelection("cross-category-move-keeps-one-selection", a, nocturnes.Id);
        Invoke("MoveSong", b, nocturnes, 0);
        Invoke("MoveSong", a, nocturnes, 0);
        Check("move-insertion-position-applied", nocturnes.Songs.SequenceEqual(new[] { a, b }) && Songs.SequenceEqual(new[] { c, a, b }), DescribeOrder());
        Invoke("MoveSong", a, nocturnes, 2);
        Check("same-category-forward-insertion", nocturnes.Songs.SequenceEqual(new[] { b, a }), "Moving an item forward uses the insertion slot before the source removal.");
        Invoke("MoveSong", a, nocturnes, 0);
        CheckPlaybackUnchanged("repeated-moves-retain-active-preview", a, plan, position);
        Expanders[nocturnes.Id].IsExpanded = false;
        Expanders[empty.Id].IsExpanded = false;
        await Task.Delay(100);
        Check("category-collapses", !Expanders[nocturnes.Id].IsExpanded && !Expanders[empty.Id].IsExpanded, "Populated and empty categories can both collapse.");

        SetStep("category-order-during-preview");
        position = Player.Position;
        Invoke("MoveCategory", nocturnes, 0);
        Invoke("MoveCategory", empty, 0);
        await Task.Delay(100);
        Check("category-reorder-updates-visible-and-playback-order", Categories.SequenceEqual(new[] { empty, nocturnes, defaultCategory }) &&
            Field<StackPanel>("LibraryGroups").Children.SequenceEqual(Categories.Select(category => Expanders[category.Id])) && Songs.SequenceEqual(new[] { a, b, c }), DescribeOrder());
        CheckPlaybackUnchanged("category-reorder-retains-active-preview", a, plan, position);
        CheckSelection("category-reorder-retains-selection", a, nocturnes.Id);
        Check("category-reorder-retains-collapse", !Expanders[nocturnes.Id].IsExpanded && !Expanders[empty.Id].IsExpanded, "Moving populated and empty categories preserves their collapsed state.");
        Invoke("MoveCategory", empty, 3);
        Check("category-forward-insertion", Categories.SequenceEqual(new[] { nocturnes, defaultCategory, empty }), "Forward category moves use the insertion boundary before source removal.");
        Invoke("MoveCategory", empty, 0);
        Invoke("MoveCategory", empty, 1);
        Check("category-adjacent-insertion-is-noop", Categories.SequenceEqual(new[] { empty, nocturnes, defaultCategory }), "Dropping immediately after the same category preserves its position.");
        CheckPlaybackUnchanged("repeated-category-moves-retain-active-preview", a, plan, position);
        await InvokeTaskAsync("StopPlaybackAsync");
        CheckCategoryMenuGeometry("category-menu-desktop");

        SetStep("collapsed-category-playback-order");
        await InvokeTaskAsync("SelectSongAsync", c);
        await InvokeTaskAsync("ChangeSongAsync", 1, false, false);
        Check("next-enters-collapsed-category", ReferenceEquals(Selected, a) && Field<TextBlock>("SongTitle").Text == RenamedTitle, DescribeOrder());
        await InvokeTaskAsync("ChangeSongAsync", 1, false, false);
        Check("next-follows-category-item-order", ReferenceEquals(Selected, b), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("ChangeSongAsync", -1, false, false);
        Check("previous-follows-category-item-order", ReferenceEquals(Selected, a), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("ChangeSongAsync", -1, false, false);
        Check("previous-exits-collapsed-category", ReferenceEquals(Selected, c), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("SelectSongAsync", a);
        await CheckTransposeBehaviorAsync(a);
        // Selection may expand its group for visibility. Persist an explicit final user collapse.
        Expanders[nocturnes.Id].IsExpanded = false;
        Expanders[empty.Id].IsExpanded = false;
        Field<Slider>("SpeedSlider").Value = 1.75;
        await Task.Delay(100);
        CheckSpeedScale("speed-scale-initial-layout");
        await CaptureRootAsync("library-collapsed-before-restart.png");
        Field<StackPanel>("TransposeControls").StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
        await Task.Delay(300);
        CheckTransposeGeometry("transpose-desktop-layout");
        await CaptureRootAsync("speed-scale-visible.png");
        Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 940));
        await Task.Delay(150);
        Field<StackPanel>("TransposeControls").StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
        await Task.Delay(150);
        CheckSpeedScale("speed-scale-resized-layout");
        CheckTransposeGeometry("transpose-narrow-layout");
        CheckCategoryMenuGeometry("category-menu-narrow");
        await CaptureRootAsync("library-narrow-window.png");
        Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 940));
        await Task.Delay(100);
        Field<TextBlock>("SongTitle").StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        Expanders[nocturnes.Id].IsExpanded = true;
        await InvokeTaskAsync("SelectSongAsync", a);
        await Task.Delay(300);
        CheckSelection("selection-after-return-to-category", a, nocturnes.Id);
        await CaptureRootAsync("library-expanded-renamed-song.png");
        Expanders[nocturnes.Id].IsExpanded = false;
        Invoke("SaveSettings");
        var saved = Field<SettingsStore>("_store").Load();
        Check("saved-playlist-matches-visible-order", saved.Playlist.SequenceEqual(new[] { FixtureA, FixtureB, FixtureC }), DescribeOrder());
        Check("saved-category-order-includes-uncategorized", saved.UncategorizedIndex == 2 && saved.LibraryCategories.Select(category => category.Id).SequenceEqual(new[] { empty.Id, nocturnes.Id }), $"uncategorized index={saved.UncategorizedIndex}");
        SetStep("close-and-restart");
    }

    private static async Task RestartPhaseAsync()
    {
        SetStep("reload-library-after-real-process-restart");
        await WaitForAsync(() => Songs.Count == 3 && Field<PlaybackPlan?>("_plan") is not null);
        var a = Songs.Single(song => song.Path == FixtureA);
        var b = Songs.Single(song => song.Path == FixtureB);
        var c = Songs.Single(song => song.Path == FixtureC);
        var nocturnes = Categories.Single(category => category.Name == "夜曲收藏");
        var empty = Categories.Single(category => category.Name == "待练习");
        Check("restart-restores-library-order", Songs.SequenceEqual(new[] { a, b, c }), DescribeOrder());
        Check("restart-restores-category-order", Categories.Select(category => category.Id).SequenceEqual(new[] { empty.Id, nocturnes.Id, "" }) &&
            Field<StackPanel>("LibraryGroups").Children.SequenceEqual(Categories.Select(category => Expanders[category.Id])), string.Join(" -> ", Categories.Select(category => category.Name)));
        Check("restart-restores-category-membership", a.CategoryId == nocturnes.Id && b.CategoryId == nocturnes.Id && c.CategoryId == "" && nocturnes.Songs.SequenceEqual(new[] { a, b }) && empty.Songs.Count == 0, $"category={nocturnes.Name}; songs={nocturnes.Songs.Count}; empty={empty.Songs.Count}");
        Check("restart-restores-category-collapse", !Expanders[nocturnes.Id].IsExpanded && !Expanders[empty.Id].IsExpanded, "Both category collapse states survived a new process.");
        Check("restart-restores-renamed-title", a.DisplayName == RenamedTitle, a.DisplayName);
        Check("restart-selects-first-ordered-song", ReferenceEquals(Selected, a), $"selected={Selected?.Path}");
        Check("restart-restores-speed", Near(Player.Speed, 1.75), $"speed={Player.Speed}");
        Check("restart-restores-transpose", Near(Field<NumberBox>("TransposeBox").Value, 2) && PlanUsesTranspose(2), $"transpose={Field<NumberBox>("TransposeBox").Value}");
        await InvokeTaskAsync("ChangeSongAsync", 1, false, false);
        Check("restart-next-follows-saved-order", ReferenceEquals(Selected, b), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("ChangeSongAsync", 1, false, false);
        Check("restart-next-enters-moved-uncategorized", ReferenceEquals(Selected, c), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("ChangeSongAsync", -1, false, false);
        Check("restart-previous-enters-reordered-category", ReferenceEquals(Selected, b), $"selected={Selected?.DisplayName}");
        await InvokeTaskAsync("ChangeSongAsync", -1, false, false);
        Check("restart-previous-follows-saved-order", ReferenceEquals(Selected, a) && Field<TextBlock>("SongTitle").Text == RenamedTitle, $"selected={Selected?.DisplayName}");
        Check("restart-rename-and-move-preserve-range", Near(Timeline.RangeStart, 1) && Near(Timeline.RangeEnd, 29), $"range={Timeline.RangeStart}..{Timeline.RangeEnd}");
        await Task.Delay(100);
        CheckSpeedScale("speed-scale-after-restart");
        await CaptureRootAsync("library-after-real-process-restart.png");
    }

    private static void CheckPlaybackUnchanged(string name, SongEntry expected, PlaybackPlan plan, TimeSpan position) =>
        Check(name, ReferenceEquals(Selected, expected) && ReferenceEquals(Field<PlaybackPlan>("_plan"), plan) && Player.State == PlaybackState.Playing && Player.Position >= position && Near(Player.StartPosition.TotalSeconds, 1) && Near(Player.EndPosition.TotalSeconds, 29),
            $"state={Player.State}; position={Player.Position}; preceding position={position}; selected={Selected?.DisplayName}; range={Player.StartPosition}..{Player.EndPosition}");

    private static void CheckSelection(string name, SongEntry expected, string categoryId) =>
        Check(name, ReferenceEquals(Selected, expected) && ReferenceEquals(Lists[categoryId].SelectedItem, expected) && Lists.Values.Sum(list => list.SelectedItems.Count) == 1,
            $"selected={Selected?.DisplayName}; selected list items={Lists.Values.Sum(list => list.SelectedItems.Count)}");

    private static void CheckRenameButtonGeometry()
    {
        var root = Field<Grid>("RootGrid");
        root.UpdateLayout();
        var button = Field<Button>("RenameSongButton");
        var title = Field<TextBlock>("SongTitle");
        var buttonOrigin = button.TransformToVisual(root).TransformPoint(new());
        var titleOrigin = title.TransformToVisual(root).TransformPoint(new());
        Check("rename-button-below-title", button.IsEnabled && button.ActualWidth > 0 && buttonOrigin.Y >= titleOrigin.Y + title.ActualHeight - 1 && buttonOrigin.X > titleOrigin.X,
            $"title origin={titleOrigin}; title size={title.ActualWidth}x{title.ActualHeight}; button origin={buttonOrigin}; button width={button.ActualWidth}");
    }

    private static void CheckSpeedScale(string name)
    {
        Field<Grid>("RootGrid").UpdateLayout();
        var scale = Field<Canvas>("SpeedScaleCanvas");
        var labels = scale.Children.OfType<TextBlock>().ToArray();
        var slider = Field<Slider>("SpeedSlider");
        var text = string.Join(", ", labels.Select(label => label.Text));
        Check(name, scale.ActualWidth > 100 && Near(slider.TickFrequency, .25) && slider.TickPlacement.ToString() == "BottomRight" && Near(slider.StepFrequency, .05) && labels.Select(label => (double)label.Tag).SequenceEqual(new[] { .25, 1, 1.5, 2, 2.5, 3 }) && labels.All(label => Canvas.GetLeft(label) >= -1 && Canvas.GetLeft(label) + label.ActualWidth <= scale.ActualWidth + 1),
            $"scale width={scale.ActualWidth:F1}; tick frequency={slider.TickFrequency}; tick placement={slider.TickPlacement}; step={slider.StepFrequency}; labels={text}; label bounds=" + string.Join("; ", labels.Select(label => $"{Canvas.GetLeft(label):F1}..{Canvas.GetLeft(label) + label.ActualWidth:F1}")));
    }

    private static async Task CheckTransposeBehaviorAsync(SongEntry song)
    {
        SetStep("transpose-controls-and-playback-mapping");
        var box = Field<NumberBox>("TransposeBox");
        var down = Field<Button>("TransposeDownButton");
        var up = Field<Button>("TransposeUpButton");
        var reset = Field<Button>("ResetTransposeButton");
        Check("transpose-original-key-state", Near(box.Value, 0) && down.IsEnabled && up.IsEnabled && !reset.IsEnabled && Field<TextBlock>("TransposeHint").Text == "原调 · 不移调", $"value={box.Value}; hint={Field<TextBlock>("TransposeHint").Text}");
        var originalPlan = Field<PlaybackPlan>("_plan");
        await InvokeTaskAsync("StartAsync", true, false, false);
        await WaitForAsync(() => Player.State == PlaybackState.Playing);
        Invoke("TransposeStep_Click", up, new RoutedEventArgs());
        await WaitForAsync(() => Player.State == PlaybackState.Stopped && PlanUsesTranspose(1));
        Check("transpose-step-stops-preview-and-rebuilds-mapping", Near(box.Value, 1) && !ReferenceEquals(originalPlan, Field<PlaybackPlan>("_plan")) && ReferenceEquals(Selected, song) &&
            Near(Timeline.RangeStart, 1) && Near(Timeline.RangeEnd, 29) && Field<TextBlock>("TransposeHint").Text == "升高 1 半音", $"state={Player.State}; value={box.Value}; range={Timeline.RangeStart}..{Timeline.RangeEnd}");
        Invoke("TransposeStep_Click", down, new RoutedEventArgs());
        await WaitForAsync(() => PlanUsesTranspose(0));
        Check("transpose-decrement-restores-original-pitches", Near(box.Value, 0) && !reset.IsEnabled, $"value={box.Value}");
        box.Value = 36;
        await WaitForAsync(() => PlanUsesTranspose(36));
        Check("transpose-upper-limit", !up.IsEnabled && down.IsEnabled && reset.IsEnabled && Field<TextBlock>("TransposeHint").Text == "升高 36 半音", $"value={box.Value}; up enabled={up.IsEnabled}");
        Invoke("TransposeStep_Click", up, new RoutedEventArgs());
        Check("transpose-cannot-step-above-limit", Near(box.Value, 36), $"value={box.Value}");
        box.Value = -36;
        await WaitForAsync(() => PlanUsesTranspose(-36));
        Check("transpose-lower-limit", !down.IsEnabled && up.IsEnabled && reset.IsEnabled && Field<TextBlock>("TransposeHint").Text == "降低 36 半音", $"value={box.Value}; down enabled={down.IsEnabled}");
        Invoke("TransposeStep_Click", down, new RoutedEventArgs());
        Check("transpose-cannot-step-below-limit", Near(box.Value, -36), $"value={box.Value}");
        Invoke("ResetTranspose_Click", reset, new RoutedEventArgs());
        await WaitForAsync(() => PlanUsesTranspose(0));
        Check("transpose-reset-restores-original-key", Near(box.Value, 0) && !reset.IsEnabled && Field<TextBlock>("TransposeHint").Text == "原调 · 不移调", $"value={box.Value}");
        box.Value = double.NaN;
        await WaitForAsync(() => Near(box.Value, 0) && PlanUsesTranspose(0));
        Check("transpose-empty-value-normalized", double.IsFinite(box.Value) && Near(box.Value, 0), $"value={box.Value}");
        box.Value = 1.6;
        await WaitForAsync(() => Near(box.Value, 2) && PlanUsesTranspose(2));
        Check("transpose-integer-value-and-persistence", Field<SettingsStore>("_store").Load().Transpose == 2 && Field<TextBlock>("TransposeHint").Text == "升高 2 半音", $"value={box.Value}; saved={Field<SettingsStore>("_store").Load().Transpose}");
    }

    private static bool PlanUsesTranspose(int semitones)
    {
        var notes = Field<PlaybackPlan?>("_plan")?.Chords.SelectMany(chord => chord.Notes).ToArray();
        return notes is { Length: > 0 } && notes.All(note => note.TransposedPitch - note.SourcePitch == semitones);
    }

    private static void CheckCategoryMenuGeometry(string name)
    {
        var root = Field<Grid>("RootGrid");
        root.UpdateLayout();
        foreach (var category in Categories)
        {
            var expander = Expanders[category.Id];
            var header = (FrameworkElement)expander.Header;
            var more = Descendants(header).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "分类操作");
            var icon = more.Content as FontIcon;
            var menu = more.Flyout as MenuFlyout;
            var headerSlotFits = true;
            for (DependencyObject? parent = header; parent is not null && !ReferenceEquals(parent, expander); parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } ancestor) headerSlotFits &= ContainsBounds(more, ancestor);
            Check($"{name}-{category.Name}", more.ActualWidth >= 28 && more.ActualHeight >= 28 && icon is not null && icon.ActualWidth > 0 && icon.ActualHeight > 0 &&
                headerSlotFits && ContainsBounds(more, header) && ContainsBounds(more, expander) && ContainsBounds(icon, more) &&
                ContainsBounds(more, Field<ScrollViewer>("LibraryScrollViewer")) && menu is not null && menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "上移分类") && menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "下移分类"),
                $"category={category.Name}; header={Bounds(header, root)}; button={Bounds(more, root)}; icon={(icon is null ? "missing" : Bounds(icon, more).ToString())}");
        }
    }

    private static void CheckTransposeGeometry(string name)
    {
        var root = Field<Grid>("RootGrid");
        root.UpdateLayout();
        var controls = Field<StackPanel>("TransposeControls");
        var stepper = Field<Grid>("TransposeStepper");
        var slider = Field<Slider>("SpeedSlider");
        var box = Field<NumberBox>("TransposeBox");
        var down = Field<Button>("TransposeDownButton");
        var up = Field<Button>("TransposeUpButton");
        var reset = Field<Button>("ResetTransposeButton");
        var hint = Field<TextBlock>("TransposeHint");
        var title = Descendants(controls).OfType<TextBlock>().Single(label => label.Text == "整体移调");
        var unit = Descendants(controls).OfType<TextBlock>().Single(label => label.Text == "半音");
        var elements = new FrameworkElement[] { stepper, box, down, up, reset, hint, title, unit };
        var downBounds = Bounds(down, stepper);
        var boxBounds = Bounds(box, stepper);
        var upBounds = Bounds(up, stepper);
        Check(name, Bounds(controls, root).Left >= Bounds(slider, root).Right + 10 && box.SpinButtonPlacementMode == NumberBoxSpinButtonPlacementMode.Hidden &&
            down.ActualWidth >= 32 && up.ActualWidth >= 32 && box.ActualWidth >= 64 && box.ActualHeight >= 32 &&
            downBounds.Right <= boxBounds.Left && boxBounds.Right <= upBounds.Left &&
            elements.All(element => element.ActualWidth > 0 && element.ActualHeight > 0 && ContainsBounds(element, controls)) && ContainsBounds(controls, root) &&
            !hint.IsTextTrimmed && !title.IsTextTrimmed && !unit.IsTextTrimmed && ContainsBounds((FrameworkElement)down.Content, down) && ContainsBounds((FrameworkElement)up.Content, up),
            $"window={root.ActualWidth:F1}x{root.ActualHeight:F1}; speed={Bounds(slider, root)}; controls={Bounds(controls, root)}; down={downBounds}; box={boxBounds}; up={upBounds}; hint={hint.Text}");
    }

    private static Rect Bounds(FrameworkElement element, UIElement relativeTo)
    {
        var origin = element.TransformToVisual(relativeTo).TransformPoint(new());
        return new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private static bool ContainsBounds(FrameworkElement element, FrameworkElement container)
    {
        var bounds = Bounds(element, container);
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1;
    }

    private static string DescribeOrder() => string.Join(" -> ", Songs.Select(song => $"{song.DisplayName} [{song.CategoryId}]"));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .00001;
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(Window) ?? (object?)default(T))!;
    private static object? Invoke(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, arguments);
    private static async Task InvokeTaskAsync(string name, params object?[] arguments) => await (Task)Invoke(name, arguments)!;
    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out at {Step} after {timeoutMs} ms.");
            await Task.Delay(25);
        }
    }

    private static async Task CaptureRootAsync(string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Field<Grid>("RootGrid"));
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
        var path = System.IO.Path.Combine(OutputDirectory, name);
        await File.WriteAllBytesAsync(path, png);
        Images.Add(path);
    }

    private static void WriteMidi(string path, double duration, IReadOnlyList<(double Start, double End, int Pitch)> notes)
    {
        var events = notes.SelectMany(note => new[] { (Tick: (int)Math.Round(note.Start * 960), On: true, note.Pitch), (Tick: (int)Math.Round(note.End * 960), On: false, note.Pitch) }).OrderBy(item => item.Tick).ThenBy(item => item.On).ToArray();
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

    private static void Check(string name, bool passed, string detail)
    {
        Checks.Add(new { Name = name, Passed = passed, Detail = detail });
        if (!passed) Failed++;
        WriteProgress();
    }
    private static void SetStep(string name) { Step = name; WriteProgress(); }
    private static void WriteProgress() => File.WriteAllText(System.IO.Path.Combine(OutputDirectory, $"progress-{Phase}.json"), JsonSerializer.Serialize(new { Phase, Step, ProcessId = Environment.ProcessId, Checks = Checks.Count, Failed }, new JsonSerializerOptions { WriteIndented = true }));
}
