using DeltaHarmonica.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace DeltaHarmonica.App;

public sealed partial class MainWindow
{
    private readonly List<SongCategory> _categories = [];
    private readonly Dictionary<string, ListView> _categoryLists = [];
    private readonly Dictionary<string, Expander> _categoryExpanders = [];
    private SongEntry? _selectedSong, _draggedSong;
    private SongCategory? _draggedCategory;
    private bool _syncingLibrarySelection, _restoringLibrary, _libraryDialogOpen;
    private int _selectionVersion;
    private FrameworkElement? _libraryDragHandle;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _libraryDragTimer;
    private Point _libraryDragStart, _libraryDragPoint;
    private bool _libraryDragActive;
    private SongCategory? _libraryDropCategory;
    private int _libraryDropIndex;
    private int _libraryDropCategoryIndex = -1;

    private void InitializeLibrary()
    {
        _categories.AddRange(_settings.LibraryCategories.Select(c => new SongCategory(c.Id, c.Name, c.IsExpanded)));
        _categories.Insert(Math.Clamp(_settings.UncategorizedIndex, 0, _categories.Count), new("", "未分类", _settings.UncategorizedExpanded));
        foreach (var category in _categories) AddCategoryView(category);
        _libraryDragTimer = DispatcherQueue.CreateTimer();
        _libraryDragTimer.Interval = TimeSpan.FromMilliseconds(30);
        _libraryDragTimer.Tick += (_, _) => ScrollLibraryDuringDrag();
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _libraryDragHandle is not null)
            { EndLibraryDrag(); e.Handled = true; }
        }), true);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) EndLibraryDrag(); };
        Closed += (_, _) => EndLibraryDrag();
    }

    private void AddCategoryView(SongCategory category)
    {
        var heading = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        heading.SetBinding(TextBlock.TextProperty, new Binding { Source = category, Path = new PropertyPath(nameof(SongCategory.Summary)), Mode = BindingMode.OneWay });
        var header = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
        var dragHandle = new Border { Width = 32, MinHeight = 32, DataContext = category,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new FontIcon { Glyph = "\uE700", FontSize = 12, Foreground = Brush(0x81, 0x92, 0x9D),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
        AutomationProperties.SetName(dragHandle, "拖动调整分类顺序");
        ToolTipService.SetToolTip(dragHandle, "按住拖动调整分类顺序，也可在右侧菜单中上移或下移。");
        dragHandle.PointerPressed += CategoryDragHandle_PointerPressed;
        dragHandle.PointerMoved += LibraryDragHandle_PointerMoved;
        dragHandle.PointerReleased += LibraryDragHandle_PointerReleased;
        dragHandle.PointerCaptureLost += LibraryDragHandle_PointerCaptureLost;
        dragHandle.PointerCanceled += LibraryDragHandle_PointerCaptureLost;
        header.Children.Add(dragHandle);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        var dropTarget = new Border { Child = header, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(dropTarget, "点击展开或收起；将歌曲拖到这里可移入分类。");

        var menu = new MenuFlyout();
        var moveUp = new MenuFlyoutItem { Text = "上移分类", Icon = new FontIcon { Glyph = "\uE74A" } };
        var moveDown = new MenuFlyoutItem { Text = "下移分类", Icon = new FontIcon { Glyph = "\uE74B" } };
        moveUp.Click += (_, _) => MoveCategory(category, _categories.IndexOf(category) - 1);
        moveDown.Click += (_, _) => MoveCategory(category, _categories.IndexOf(category) + 2);
        menu.Opening += (_, _) =>
        {
            var index = _categories.IndexOf(category);
            moveUp.IsEnabled = index > 0;
            moveDown.IsEnabled = index >= 0 && index < _categories.Count - 1;
        };
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        if (category.Id.Length > 0)
        {
            var rename = new MenuFlyoutItem { Text = "修改分类名称" };
            rename.Click += async (_, _) =>
            {
                var name = await PromptLibraryNameAsync("修改分类名称", category.Name, 40,
                    value => CategoryNameError(value, category), "分类名称");
                if (name is null) return;
                category.Name = name;
                AutomationProperties.SetName(_categoryLists[category.Id], $"{name}歌曲列表");
                SaveSettings();
            };
            var remove = new MenuFlyoutItem { Text = "删除分类（歌曲移回未分类）" };
            remove.Click += (_, _) => DeleteCategory(category);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(rename);
            menu.Items.Add(remove);
        }
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 16 },
            Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Flyout = menu };
        AutomationProperties.SetName(more, "分类操作");
        ToolTipService.SetToolTip(more, "分类操作");
        Grid.SetColumn(more, 2);
        header.Children.Add(more);

        var list = new ListView
        {
            ItemsSource = category.Songs, ItemTemplate = (DataTemplate)RootGrid.Resources["LibrarySongTemplate"],
            SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // System drag/drop is unsupported in elevated WinUI apps. The row handle uses pointer capture instead.
            CanDragItems = false, CanReorderItems = false, IsSwipeEnabled = true,
            Background = Brush(0x20, 0x2B, 0x32), Margin = new Thickness(-12, 0, -12, 0)
        };
        AutomationProperties.SetName(list, $"{category.Name}歌曲列表");
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += async (_, _) =>
        {
            if (!_initialized || _syncingLibrarySelection || _draggedSong is not null || _changingSong || _switchingSong) return;
            if (list.SelectedItem is SongEntry song && !ReferenceEquals(song, _selectedSong)) await SelectSongAsync(song);
        };
        var empty = new TextBlock { Text = "暂无曲目 · 将歌曲拖到分类标题", FontSize = 11,
            Foreground = Brush(0xAE, 0xBC, 0xC6), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        var content = new StackPanel();
        content.Children.Add(list);
        content.Children.Add(empty);
        void UpdateEmpty() => empty.Visibility = category.Songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        category.Songs.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
        var expander = new Expander { Header = dropTarget, Content = content, IsExpanded = category.IsExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        // Let the template's star column measure the header after its padding and chevron.
        // A width derived from the outer Expander can exceed that slot and clip the menu.
        expander.Expanding += (_, _) => { category.IsExpanded = true; SaveSettings(); };
        expander.Collapsed += (_, _) => { category.IsExpanded = false; SaveSettings(); };
        _categoryLists.Add(category.Id, list);
        _categoryExpanders.Add(category.Id, expander);
        LibraryGroups.Children.Add(expander);
    }

    private void AddLibrarySong(SongEntry song)
    {
        if (_settings.SongMetadata.TryGetValue(song.Path, out var metadata))
        {
            if (!string.IsNullOrWhiteSpace(metadata.DisplayName)) song.DisplayName = metadata.DisplayName;
            song.CategoryId = metadata.CategoryId;
        }
        var category = _categories.FirstOrDefault(c => c.Id == song.CategoryId) ?? _categories.First(c => c.Id.Length == 0);
        song.CategoryId = category.Id;
        category.Songs.Add(song);
        _songs.Add(song);
    }

    private void RefreshLibraryOrder()
    {
        var ordered = _categories.SelectMany(c => c.Songs).ToArray();
        _songs.Clear();
        foreach (var song in ordered) _songs.Add(song);
        LibraryCount.Text = $"{_songs.Count} 首曲目 · {_categories.Count - 1} 个分类";
        SaveSettings();
    }

    private Dictionary<string, SongLibraryMetadata> CaptureSongMetadata()
    {
        var metadata = new Dictionary<string, SongLibraryMetadata>(_settings.SongMetadata, StringComparer.OrdinalIgnoreCase);
        foreach (var song in _songs) metadata[song.Path] = new(song.DisplayName, song.CategoryId);
        return metadata;
    }

    private void SetSelectedSong(SongEntry song)
    {
        _selectedSong = song;
        UpdateLibrarySelection();
    }

    private void UpdateLibrarySelection()
    {
        _syncingLibrarySelection = true;
        try
        {
            foreach (var category in _categories)
                _categoryLists[category.Id].SelectedItem = _selectedSong is not null && category.Songs.Contains(_selectedSong) ? _selectedSong : null;
        }
        finally { _syncingLibrarySelection = false; }
        RenameSongButton.IsEnabled = MoveSongButton.IsEnabled = RemoveSongButton.IsEnabled = _selectedSong is not null;
    }

    private async Task SelectSongAsync(SongEntry song)
    {
        var version = ++_selectionVersion;
        SaveSettings();
        SetSelectedSong(song);
        await StopPlaybackAsync();
        if (version == _selectionVersion && !_closing) LoadSelection();
    }

    private void LibraryDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SongEntry song } handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || _libraryDialogOpen) return;
        EndLibraryDrag();
        _draggedSong = song;
        _libraryDragHandle = handle;
        _libraryDragPoint = _libraryDragStart = e.GetCurrentPoint(LibraryDragOverlay).Position;
        if (!handle.CapturePointer(e.Pointer)) EndLibraryDrag();
        e.Handled = true;
    }

    private void CategoryDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SongCategory category } handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || _libraryDialogOpen) return;
        EndLibraryDrag();
        _draggedCategory = category;
        _libraryDragHandle = handle;
        _libraryDragPoint = _libraryDragStart = e.GetCurrentPoint(LibraryDragOverlay).Position;
        if (!handle.CapturePointer(e.Pointer)) EndLibraryDrag();
        e.Handled = true;
    }

    private void LibraryDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _libraryDragHandle)) return;
        _libraryDragPoint = e.GetCurrentPoint(LibraryDragOverlay).Position;
        if (!_libraryDragActive && Math.Abs(_libraryDragPoint.X - _libraryDragStart.X) + Math.Abs(_libraryDragPoint.Y - _libraryDragStart.Y) >= 6)
        { _libraryDragActive = true; _libraryDragTimer?.Start(); }
        if (_libraryDragActive) UpdateLibraryDropTarget();
        e.Handled = true;
    }

    private void LibraryDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _libraryDragHandle)) return;
        _libraryDragPoint = e.GetCurrentPoint(LibraryDragOverlay).Position;
        if (_libraryDragActive)
        {
            UpdateLibraryDropTarget();
            if (_draggedSong is { } song && _libraryDropCategory is { } category) MoveSong(song, category, _libraryDropIndex);
            else if (_draggedCategory is { } draggedCategory && _libraryDropCategoryIndex >= 0) MoveCategory(draggedCategory, _libraryDropCategoryIndex);
        }
        EndLibraryDrag();
        e.Handled = true;
    }

    private void LibraryDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _libraryDragHandle)) EndLibraryDrag();
    }

    private void EndLibraryDrag()
    {
        var handle = _libraryDragHandle;
        _libraryDragHandle = null;
        _draggedSong = null;
        _draggedCategory = null;
        _libraryDragActive = false;
        _libraryDropCategory = null;
        _libraryDropCategoryIndex = -1;
        _libraryDragTimer?.Stop();
        LibraryDragOverlay.Children.Clear();
        handle?.ReleasePointerCaptures();
    }

    private Rect LibraryBounds(FrameworkElement element)
    {
        var origin = element.TransformToVisual(LibraryDragOverlay).TransformPoint(new());
        return new(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private void UpdateLibraryDropTarget()
    {
        LibraryDragOverlay.Children.Clear();
        _libraryDropCategory = null;
        _libraryDropCategoryIndex = -1;
        var viewport = LibraryBounds(LibraryScrollViewer);
        if (!viewport.Contains(_libraryDragPoint)) return;
        if (_draggedCategory is not null)
        {
            UpdateCategoryDropTarget(viewport);
            return;
        }
        foreach (var category in _categories)
        {
            var expander = _categoryExpanders[category.Id];
            var header = LibraryBounds((FrameworkElement)expander.Header);
            if (header.Contains(_libraryDragPoint))
            {
                _libraryDropCategory = category;
                _libraryDropIndex = category.Songs.Count;
                ShowLibraryDropMarker(header.X, header.Bottom, header.Width, $"移到「{category.Name}」末尾");
                return;
            }
            var list = _categoryLists[category.Id];
            if (!expander.IsExpanded || !LibraryBounds((FrameworkElement)expander.Content).Contains(_libraryDragPoint)) continue;
            var bounds = LibraryBounds(list);
            var index = category.Songs.Count;
            var markerY = bounds.Bottom;
            for (var i = 0; i < category.Songs.Count; i++)
            {
                if (list.ContainerFromIndex(i) is not ListViewItem item) continue;
                var row = LibraryBounds(item);
                if (_libraryDragPoint.Y < row.Y + row.Height / 2)
                { index = i; markerY = row.Y; break; }
                markerY = row.Bottom;
            }
            _libraryDropCategory = category;
            _libraryDropIndex = index;
            var sourceIndex = _draggedSong is null ? -1 : category.Songs.IndexOf(_draggedSong);
            var displayIndex = index - (sourceIndex >= 0 && sourceIndex < index ? 1 : 0);
            ShowLibraryDropMarker(bounds.X, Math.Clamp(markerY, viewport.Y, viewport.Bottom - 3), bounds.Width,
                $"移到「{category.Name}」第 {displayIndex + 1} 个位置");
            return;
        }
    }

    private void UpdateCategoryDropTarget(Rect viewport)
    {
        var index = _categories.Count;
        var markerY = LibraryBounds(_categoryExpanders[_categories[^1].Id]).Bottom;
        for (var i = 0; i < _categories.Count; i++)
        {
            var expander = _categoryExpanders[_categories[i].Id];
            var header = LibraryBounds((FrameworkElement)expander.Header);
            if (_libraryDragPoint.Y < header.Y + header.Height / 2)
            {
                index = i;
                markerY = LibraryBounds(expander).Y;
                break;
            }
        }
        _libraryDropCategoryIndex = index;
        var sourceIndex = _draggedCategory is null ? -1 : _categories.IndexOf(_draggedCategory);
        var displayIndex = index - (sourceIndex >= 0 && sourceIndex < index ? 1 : 0);
        ShowLibraryDropMarker(viewport.X, Math.Clamp(markerY, viewport.Y, viewport.Bottom - 3), viewport.Width,
            $"分类移到第 {displayIndex + 1} 个位置");
    }

    private void ShowLibraryDropMarker(double x, double y, double width, string caption)
    {
        var marker = new Border { Width = width, Height = 3, Background = Brush(0x63, 0xE6, 0xBE), CornerRadius = new(2) };
        Canvas.SetLeft(marker, x); Canvas.SetTop(marker, y);
        LibraryDragOverlay.Children.Add(marker);
        var hint = new Border { Background = Brush(0x63, 0xE6, 0xBE), CornerRadius = new(5), Padding = new(8, 5, 8, 5),
            Child = new TextBlock { Text = caption, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brush(0x14, 0x1D, 0x24) } };
        Canvas.SetLeft(hint, Math.Clamp(_libraryDragPoint.X + 12, 0, Math.Max(0, LibraryDragOverlay.ActualWidth - 260)));
        Canvas.SetTop(hint, Math.Clamp(_libraryDragPoint.Y + 14, 0, Math.Max(0, LibraryDragOverlay.ActualHeight - 40)));
        LibraryDragOverlay.Children.Add(hint);
    }

    private void ScrollLibraryDuringDrag()
    {
        if (!_libraryDragActive) return;
        var viewport = LibraryBounds(LibraryScrollViewer);
        if (_libraryDragPoint.X < viewport.X || _libraryDragPoint.X > viewport.Right) return;
        var delta = _libraryDragPoint.Y < viewport.Y + 30 ? -12 : _libraryDragPoint.Y > viewport.Bottom - 30 ? 12 : 0;
        if (delta == 0) return;
        LibraryScrollViewer.ChangeView(null, Math.Clamp(LibraryScrollViewer.VerticalOffset + delta, 0, LibraryScrollViewer.ScrollableHeight), null, true);
        UpdateLibraryDropTarget();
    }

    // index denotes an insertion boundary in the destination before removing the source item.
    private void MoveSong(SongEntry song, SongCategory destination, int index)
    {
        var source = _categories.FirstOrDefault(c => c.Songs.Contains(song));
        if (source is null || !_categories.Contains(destination)) return;
        var oldIndex = source.Songs.IndexOf(song);
        if (source == destination && oldIndex < index) index--;
        _syncingLibrarySelection = true;
        try
        {
            source.Songs.Remove(song);
            song.CategoryId = destination.Id;
            destination.Songs.Insert(Math.Clamp(index, 0, destination.Songs.Count), song);
        }
        finally { _syncingLibrarySelection = false; }
        RefreshLibraryOrder();
        _categoryExpanders[destination.Id].IsExpanded = true;
        UpdateLibrarySelection();
    }

    // index denotes an insertion boundary before removing the source category, like MoveSong.
    private void MoveCategory(SongCategory category, int index)
    {
        var oldIndex = _categories.IndexOf(category);
        if (oldIndex < 0) return;
        index = Math.Clamp(index, 0, _categories.Count);
        if (oldIndex < index) index--;
        if (oldIndex == index) return;
        _syncingLibrarySelection = true;
        try
        {
            _categories.RemoveAt(oldIndex);
            _categories.Insert(index, category);
            var expander = _categoryExpanders[category.Id];
            LibraryGroups.Children.Remove(expander);
            LibraryGroups.Children.Insert(index, expander);
            RefreshLibraryOrder();
        }
        finally { _syncingLibrarySelection = false; }
        UpdateLibrarySelection();
    }

    private bool RenameSong(SongEntry song, string name)
    {
        name = name.Trim();
        if (!_songs.Contains(song) || NameError(name, 120) is not null) return false;
        song.DisplayName = name;
        if (ReferenceEquals(song, _selectedSong)) SongTitle.Text = name;
        SaveSettings();
        return true;
    }

    private SongCategory? CreateCategory(string name)
    {
        name = name.Trim();
        if (CategoryNameError(name) is not null) return null;
        var category = new SongCategory(Guid.NewGuid().ToString("N"), name);
        _categories.Add(category);
        AddCategoryView(category);
        RefreshLibraryOrder();
        return category;
    }

    private void DeleteCategory(SongCategory category)
    {
        if (category.Id.Length == 0 || !_categories.Contains(category)) return;
        _syncingLibrarySelection = true;
        try
        {
            foreach (var song in category.Songs.ToArray())
            {
                category.Songs.Remove(song);
                song.CategoryId = "";
                _categories.First(c => c.Id.Length == 0).Songs.Add(song);
            }
            LibraryGroups.Children.Remove(_categoryExpanders[category.Id]);
            _categoryExpanders.Remove(category.Id);
            _categoryLists.Remove(category.Id);
            _categories.Remove(category);
        }
        finally { _syncingLibrarySelection = false; }
        RefreshLibraryOrder();
        UpdateLibrarySelection();
        SetStatus("分类已删除，其中的歌曲已移回未分类。", InfoBarSeverity.Success);
    }

    private static string? NameError(string value, int maximum) => string.IsNullOrWhiteSpace(value) ? "请输入名称。" :
        value.Length > maximum ? $"名称最多 {maximum} 个字符。" : value.Any(char.IsControl) ? "名称不能包含换行或控制字符。" : null;

    private string? CategoryNameError(string value, SongCategory? except = null) => NameError(value, 40) ??
        (_categories.Any(c => c != except && string.Equals(c.Name, value.Trim(), StringComparison.OrdinalIgnoreCase)) ? "已存在同名分类，请使用其他名称。" : null);

    private async Task<string?> PromptLibraryNameAsync(string title, string current, int maximum, Func<string, string?> validate, string label)
    {
        if (_libraryDialogOpen) return null;
        _libraryDialogOpen = true;
        try
        {
            var input = new TextBox { Text = current, MaxLength = maximum, Header = label, PlaceholderText = $"最多 {maximum} 个字符" };
            var error = new TextBlock { Foreground = Brush(0xFF, 0xB4, 0xAB), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
            panel.Children.Add(input); panel.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = ElementTheme.Dark,
                Title = title, Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var problem = validate(input.Text.Trim());
                if (problem is null) return;
                args.Cancel = true; error.Text = problem; error.Visibility = Visibility.Visible;
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
        }
        finally { _libraryDialogOpen = false; }
    }

    private async void RenameSong_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSong is not { } song) return;
        var name = await PromptLibraryNameAsync("修改歌曲名称", song.DisplayName, 120, value => NameError(value, 120), "歌曲名称（不修改原 MIDI 文件）");
        if (name is not null && RenameSong(song, name)) SetStatus("歌曲名称已保存。", InfoBarSeverity.Success);
    }

    private async void CreateCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptLibraryNameAsync("新增分类", "", 40, value => CategoryNameError(value), "分类名称");
        if (name is not null) CreateCategory(name);
    }

    private async void MoveSong_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSong is not { } song || _libraryDialogOpen) return;
        _libraryDialogOpen = true;
        try
        {
            var choices = new ComboBox { Header = "目标分类", ItemsSource = _categories, DisplayMemberPath = nameof(SongCategory.Name),
                SelectedItem = _categories.First(c => c.Songs.Contains(song)), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 300 };
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "移动歌曲到分类",
                Content = choices, PrimaryButtonText = "移动", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is SongCategory target && song.CategoryId != target.Id)
                MoveSong(song, target, target.Songs.Count);
        }
        finally { _libraryDialogOpen = false; }
    }

    private void SpeedScale_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SpeedScaleCanvas.Children.Clear();
        var width = SpeedScaleCanvas.ActualWidth;
        if (width <= 0) return;
        foreach (var speed in new[] { .25, 1, 1.5, 2, 2.5, 3 })
        {
            var label = new TextBlock { Text = $"{speed:0.##}×", FontSize = 11,
                Foreground = speed == 1 ? Brush(0x63, 0xE6, 0xBE) : Brush(0xAE, 0xBC, 0xC6), Tag = speed };
            label.Measure(new(double.PositiveInfinity, 22));
            var position = 11 + (width - 22) * (speed - .25) / 2.75;
            Canvas.SetLeft(label, Math.Clamp(position - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            SpeedScaleCanvas.Children.Add(label);
        }
    }
}
