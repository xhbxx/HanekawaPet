using System.IO;
using System.Windows;
using System.Windows.Controls;
using HanekawaPet.Core;
using HanekawaPet.Platform;

namespace HanekawaPet;

public partial class SettingsWindow : Window
{
    private void OnChat(object sender, RoutedEventArgs e) => _pet.ShowAiSettings();
    private readonly PetWindow _pet;
    private bool _updating = true;
    private string[]? _pendingFiles;
    private static readonly (string Key, string Label)[] States =
    [ ("idle", "待机"), ("walk-right", "向右走"), ("walk-left", "向左走"), ("head-pat", "摸头"), ("cheek-block", "戳脸"), ("wave", "挥手"),
      ("jump", "跳跃"), ("thinking", "对话 · 思考"), ("sleep", "团睡 · 静息"), ("sleep-ear-twitch", "团睡 · 耳朵微动"), ("sleep-tail-flick", "团睡 · 尾巴微动"), ("yawn", "打哈欠"), ("knock", "敲击屏幕"),
      ("climb-left", "爬墙 · 左侧"), ("climb-right", "爬墙 · 右侧"), ("foot-swing", "坐着晃脚"), ("drag", "悬空眨眼"), ("drag-left", "向左拖拽"), ("drag-right", "向右拖拽"), ("happy", "开心") ];

    public SettingsWindow(PetWindow pet)
    {
        _pet = pet;
        InitializeComponent();
        _pet.ModelChanged += OnModelChanged;
        Closed += (_, _) => _pet.ModelChanged -= OnModelChanged;
        Loaded += (_, _) =>
        {
            var area = DesktopPlatform.WorkArea(_pet);
            MinHeight = Math.Min(MinHeight, area.Height - 32);
            MinWidth = Math.Min(MinWidth, area.Width - 32);
            Height = Math.Min(Height, area.Height - 32);
            Width = Math.Min(Width, area.Width - 32);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        };
        UpdateView();
    }

    private string SelectedState => (ActionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "idle";
    private void OnModelChanged(object? sender, EventArgs e) => UpdateView();

    private void UpdateView()
    {
        _updating = true;
        var settings = _pet.Preferences;
        PackNameText.Text = _pet.Pack.Name;
        PackPathText.Text = _pet.Pack.Directory;
        StatusText.Text = _pet.Status;
        ScaleSlider.Value = settings.Scale;
        ScaleText.Text = $"{settings.Scale:P0}";
        SpeedSlider.Value = settings.Speed;
        SpeedText.Text = $"{settings.Speed:0} px/s";
        foreach (ComboBoxItem item in MovementBox.Items)
            if ((string)item.Tag == settings.Movement.ToString()) MovementBox.SelectedItem = item;
        TopmostBox.IsChecked = settings.Topmost;
        SleepBox.IsChecked = settings.Sleeping;
        ClickThroughBox.IsChecked = settings.ClickThrough;
        if (!DurationText.IsKeyboardFocused) DurationText.Text = settings.ImportFrameDurationMs.ToString();
        var selected = SelectedState;
        var keys = States.Select(item => item.Key).Concat(_pet.Pack.Clips.Keys).Distinct().ToArray();
        if (!ActionBox.Items.Cast<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(keys))
        {
            ActionBox.Items.Clear();
            foreach (var key in keys)
                ActionBox.Items.Add(new ComboBoxItem { Content = States.FirstOrDefault(item => item.Key == key).Label ?? key, Tag = key });
        }
        foreach (ComboBoxItem item in ActionBox.Items) if ((string)item.Tag == selected) ActionBox.SelectedItem = item;
        if (ActionBox.SelectedItem is null) ActionBox.SelectedIndex = 0;
        UpdateActionInfo();
        _updating = false;
    }

    private void UpdateActionInfo()
    {
        var clip = _pet.Pack.Get(SelectedState);
        var fallback = !_pet.Pack.Clips.ContainsKey(SelectedState);
        ActionInfoText.Text = fallback
            ? (SelectedState == "walk-left" && _pet.Pack.Clips.ContainsKey("walk-right") ? "使用向右走动画的镜像。" : "尚未提供此动作，暂用待机素材。")
            : $"{clip.Frames.Count} 帧 · 每轮 {clip.DurationMs / 1000:0.##} 秒{(clip.Mirror ? " · 镜像" : "")}";
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updating) _pet.SetScale(e.NewValue); }
    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_updating) _pet.SetSpeed(e.NewValue); }
    private void OnMovementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && MovementBox.SelectedItem is ComboBoxItem item && Enum.TryParse<MovementMode>((string)item.Tag, out var mode)) _pet.SetMovement(mode);
    }
    private void OnFlagsChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (ReferenceEquals(sender, TopmostBox)) _pet.SetTopmost(TopmostBox.IsChecked == true);
        else if (ReferenceEquals(sender, SleepBox)) _pet.SetSleeping(SleepBox.IsChecked == true);
        else if (ReferenceEquals(sender, ClickThroughBox)) _pet.SetClickThrough(ClickThroughBox.IsChecked == true);
    }
    private void OnActionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating) UpdateActionInfo(); }
    private void OnWave(object sender, RoutedEventArgs e) => _pet.PlayAction("wave");
    private void OnJump(object sender, RoutedEventArgs e) => _pet.PlayAction("jump");
    private void OnResetPosition(object sender, RoutedEventArgs e) => _pet.ResetPosition();
    private void OnChoosePack(object sender, RoutedEventArgs e) => _pet.ChoosePack();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _pet.OpenPackFolder();
    private void OnReload(object sender, RoutedEventArgs e) => _pet.ReloadPack();
    private void OnDefaultPack(object sender, RoutedEventArgs e) => _pet.LoadPack(Path.Combine(AppContext.BaseDirectory, "pets", "default"));
    private void OnPreview(object sender, RoutedEventArgs e) => _pet.PlayAction(SelectedState);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public void OfferImport(string[] files)
    {
        _pendingFiles = files;
        PendingText.Text = $"已接收 {files.Length} 个文件。请选择要替换的动作，再点击下方按钮。";
        ImportButton.Content = "使用拖入的文件替换";
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DurationText.Text, out var duration) || duration is < 20 or > 2000)
        {
            MessageBox.Show(this, "PNG 每帧时长请输入 20–2000 之间的整数。", "帧时长", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var files = _pendingFiles;
        if (files is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择一个 GIF 或一组按顺序命名的 PNG 帧",
                Filter = "PNG 序列帧 / GIF 动画|*.png;*.gif",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true) return;
            files = dialog.FileNames;
        }
        _pet.ImportAnimation(SelectedState, files, duration);
        _pendingFiles = null;
        PendingText.Text = "";
        ImportButton.Content = "替换所选动作…";
    }
}
