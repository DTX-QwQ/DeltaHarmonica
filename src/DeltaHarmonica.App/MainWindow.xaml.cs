using System.Collections.ObjectModel;
using System.Diagnostics;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DeltaHarmonica.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SongEntry> _songs = [];
    private readonly ForegroundWindowService _foreground = new();
    private readonly NativeInputService _input = new();
    private readonly PlaybackEngine _player;
    private readonly SettingsStore _store;
    private readonly AudioTranscriptionService _audio;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly List<Border> _keyCaps = [];
    private AppSettings _settings;
    private PlaybackPlan? _plan;
    private WindowTarget? _target;
    private CancellationTokenSource? _countdown;
    private CancellationTokenSource? _audioCancellation;
    private CancellationTokenSource? _hotkeyWait;
    private bool _initialized, _updatingTimeline, _changingSong, _starting, _switchingSong, _hotkeyBusy, _preview, _closing;
    private PlaybackState _lastState;
    private string _lastPlaybackMessage = "";

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 940));
        _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica"));
        _settings = _store.Load();
        _audio = new(_store.DirectoryPath);
        _player = new(_input);
        _player.Updated += snapshot => DispatcherQueue.TryEnqueue(() => UpdatePlayback(snapshot));
        _hotkeys = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _hotkeys.Triggered += action => DispatcherQueue.TryEnqueue(async () => await HandleHotkeyAsync(action));
        PlaylistList.ItemsSource = _songs;
        CreateKeyboard();
        RestoreControls();
        RootGrid.Loaded += RootGrid_Loaded;
        Closed += (_, _) =>
        {
            _closing = true;
            _countdown?.Cancel();
            _audioCancellation?.Cancel();
            _hotkeyWait?.Cancel();
            try { _player.Dispose(); } catch { }
            try { _hotkeys.Dispose(); } catch { }
            try { _input.Dispose(); } catch { }
            SaveSettings();
        };
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        var error = _hotkeys.Apply(_settings.Hotkeys);
        HotkeyStatus.Text = error ?? $"快捷键已生效：{_settings.Hotkeys.Pause} 暂停 / 继续，{_settings.Hotkeys.Previous} 上一首，{_settings.Hotkeys.Next} 下一首，{_settings.Hotkeys.Stop} 停止";
        if (error is not null) SetStatus(error, InfoBarSeverity.Warning);
        EnvironmentLabel.Text = _audio.IsInstalled ? "本地转录环境已就绪" : "尚未安装 · 选择 Python 3.10–3.12 后安装";
        await AddMidiPathsAsync(_settings.Playlist.Where(File.Exists).ToArray());
    }

    private void RestoreControls()
    {
        SpeedSlider.Value = Math.Clamp(_settings.Speed, .25, 3);
        _player.Speed = SpeedSlider.Value;
        SpeedLabel.Text = $"{SpeedSlider.Value:F2} ×";
        BaseNoteBox.Value = Math.Clamp(_settings.BaseMidiNote, 0, 115);
        TransposeBox.Value = Math.Clamp(_settings.Transpose, -36, 36);
        LowerOffsetBox.Value = Math.Clamp(_settings.LowerOffset, -36, 0);
        UpperOffsetBox.Value = Math.Clamp(_settings.UpperOffset, 0, 36);
        SemitoneBox.Value = Math.Clamp(_settings.SemitoneOffset, -2, 2);
        CountdownBox.Value = Math.Clamp(_settings.CountdownSeconds, 2, 15);
        ChordCombo.SelectedIndex = Math.Clamp(_settings.ChordMode, 0, 2);
        FoldOctavesCheck.IsChecked = _settings.FoldOctaves;
        AutoNextCheck.IsChecked = _settings.AutoNext;
        PythonPathBox.Text = _settings.PythonPath;
        if (string.IsNullOrWhiteSpace(PythonPathBox.Text))
        {
            var pythonRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            PythonPathBox.Text = new[] { "Python311", "Python310", "Python312" }
                .Select(p => Path.Combine(pythonRoot, p, "python.exe")).FirstOrDefault(File.Exists) ?? "";
        }
        OutputDirectoryBox.Text = string.IsNullOrWhiteSpace(_settings.AudioOutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "DeltaHarmonica") : _settings.AudioOutputDirectory;
        PauseHotkeyBox.Text = _settings.Hotkeys.Pause;
        PreviousHotkeyBox.Text = _settings.Hotkeys.Previous;
        NextHotkeyBox.Text = _settings.Hotkeys.Next;
        StopHotkeyBox.Text = _settings.Hotkeys.Stop;
    }

    private void CreateKeyboard()
    {
        var keys = "ZXCVBNM,";
        var notes = new[] { "Do", "Re", "Mi", "Fa", "Sol", "La", "Si", "Do" };
        for (int i = 0; i < keys.Length; i++)
        {
            KeyboardGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = keys[i].ToString(), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = notes[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var cap = new Border { Child = stack, Background = Brush(0x30, 0x3E, 0x47), CornerRadius = new CornerRadius(8) };
            Grid.SetColumn(cap, i);
            KeyboardGrid.Children.Add(cap);
            _keyCaps.Add(cap);
        }
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    private async Task<IReadOnlyList<string>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToArray();
    }
    private async void ImportMidi_Click(object sender, RoutedEventArgs e)
    {
        try { await AddMidiPathsAsync(await PickFilesAsync(".mid", ".midi")); }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
    }
    private async void LoadDemo_Click(object sender, RoutedEventArgs e) => await AddMidiPathsAsync(new[] { Path.Combine(AppContext.BaseDirectory, "examples", "小星星.mid") });
    private async Task AddMidiPathsAsync(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (var path in paths)
        {
            if (_songs.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var song = await Task.Run(() => MidiReader.Read(path));
                _songs.Add(new(path, song));
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
        }
        if (_songs.Count > 0 && PlaylistList.SelectedIndex < 0) PlaylistList.SelectedIndex = 0;
        LibraryCount.Text = $"{_songs.Count} 首曲目 · 保存在本机";
        SaveSettings();
        if (errors.Count > 0) SetStatus(string.Join("\n", errors), InfoBarSeverity.Warning);
    }
    private async void RemoveSong_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not SongEntry selected) return;
        await StopPlaybackAsync();
        _songs.Remove(selected);
        if (_songs.Count > 0) PlaylistList.SelectedIndex = 0;
        else { SongTitle.Text = "选择一首 MIDI"; SongInfo.Text = "曲库为空"; _plan = null; }
        LibraryCount.Text = $"{_songs.Count} 首曲目 · 保存在本机";
        SaveSettings();
    }
    private async void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _changingSong) return;
        await StopPlaybackAsync();
        LoadSelection();
    }
    private void LoadSelection()
    {
        if (PlaylistList.SelectedItem is not SongEntry entry) return;
        SongTitle.Text = entry.DisplayName;
        SongInfo.Text = entry.Detail;
        _changingSong = true;
        TrackCombo.ItemsSource = new[] { new TrackChoice(null, "全部非打击乐音轨") }
            .Concat(entry.Song.Tracks.Select(t => new TrackChoice(t.Index, $"{t.Index + 1}. {t.Name} ({t.NoteCount} 音符)"))).ToArray();
        TrackCombo.SelectedIndex = 0;
        _changingSong = false;
        RebuildPlan();
    }
    private void RebuildPlan()
    {
        if (PlaylistList.SelectedItem is not SongEntry entry) return;
        try
        {
            int? track = (TrackCombo.SelectedItem as TrackChoice)?.Index;
            var options = new MappingOptions
            {
                BaseMidiNote = Integer(BaseNoteBox, 60), TransposeSemitones = Integer(TransposeBox, 0),
                LowerRegisterSemitones = Integer(LowerOffsetBox, -12), UpperRegisterSemitones = Integer(UpperOffsetBox, 12),
                SemitoneModifier = Integer(SemitoneBox, 1),
                OutOfRangePolicy = FoldOctavesCheck.IsChecked == true ? OutOfRangePolicy.FoldOctaves : OutOfRangePolicy.Skip,
                ChordPolicy = ChordCombo.SelectedIndex switch { 1 => ChordPolicy.Lowest, 2 => ChordPolicy.Compatible, _ => ChordPolicy.Highest },
                SelectedTracks = track.HasValue ? new[] { track.Value } : null
            };
            _plan = PlaybackPlanBuilder.Build(entry.Song, options);
            _updatingTimeline = true;
            TimelineSlider.Maximum = Math.Max(.01, _plan.Duration.TotalSeconds);
            TimelineSlider.Value = 0;
            TimelineSlider.IsEnabled = false;
            PositionLabel.Text = $"00:00 / {FormatTime(_plan.Duration)}";
            _updatingTimeline = false;
            PlanInfo.Text = $"{_plan.Chords.Count} 段指法 · 跳过 {_plan.SkippedNoteCount} 个音符" +
                (_plan.Warnings.Count > 0 ? "\n" + string.Join("\n", _plan.Warnings) : " · 映射完成");
            var warnings = _plan.Warnings;
            SetStatus(warnings.Count > 0 ? string.Join("\n", warnings) : "已生成指法，可先预览或开始演奏。", warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        }
        catch (Exception ex) { _plan = null; SetStatus(ex.Message, InfoBarSeverity.Error); }
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await StartAsync(false);
    private async void Preview_Click(object sender, RoutedEventArgs e) => await StartAsync(true);
    private async Task StartAsync(bool preview, bool preserveTarget = false, bool songTransition = false)
    {
        if (_closing || _starting || (_switchingSong && !songTransition)) return;
        if (_plan is null || _plan.Chords.Count == 0) { SetStatus("请先导入 MIDI 并选择有音符的音轨。", InfoBarSeverity.Warning); return; }
        var plan = _plan;
        _starting = true;
        _countdown?.Cancel();
        _countdown?.Dispose();
        _countdown = new();
        var token = _countdown.Token;
        try
        {
            await _player.StopAsync();
            token.ThrowIfCancellationRequested();
            if (_player.State == PlaybackState.Faulted) return;
            _preview = preview;
            if (!preview)
            {
                if (!preserveTarget || _target is null || !_foreground.IsForeground(_target))
                {
                    for (int seconds = Integer(CountdownBox, 5); seconds > 0; seconds--)
                    {
                        SetStatus($"{seconds} 秒后开始：请切到游戏并打开口琴。F9 或停止按钮可取消。", InfoBarSeverity.Informational);
                        StateBadge.Text = $"● 倒计时 {seconds}";
                        await Task.Delay(1000, token);
                    }
                    token.ThrowIfCancellationRequested();
                    _target = _foreground.CaptureTarget();
                    if (_target is null) { SetStatus("没有找到外部目标窗口，请切到游戏后重试。", InfoBarSeverity.Warning); StateBadge.Text = "● 就绪"; return; }
                }
                TargetLabel.Text = $"目标窗口：{_target.Title} · PID {_target.ProcessId}";
            }
            token.ThrowIfCancellationRequested();
            if (_closing) return;
            _lastState = PlaybackState.Stopped;
            _player.Start(plan, preview, preview ? null : () => _target is not null && _foreground.IsForeground(_target));
        }
        catch (OperationCanceledException) { SetStatus("开始演奏已取消。", InfoBarSeverity.Informational); StateBadge.Text = "● 就绪"; }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
        finally { _starting = false; }
    }
    private async void Pause_Click(object sender, RoutedEventArgs e) => await TogglePauseAsync(fromHotkey: false);
    private async Task TogglePauseAsync(bool fromHotkey)
    {
        if (_player.State == PlaybackState.Playing) { _player.Pause(); return; }
        if (_player.State != PlaybackState.Paused) return;
        if (_preview || fromHotkey) { _player.Resume(); return; }
        if (_closing || _starting || _switchingSong) return;
        _starting = true;
        _countdown?.Dispose();
        _countdown = new();
        var token = _countdown.Token;
        try
        {
            for (int seconds = Integer(CountdownBox, 5); seconds > 0; seconds--)
            {
                SetStatus($"{seconds} 秒后继续：请切回原游戏窗口。", InfoBarSeverity.Informational);
                await Task.Delay(1000, token);
            }
            token.ThrowIfCancellationRequested();
            if (_closing) return;
            _player.Resume();
        }
        catch (OperationCanceledException) { }
        finally { _starting = false; }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopPlaybackAsync();
    private async Task StopPlaybackAsync()
    {
        _countdown?.Cancel();
        _hotkeyWait?.Cancel();
        await _player.StopAsync();
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await ChangeSongAsync(-1, false);
    private async void Next_Click(object sender, RoutedEventArgs e) => await ChangeSongAsync(1, false);
    private async Task ChangeSongAsync(int delta, bool fromHotkey, bool forcePlay = false)
    {
        if (_closing || _songs.Count == 0 || _starting || _switchingSong) return;
        _switchingSong = true;
        var play = forcePlay || _player.State == PlaybackState.Playing;
        var preview = _preview;
        _countdown?.Cancel();
        _countdown?.Dispose();
        _countdown = new();
        var token = _countdown.Token;
        try
        {
            await _player.StopAsync();
            token.ThrowIfCancellationRequested();
            if (_closing) return;
            _changingSong = true;
            PlaylistList.SelectedIndex = (Math.Max(0, PlaylistList.SelectedIndex) + delta + _songs.Count) % _songs.Count;
            _changingSong = false;
            LoadSelection();
            if (play) await StartAsync(preview, preserveTarget: fromHotkey, songTransition: true);
        }
        catch (OperationCanceledException) { }
        finally { _switchingSong = false; }
    }
    private async Task HandleHotkeyAsync(HotkeyAction action)
    {
        if (_closing) return;
        if (action == HotkeyAction.Stop) { await StopPlaybackAsync(); return; }
        if (action == HotkeyAction.TogglePause && _player.State == PlaybackState.Playing) { _player.Pause(); return; }
        if (_hotkeyBusy) return;
        _hotkeyBusy = true;
        using var waitCancellation = new CancellationTokenSource();
        _hotkeyWait = waitCancellation;
        try
        {
            var wasPlaying = _player.State == PlaybackState.Playing;
            if (wasPlaying && action is HotkeyAction.Previous or HotkeyAction.Next)
                _player.Pause("正在切换曲目，请松开快捷键");
            if (!await _hotkeys.WaitForReleaseAsync(action, waitCancellation.Token))
            {
                SetStatus("请松开快捷键及其修饰键，再重试。", InfoBarSeverity.Warning);
                return;
            }
            waitCancellation.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            switch (action)
            {
                case HotkeyAction.TogglePause:
                    if (_player.State is PlaybackState.Stopped or PlaybackState.Completed or PlaybackState.Faulted) await StartAsync(false);
                    else await TogglePauseAsync(true);
                    break;
                case HotkeyAction.Previous: await ChangeSongAsync(-1, true, wasPlaying); break;
                case HotkeyAction.Next: await ChangeSongAsync(1, true, wasPlaying); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
        finally { _hotkeyWait = null; _hotkeyBusy = false; }
    }
    private void UpdatePlayback(PlaybackSnapshot snapshot)
    {
        if (_closing) return;
        _updatingTimeline = true;
        TimelineSlider.Value = snapshot.Position.TotalSeconds;
        TimelineSlider.IsEnabled = snapshot.State is PlaybackState.Playing or PlaybackState.Paused;
        PositionLabel.Text = $"{FormatTime(snapshot.Position)} / {FormatTime(snapshot.Duration)}";
        _updatingTimeline = false;
        if (!_starting || snapshot.State != PlaybackState.Stopped)
            StateBadge.Text = snapshot.State switch { PlaybackState.Playing => _preview ? "● 预览中" : "● 演奏中", PlaybackState.Paused => "● 已暂停", _ => "● 就绪" };
        var pressed = snapshot.Chord?.Notes.Select(n => n.Key).ToHashSet() ?? [];
        for (int i = 0; i < _keyCaps.Count; i++) _keyCaps[i].Background = pressed.Contains("zxcvbnm,"[i]) ? Brush(0x1F, 0x8D, 0x73) : Brush(0x30, 0x3E, 0x47);
        ModifierLabel.Text = snapshot.Chord is { } chord
            ? $"{chord.Register switch { HarmonicaRegister.Lower => "左键 · 低音", HarmonicaRegister.Upper => "右键 · 高音", _ => "原调" }}{(chord.Semitone ? " + 中键" : "")}" : "原调";
        if (_lastState != snapshot.State || _lastPlaybackMessage != snapshot.Message)
        {
            if (!_starting || snapshot.State != PlaybackState.Stopped)
                SetStatus(snapshot.Message, snapshot.State == PlaybackState.Faulted ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
            var completed = _lastState != PlaybackState.Completed && snapshot.State == PlaybackState.Completed;
            _lastState = snapshot.State;
            _lastPlaybackMessage = snapshot.Message;
            if (completed && AutoNextCheck.IsChecked == true && PlaylistList.SelectedIndex < _songs.Count - 1)
                DispatcherQueue.TryEnqueue(async () => await ChangeSongAsync(1, true, true));
        }
    }
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString("h\\:mm\\:ss") : value.ToString("mm\\:ss");
    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initialized && !_updatingTimeline) _player.Seek(TimeSpan.FromSeconds(e.NewValue));
    }
    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized) return;
        _player.Speed = e.NewValue;
        SpeedLabel.Text = $"{e.NewValue:F2} ×";
        SaveSettings();
    }
    private async void Mapping_Changed(object sender, SelectionChangedEventArgs e) => await MappingChangedAsync();
    private async void MappingNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => await MappingChangedAsync();
    private async void MappingCheck_Changed(object sender, RoutedEventArgs e) => await MappingChangedAsync();
    private async Task MappingChangedAsync()
    {
        if (!_initialized || _changingSong) return;
        await StopPlaybackAsync();
        RebuildPlan();
        SaveSettings();
    }
    private void Preference_Changed(object sender, RoutedEventArgs e) { if (_initialized) SaveSettings(); }
    private void PreferenceNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (_initialized) SaveSettings(); }
    private void SaveHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var candidate = new HotkeySettings { Pause = PauseHotkeyBox.Text, Previous = PreviousHotkeyBox.Text, Next = NextHotkeyBox.Text, Stop = StopHotkeyBox.Text };
        var error = _hotkeys.Apply(candidate);
        HotkeyStatus.Text = error ?? "快捷键已保存并生效。";
        if (error is null) { _settings = _settings with { Hotkeys = candidate }; SaveSettings(); }
        SetStatus(HotkeyStatus.Text, error is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void SelectAudio_Click(object sender, RoutedEventArgs e)
    {
        try { AudioPathBox.Text = (await PickFilesAsync(".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aiff" )).FirstOrDefault() ?? AudioPathBox.Text; }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
    }
    private async void SelectPython_Click(object sender, RoutedEventArgs e)
    {
        try { PythonPathBox.Text = (await PickFilesAsync(".exe")).FirstOrDefault() ?? PythonPathBox.Text; SaveSettings(); }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
    }
    private async void SelectOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) { OutputDirectoryBox.Text = folder.Path; SaveSettings(); }
        }
        catch (Exception ex) { SetStatus(ex.Message, InfoBarSeverity.Error); }
    }
    private async void InstallAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCancellation is not null) return;
        await RunAudioTaskAsync(async (progress, token) =>
        {
            await _audio.EnsureEnvironmentAsync(PythonPathBox.Text.Trim(), progress, token);
            EnvironmentLabel.Text = "本地转录环境已就绪";
            SetStatus("转录环境安装完成。", InfoBarSeverity.Success);
        });
    }
    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCancellation is not null) return;
        if (!_audio.IsInstalled) { SetStatus("请先选择 Python 并安装本地转录环境。", InfoBarSeverity.Warning); return; }
        if (!File.Exists(AudioPathBox.Text)) { SetStatus("请先选择音频文件。", InfoBarSeverity.Warning); return; }
        await RunAudioTaskAsync(async (progress, token) =>
        {
            var midiPath = await _audio.TranscribeAsync(AudioPathBox.Text, OutputDirectoryBox.Text.Trim(), progress, token);
            await AddMidiPathsAsync(new[] { midiPath });
            SetStatus($"转录完成，已加入曲库：{midiPath}", InfoBarSeverity.Success);
        });
    }
    private async Task RunAudioTaskAsync(Func<IProgress<string>, CancellationToken, Task> operation)
    {
        _audioCancellation = new();
        InstallAudioButton.IsEnabled = TranscribeButton.IsEnabled = false;
        CancelAudioButton.IsEnabled = true;
        AudioProgress.IsIndeterminate = true;
        AudioLogBox.Text = "";
        IProgress<string> progress = new Progress<string>(line =>
        {
            AudioLogBox.Text = (AudioLogBox.Text + line + "\n");
            if (AudioLogBox.Text.Length > 20000) AudioLogBox.Text = AudioLogBox.Text[^15000..];
        });
        try { SaveSettings(); await operation(progress, _audioCancellation.Token); }
        catch (OperationCanceledException) { SetStatus("转录任务已取消。", InfoBarSeverity.Informational); }
        catch (Exception ex) { progress.Report(ex.Message); SetStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            AudioProgress.IsIndeterminate = false;
            InstallAudioButton.IsEnabled = TranscribeButton.IsEnabled = true;
            CancelAudioButton.IsEnabled = false;
            _audioCancellation.Dispose(); _audioCancellation = null;
        }
    }
    private void CancelAudio_Click(object sender, RoutedEventArgs e) => _audioCancellation?.Cancel();
    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.DirectoryPath);
        Process.Start(new ProcessStartInfo(_store.DirectoryPath) { UseShellExecute = true });
    }
    private static int Integer(NumberBox box, int fallback) => double.IsFinite(box.Value) ? (int)Math.Round(box.Value) : fallback;
    private void SaveSettings()
    {
        if (!_initialized) return;
        _settings = _settings with
        {
            Speed = SpeedSlider.Value, BaseMidiNote = Integer(BaseNoteBox, 60), Transpose = Integer(TransposeBox, 0),
            LowerOffset = Integer(LowerOffsetBox, -12), UpperOffset = Integer(UpperOffsetBox, 12), SemitoneOffset = Integer(SemitoneBox, 1),
            CountdownSeconds = Integer(CountdownBox, 5), FoldOctaves = FoldOctavesCheck.IsChecked == true,
            ChordMode = ChordCombo.SelectedIndex, AutoNext = AutoNextCheck.IsChecked == true,
            PythonPath = PythonPathBox.Text, AudioOutputDirectory = OutputDirectoryBox.Text,
            Playlist = _songs.Select(s => s.Path).ToList()
        };
        try { _store.Save(_settings); }
        catch (Exception ex) { if (!_closing) SetStatus($"设置保存失败：{ex.Message}", InfoBarSeverity.Warning); }
    }
    private void SetStatus(string message, InfoBarSeverity severity)
    {
        if (_closing) return;
        StatusBar.Severity = severity;
        StatusBar.Message = message;
    }
}
