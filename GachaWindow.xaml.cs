using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class GachaWindow : Window
{
    private readonly GachaStore _store;
    private readonly string _catalogRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private GachaState? _state;
    private string? _shownDay;
    internal bool IsDrawing { get; private set; }
    internal GachaResultWindow? ResultWindow { get; private set; }
    internal Task Ready => _ready.Task;
    internal string? LastAnimation { get; private set; }
    private readonly TaskCompletionSource _ready = new();

    public GachaWindow(GachaStore store, string catalogRoot)
    {
        _store = store; _catalogRoot = catalogRoot;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var area = Owner is { } owner ? Platform.DesktopPlatform.WorkArea(owner) : SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, Math.Max(300, area.Width - 24)); MinHeight = Math.Min(MinHeight, Math.Max(300, area.Height - 24));
            Width = Math.Min(Width, area.Width - 24); Height = Math.Min(Height, area.Height - 24);
            Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2;
            RefreshView(); _refresh.Start(); _ready.TrySetResult();
        };
        _refresh.Tick += (_, _) => { if (!IsDrawing && _shownDay != _store.Today) RefreshView(); };
        Activated += (_, _) => { if (IsLoaded && !IsDrawing && ResultWindow == null) RefreshView(); };
        Closed += (_, _) => { _lifetime.Cancel(); _refresh.Stop(); };
    }
    private static SolidColorBrush ColorBrush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    internal void RefreshView()
    {
        if (_lifetime.IsCancellationRequested || IsDrawing) return;
        try
        {
            _state = _store.Read(); _shownDay = _store.Today;
            var used = _store.HasDrawnToday(_state);
            QuotaText.Text = used ? "今日已抽取" : "今日剩余 1 次";
            PendingButton.Visibility = _state.PendingDrawId == null ? Visibility.Collapsed : Visibility.Visible;
            BuildCabinet(_state);
            var catalog = GachaCatalog.Load(_catalogRoot);
            DrawButton.IsEnabled = !used && catalog.Prizes.Count > 0 && _state.PendingDrawId == null;
            DrawButton.Content = used ? "明天再来" : catalog.Prizes.Count == 0 ? "等待奖品加入" : "转一颗扭蛋";
            StatusText.Text = used ? "今天的小惊喜已经收藏好了，明天再来看看吧。" : catalog.Prizes.Count == 0
                ? "奖品插图还没准备好，今天的抽取机会会为你保留。" : "准备好了吗？转出今天的小惊喜。";
            if (!used && _state.PendingDrawId != null) StatusText.Text = "上次的小惊喜还没打开，先看看抽中了什么吧。";
            PreviewButton.Visibility = catalog.AnimationFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewButton.IsEnabled = true;
            AnimationHint.Text = catalog.AnimationFiles.Count == 0 ? "扭蛋动画预留位置" : "等待转动扭蛋";
            AnimationImage.Visibility = Visibility.Collapsed; AnimationImage.Source = null; AnimationPlaceholder.Visibility = Visibility.Visible;
            if (catalog.AnimationFiles.Count > 0)
            {
                using var file = File.OpenRead(GachaCatalog.Resolve(_catalogRoot, catalog.AnimationFiles[0]));
                var decoder = new GifBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                decoder.Frames[0].Freeze(); AnimationImage.Source = decoder.Frames[0];
                AnimationImage.Visibility = Visibility.Visible; AnimationPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { DrawButton.IsEnabled = false; PreviewButton.IsEnabled = false; StatusText.Text = ex is InvalidDataException or IOException ? ex.Message : "扭蛋素材或记录暂时无法读取，今天的次数不会被扣除。"; }
    }
    internal async Task Draw()
    {
        if (IsDrawing || !DrawButton.IsEnabled || _lifetime.IsCancellationRequested) return;
        IsDrawing = true; DrawButton.IsEnabled = false; PreviewButton.IsEnabled = false; PendingButton.Visibility = Visibility.Collapsed;
        string? errorText = null;
        var committed = false;
        try
        {
            // Persist before the presentation, so closing during playback cannot grant another draw.
            var catalog = GachaCatalog.Load(_catalogRoot);
            var clip = LoadAnimation(catalog);
            var draw = _store.Draw(_catalogRoot);
            committed = true;
            _state = _store.Read(); QuotaText.Text = "今日已抽取";
            DrawingVeil.Visibility = Visibility.Visible;
            StatusText.Text = "正在转动扭蛋…";
            await PlayAnimation(clip);
            _lifetime.Token.ThrowIfCancellationRequested();
            ShowResult(draw);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { errorText = (committed ? "结果已保存，可通过上次抽取记录继续查看。" : "本次没有扣除次数。") + (ex is InvalidOperationException or InvalidDataException or IOException ? ex.Message : "展示暂时失败。"); }
        finally
        {
            IsDrawing = false;
            if (!_lifetime.IsCancellationRequested)
            {
                DrawingVeil.Visibility = Visibility.Collapsed; AnimationImage.Visibility = Visibility.Collapsed;
                AnimationPlaceholder.Visibility = Visibility.Visible; RefreshView();
                if (errorText != null) StatusText.Text = errorText;
            }
        }
    }
    private AnimationClip? LoadAnimation(GachaCatalog catalog)
    {
        LastAnimation = catalog.SelectAnimation();
        return LastAnimation == null ? null : new(GifFrames.Load(GachaCatalog.Resolve(_catalogRoot, LastAnimation), 320), false);
    }
    private async Task PlayAnimation(AnimationClip? clip)
    {
        if (clip == null) { await Task.Delay(1200, _lifetime.Token); return; }
        AnimationPlaceholder.Visibility = Visibility.Collapsed; AnimationImage.Visibility = Visibility.Visible;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < clip.DurationMs)
        {
            AnimationImage.Source = clip.At(clock.Elapsed.TotalMilliseconds).Image;
            await Task.Delay(20, _lifetime.Token);
        }
    }
    internal async Task Preview()
    {
        if (IsDrawing || _lifetime.IsCancellationRequested || !PreviewButton.IsEnabled) return;
        IsDrawing = true; DrawButton.IsEnabled = false; PreviewButton.IsEnabled = false; PendingButton.IsEnabled = false;
        string? error = null;
        try
        {
            var clip = LoadAnimation(GachaCatalog.Load(_catalogRoot));
            StatusText.Text = "动画预览中，不消耗抽取次数。";
            await PlayAnimation(clip);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = "动画暂时无法播放，抽取次数未受影响。"; }
        finally
        {
            IsDrawing = false;
            if (!_lifetime.IsCancellationRequested) { PendingButton.IsEnabled = true; RefreshView(); if (error != null) StatusText.Text = error; }
        }
    }
    private async void OnPreview(object sender, RoutedEventArgs e) => await Preview();
    private void ShowResult(GachaDraw draw, bool acknowledge = true)
    {
        var count = _store.Read().Draws.Count(d => d.PrizeId == draw.PrizeId);
        var dialog = new GachaResultWindow(_store, draw, count, acknowledge) { Owner = this };
        ResultWindow = dialog;
        try { dialog.ShowDialog(); if (acknowledge && !_lifetime.IsCancellationRequested) _store.Acknowledge(draw.Id); }
        finally { ResultWindow = null; }
    }
    private async void OnDraw(object sender, RoutedEventArgs e) => await Draw();
    private void OnPending(object sender, RoutedEventArgs e)
    {
        try
        {
            var state = _store.Read(); var draw = state.Draws.FirstOrDefault(d => d.Id == state.PendingDrawId);
            if (draw != null) ShowResult(draw);
            RefreshView();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { StatusText.Text = "暂时无法展示上次的物品。"; }
    }
    internal static Image Artwork(GachaStore store, GachaDraw draw, double height)
    {
        var image = new Image { Stretch = Stretch.Uniform, Height = height, Margin = new Thickness(4) };
        try
        {
            using var file = File.OpenRead(GachaCatalog.Resolve(store.Root, draw.Image));
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            decoder.Frames[0].Freeze(); image.Source = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { }
        return image;
    }
    private void BuildCabinet(GachaState state)
    {
        CabinetRows.Children.Clear();
        var collected = state.Draws.GroupBy(d => d.PrizeId).Select(g => new { Draw = g.First(), Count = g.Count() }).ToArray();
        CollectionText.Text = $"{collected.Length} 种 · {state.Draws.Count} 件";
        for (var row = 0; row < Math.Max(3, (collected.Length + 3) / 4); row++)
        {
            var shelf = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            for (var i = 0; i < 4; i++) shelf.ColumnDefinitions.Add(new ColumnDefinition());
            for (var column = 0; column < 4; column++)
            {
                var index = row * 4 + column;
                var content = new StackPanel { Margin = new Thickness(5, 10, 5, 10) };
                var cell = new Border { Background = ColorBrush("#F9F3E6"), BorderBrush = ColorBrush("#CEB48D"), BorderThickness = new Thickness(0, 0, 0, 8), Margin = new Thickness(3), Child = content };
                if (index < collected.Length)
                {
                    var item = collected[index];
                    content.Children.Add(Artwork(_store, item.Draw, 108));
                    content.Children.Add(new TextBlock { Text = item.Draw.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush("#574D42"), FontSize = 12 });
                    content.Children.Add(new TextBlock { Text = "× " + item.Count, TextAlignment = TextAlignment.Center, Foreground = ColorBrush("#9A8060"), FontSize = 12, Margin = new Thickness(0, 5, 0, 0) });
                    cell.Cursor = System.Windows.Input.Cursors.Hand;
                    cell.MouseLeftButtonUp += (_, _) => { try { ShowResult(item.Draw, false); } catch (Exception ex) when (ex is not OutOfMemoryException) { StatusText.Text = "暂时无法打开收藏详情。"; } };
                }
                else
                {
                    content.Children.Add(new Border { Height = 108, Margin = new Thickness(12, 4, 12, 4), CornerRadius = new CornerRadius(32, 32, 10, 10), BorderBrush = ColorBrush("#E4D8C3"), BorderThickness = new Thickness(1) });
                    content.Children.Add(new TextBlock { Text = "尚未收集", TextAlignment = TextAlignment.Center, Foreground = ColorBrush("#B5A58D"), FontSize = 12, Margin = new Thickness(0, 5, 0, 10) });
                }
                Grid.SetColumn(cell, column); shelf.Children.Add(cell);
            }
            CabinetRows.Children.Add(shelf);
        }
    }
}
