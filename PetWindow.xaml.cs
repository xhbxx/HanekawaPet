using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HanekawaPet.Core;
using HanekawaPet.Platform;
using Forms = System.Windows.Forms;

namespace HanekawaPet;

public partial class PetWindow : Window
{
    private readonly SettingsStore _store;
    private readonly PetSettings _settings;
    private PetPack _pack;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly Random _random = new();
    private readonly PetRoutine _routine;
    private bool _resting;
    private double _restCycleUntil;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private FileSystemWatcher? _watcher;
    private ChatWindow? _chatWindow;
    private SpeechBubbleWindow? _speechBubble;
    internal SemaphoreSlim ChatGate { get; } = new(1, 1);
    internal string? ActiveConversationId => _chatWindow?.ConversationId;
    private bool IsThinking => _chatWindow is { IsGenerating: true } || _speechBubble is { IsGenerating: true };
    private string ThinkingState => _pack.Clips.ContainsKey("thinking") ? "thinking" : "idle";
    private SettingsWindow? _settingsWindow;
    private AiSettingsWindow? _aiSettingsWindow;
    private GachaWindow? _gachaWindow;
    private GachaStore? _gachaStore;
    private BackendHost? _backend;
    private bool _ready, _closing, _pressed, _dragging, _autoWalking;
    private Point _pressPoint;
    private Point _pressScreen, _dragOrigin;
    private int _dragDirection;
    private double _lastDragMotion;
    private int _direction = 1;
    private double _lastTick, _stateStarted, _actionUntil, _autoUntil;
    private string _state = "";
    private string? _action;
    private AnimationClip? _clip;
    private AnimationFrame? _renderedFrame;
    private bool _renderedMirror;
    private Size _renderedSize;
    private string _status = "动画资源已就绪。";

    public PetWindow(SettingsStore store, PetSettings settings, PetPack pack)
    {
        _store = store;
        _settings = settings;
        _pack = pack;
        _routine = new PetRoutine(Now);
        InitializeComponent();
        TrackInteraction(this, true);
        SetScale(settings.Scale);
        Topmost = settings.Topmost;
        _timer.Tick += OnTick;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); ReloadPack(false); };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ShowBubble(); };
        LocationChanged += (_, _) => { _speechBubble?.Reposition(); if (_ready && !_dragging) ScheduleSave(); };
        SizeChanged += (_, _) => _speechBubble?.Reposition();
        IsVisibleChanged += (_, _) =>
        {
            if (!_ready || _closing) return;
            _lastTick = Now;
            if (IsVisible) _timer.Start(); else { _timer.Stop(); _speechBubble?.Dismiss(); }
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        WatchPack();
        SelectState("idle", true);
    }

    public event EventHandler? ModelChanged;
    public PetSettings Preferences => _settings;
    public PetPack Pack => _pack;
    public string Status => _status;
    private double Now => _clock.Elapsed.TotalMilliseconds;

    private void OnSourceInitialized(object? sender, EventArgs e) => DesktopPlatform.SetClickThrough(this, _settings.ClickThrough);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = DesktopPlatform.WorkArea(this);
        Left = _settings.Left ?? area.Right - Width - 28;
        Top = _settings.Top ?? area.Bottom - Height - 10;
        DesktopPlatform.Clamp(this);
        _ready = true;
        CreateTray();
        _lastTick = Now;
        ResetWander();
        _timer.Start();
        ScheduleSave();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Advance(Now, DateTime.Now.TimeOfDay);
    }

    private void Advance(double now, TimeSpan localTime, double? dailySample = null, double? climbSample = null, double? climbDistanceSample = null)
    {
        var deltaSeconds = Math.Clamp((now - _lastTick) / 1000, 0, .1);
        _lastTick = now;
        if (!IsVisible) return;
        var thinking = IsThinking;
        if (thinking && _action is not ("head-pat" or "cheek-block")) _action = null;
        if (_action is not null && now >= _actionUntil) _action = null;
        _resting = !thinking && !_pressed && !_dragging && _action is null &&
            (_settings.Sleeping || _routine.ShouldRest(now, localTime));
        if (!thinking && !_resting && !_pressed && !_dragging && _action is null &&
            _routine.YawnDue(now) && _pack.Clips.ContainsKey("yawn"))
        {
            StartAction("yawn");
            _routine.YawnPlayed(now);
            _routine.KnockPlayed(now);
        }
        var climbing = AdvanceClimb(now, deltaSeconds, !thinking && !_resting && !_pressed && !_dragging && _action is null && !ClimbBlockedByWindow, climbSample, climbDistanceSample);
        if (!climbing && !thinking && !_resting && !_pressed && !_dragging && _action is null && _chatWindow is not { IsVisible: true } &&
            _speechBubble is not { IsVisible: true } && _speechBubble is not { IsGenerating: true } &&
            _settingsWindow is not { IsVisible: true } && _aiSettingsWindow is not { IsVisible: true } && _gachaWindow is not { IsVisible: true } &&
            _routine.KnockDue(now))
        {
            var actions = new[] { "knock", "foot-swing" }.Where(_pack.Clips.ContainsKey).ToArray();
            if (actions.Length > 0)
            {
                StartAction(actions[Math.Min(actions.Length - 1, (int)((dailySample ?? _random.NextDouble()) * actions.Length))]);
                _routine.KnockPlayed(now);
            }
        }
        if (!climbing && !thinking && _settings.Movement == MovementMode.Wander && !_resting && !_dragging && _action is null && now >= _autoUntil)
        {
            _autoWalking = !_autoWalking;
            _autoUntil = now + (_autoWalking ? _random.Next(3500, 7500) : _random.Next(7000, 16000));
            if (_autoWalking) _direction = _random.Next(2) == 0 ? -1 : 1;
        }
        var walking = !climbing && !thinking && !_resting && !_pressed && !_dragging && _action is null &&
            _speechBubble is not { IsVisible: true } && _speechBubble is not { IsGenerating: true } &&
            _chatWindow is not { IsVisible: true } && _settingsWindow is not { IsVisible: true } && _aiSettingsWindow is not { IsVisible: true } && _gachaWindow is not { IsVisible: true } &&
            (_settings.Movement == MovementMode.Walk || _settings.Movement == MovementMode.Wander && _autoWalking);
        if (walking)
        {
            var area = DesktopPlatform.WorkArea(this);
            var maximum = Math.Max(area.Left, area.Right - Width);
            var next = Left + _direction * _settings.Speed * deltaSeconds;
            Left = Math.Clamp(next, area.Left, maximum);
            if (TryStartClimb(now, area, climbSample, climbDistanceSample)) { climbing = true; walking = false; }
            else
            {
                if (next <= area.Left) _direction = 1;
                if (next >= maximum) _direction = -1;
            }
        }
        if (_resting) UpdateRestAnimation(now);
        else
        {
            _restCycleUntil = 0;
            var state = _dragging ? DragState(now) : _action is "head-pat" or "cheek-block" ? _action : thinking ? ThinkingState : climbing ? (_climbSide < 0 ? "climb-left" : "climb-right") : _action ?? (walking ? (_direction > 0 ? "walk-right" : "walk-left") : "idle");
            SelectState(state);
        }
        RenderFrame(now);
    }

    private void UpdateRestAnimation(double now)
    {
        if (_restCycleUntil > now) return;
        var state = PetRoutine.RestVariant(_random.NextDouble());
        if (!_pack.Clips.ContainsKey(state)) state = "sleep";
        SelectState(state, true);
        _restCycleUntil = now + _pack.Get(state).DurationMs;
    }

    private void TrackInteraction(Window window, bool wakeManual)
    {
        window.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => RecordInteraction(wakeManual)), true);
        window.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, _) => RecordInteraction(wakeManual)), true);
    }

    internal void RecordInteraction(bool wakeManual = true)
    {
        CancelClimb(Now);
        _routine.Interact(Now);
        var manualChanged = wakeManual && _settings.Sleeping;
        if (wakeManual) _settings.Sleeping = false;
        if (_action is "yawn" or "knock" or "foot-swing" or "head-pat" or "cheek-block") _action = null;
        if (!_settings.Sleeping && _resting)
        {
            _resting = false; _restCycleUntil = 0;
            if (!_dragging) SelectState("idle", true);
        }
        if (manualChanged) Changed();
    }

    private void SelectState(string state, bool force = false)
    {
        if (!force && state == _state) return;
        _state = state;
        _stateStarted = Now;
        _clip = _pack.Get(state);
        RenderFrame(Now);
    }

    private void RenderFrame(double now)
    {
        if (_clip is null) return;
        var frame = _clip.At(now - _stateStarted);
        var size = new Size(Width, Height);
        var rotation = _state == "climb-left" ? 90d : _state == "climb-right" ? -90d : 0;
        if (ReferenceEquals(frame, _renderedFrame) && _clip.Mirror == _renderedMirror && size == _renderedSize && rotation == _renderedRotation) return;
        _renderedFrame = frame;
        _renderedMirror = _clip.Mirror;
        _renderedSize = size;
        _renderedRotation = rotation;
        if (!ReferenceEquals(PetImage.Source, frame.Image)) PetImage.Source = frame.Image;
        var transforms = new TransformGroup();
        var scale = 1d; var wallOffset = 0d;
        if (rotation != 0)
        {
            var fit = Math.Min(Width / frame.Image.PixelWidth, Height / frame.Image.PixelHeight);
            var imageWidth = frame.Image.PixelWidth * fit;
            var imageHeight = frame.Image.PixelHeight * fit;
            scale = Math.Min(1, Math.Min(Width / imageHeight, Height / imageWidth));
            wallOffset = (Width - imageHeight * scale) / 2 * (rotation > 0 ? -1 : 1);
        }
        transforms.Children.Add(new ScaleTransform((_clip.Mirror ? -1 : 1) * scale, scale));
        if (rotation != 0) transforms.Children.Add(new RotateTransform(rotation));
        transforms.Children.Add(new TranslateTransform(
            wallOffset + frame.OffsetX * Width / _pack.Definition.Canvas.Width * (_clip.Mirror ? -1 : 1),
            frame.OffsetY * Height / _pack.Definition.Canvas.Height));
        transforms.Freeze();
        PetImage.RenderTransform = transforms;
    }

    public void PlayAction(string state)
    {
        RecordInteraction();
        _settings.Sleeping = false;
        StartAction(state);
        Changed();
    }

    private void StartAction(string state)
    {
        _action = state;
        _actionUntil = Now + _pack.Get(state).DurationMs;
        SelectState(state, true);
    }

    public void SetMovement(MovementMode movement, int? direction = null)
    {
        RecordInteraction();
        _settings.Movement = movement;
        _settings.Sleeping = false;
        _action = null;
        if (direction.HasValue) _direction = direction.Value < 0 ? -1 : 1;
        ResetWander();
        Changed();
    }

    private void ResetWander() { _autoWalking = false; _autoUntil = Now + _random.Next(5000, 12000); }

    public void SetScale(double scale)
    {
        _settings.Scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, 2.5) : 1;
        var area = _ready ? DesktopPlatform.WorkArea(this) : SystemParameters.WorkArea;
        var width = _pack.Definition.Canvas.Width * _settings.Scale;
        var height = _pack.Definition.Canvas.Height * _settings.Scale;
        var fit = Math.Min(1, Math.Min(area.Width / width, area.Height / height));
        Width = width * fit;
        Height = height * fit;
        if (_ready) DesktopPlatform.Clamp(this);
        Changed();
    }

    public void SetSpeed(double speed) { _settings.Speed = double.IsFinite(speed) ? Math.Clamp(speed, 5, 160) : 28; Changed(); }
    public void SetTopmost(bool value)
    {
        Topmost = _settings.Topmost = value;
        if (_speechBubble != null) { _speechBubble.Topmost = value; _speechBubble.HeadText.Topmost = value; }
        Changed();
    }
    public void SetSleeping(bool value) { RecordInteraction(false); _settings.Sleeping = value; _action = null; _restCycleUntil = 0; Changed(); }
    public void SetClickThrough(bool value)
    {
        _settings.ClickThrough = value;
        if (value) _speechBubble?.Dismiss();
        DesktopPlatform.SetClickThrough(this, value);
        Changed();
    }

    public void ResetPosition() { Show(); DesktopPlatform.ParkAtCursor(this); Changed(); }
    public void Reveal()
    {
        RecordInteraction();
        Show();
        SetClickThrough(false);
        DesktopPlatform.Clamp(this);
        Activate();
    }

    public void ShowBubble()
    {
        RecordInteraction();
        Show();
        if (_settings.ClickThrough) SetClickThrough(false);
        if (_speechBubble is null)
        {
            _backend ??= new BackendHost(_store);
            _speechBubble = new SpeechBubbleWindow(this, _backend) { Owner = this };
            TrackInteraction(_speechBubble, true);
            _speechBubble.Closed += (_, _) => _speechBubble = null;
        }
        _speechBubble.Open();
    }

    internal Task RefreshChat() => _chatWindow?.RefreshConversation() ?? Task.CompletedTask;

    public void ShowChat()
    {
        RecordInteraction();
        Show();
        if (_settings.ClickThrough) SetClickThrough(false);
        var existing = _chatWindow != null;
        if (_chatWindow is null)
        {
            _backend ??= new BackendHost(_store);
            _chatWindow = new ChatWindow(this, _backend) { Owner = this };
            TrackInteraction(_chatWindow, true);
            _chatWindow.Closed += (_, _) => _chatWindow = null;
        }
        OpenWindow(_chatWindow);
        if (existing) _ = _chatWindow.RefreshConversation();
        _chatWindow.InputBox.Focus();
    }

    private static void OpenWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void ShowSettings()
    {
        RecordInteraction(false);
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this) { Owner = this };
            TrackInteraction(_settingsWindow, false);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        OpenWindow(_settingsWindow);
    }

    public void ShowAiSettings()
    {
        RecordInteraction(false);
        if (_aiSettingsWindow is null)
        {
            _backend ??= new BackendHost(_store);
            _aiSettingsWindow = new AiSettingsWindow(_backend) { Owner = this };
            TrackInteraction(_aiSettingsWindow, false);
            _aiSettingsWindow.Closed += (_, _) => _aiSettingsWindow = null;
        }
        OpenWindow(_aiSettingsWindow);
    }

    public void ShowGacha()
    {
        RecordInteraction(); Show();
        if (_settings.ClickThrough) SetClickThrough(false);
        if (_gachaWindow == null)
        {
            _gachaStore ??= new GachaStore(Path.Combine(_store.DataDirectory, "gacha"));
            _gachaWindow = new GachaWindow(_gachaStore, Path.Combine(AppContext.BaseDirectory, "gacha")) { Owner = this };
            TrackInteraction(_gachaWindow, true);
            _gachaWindow.Closed += (_, _) => _gachaWindow = null;
        }
        OpenWindow(_gachaWindow);
    }

    public void ChoosePack()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择包含 pet.json 的动画资源文件夹", SelectedPath = _pack.Directory, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) LoadPack(dialog.SelectedPath);
    }

    public bool LoadPack(string directory, bool feedback = true)
    {
        try { ApplyPack(PetPackLoader.Load(directory)); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.Log("加载动画资源失败。", ex);
            _status = "加载失败，已保留当前动画。";
            ModelChanged?.Invoke(this, EventArgs.Empty);
            if (feedback) MessageBox.Show(this, ex.Message, "加载动画失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ApplyPack(PetPack candidate)
    {
        CancelClimb(Now);
        _restCycleUntil = 0;
        _pack = candidate;
        _settings.PackDirectory = candidate.Directory;
        _action = null;
        Title = candidate.Name + " · 桌宠";
        _status = "已加载：" + candidate.Name;
        SetScale(_settings.Scale);
        SelectState("idle", true);
        WatchPack();
        Changed();
    }

    public void ReloadPack(bool feedback = true) => LoadPack(_pack.Directory, feedback);
    public void OpenPackFolder() => Process.Start(new ProcessStartInfo(_pack.Directory) { UseShellExecute = true });

    public void ImportAnimation(string state, string[] files, int duration)
    {
        try
        {
            var candidate = PackEditor.Replace(_pack, _store.DataDirectory, state, files, duration);
            _settings.ImportFrameDurationMs = duration;
            ApplyPack(candidate);
            PlayAction(state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _store.Log("替换动画失败。", ex);
            MessageBox.Show(this, ex.Message, "替换动画失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WatchPack()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_pack.Directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        void Queue()
        {
            if (_closing || Dispatcher.HasShutdownStarted) return;
            Dispatcher.InvokeAsync(() => { if (!_closing) { _reloadTimer.Stop(); _reloadTimer.Start(); } });
        }
        _watcher.Changed += (_, _) => Queue();
        _watcher.Created += (_, _) => Queue();
        _watcher.Deleted += (_, _) => Queue();
        _watcher.Renamed += (_, _) => Queue();
        _watcher.Error += (_, e) => _store.Log("资源包文件监听异常，可手动重新加载。", e.GetException());
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true; _pressPoint = e.GetPosition(this);
        _pressScreen = PointToScreen(_pressPoint); _dragOrigin = new Point(Left, Top);
        CaptureMouse(); e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var screenDelta = PointToScreen(e.GetPosition(this)) - _pressScreen;
        var delta = new Vector(screenDelta.X / dpi.DpiScaleX, screenDelta.Y / dpi.DpiScaleY);
        if (!_dragging && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_dragging) BeginPetDrag();
        MovePetDrag(_dragOrigin.X + delta.X, _dragOrigin.Y + delta.Y);
        e.Handled = true;
    }

    private void BeginPetDrag()
    {
        CancelClimb(Now);
        _edgeVisitSide = 0;
        _speechBubble?.Hide();
        _clickTimer.Stop(); _dragging = true; _dragDirection = 0; _lastDragMotion = Now;
        _action = null; SelectState("drag", true);
    }

    private void MovePetDrag(double left, double top)
    {
        _routine.Interact(Now);
        var delta = left - Left;
        if (Math.Abs(delta) >= .5) { _dragDirection = delta > 0 ? 1 : -1; _lastDragMotion = Now; }
        Left = left; Top = top;
        SelectState(DragState(Now));
    }

    private string DragState(double now)
    {
        var state = now - _lastDragMotion > 220 || _dragDirection == 0 ? "drag" : _dragDirection > 0 ? "drag-right" : "drag-left";
        return _pack.Clips.ContainsKey(state) ? state : "drag";
    }

    private void EndPetDrag()
    {
        _pressed = _dragging = false; _dragDirection = 0;
        _climbBlockedUntil = 0;
        SelectState(IsThinking ? ThinkingState : _settings.Sleeping ? "sleep" : "idle", true);
        DesktopPlatform.Clamp(this); Changed();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        RecordInteraction();
        if (_dragging) { EndPetDrag(); ReleaseMouseCapture(); e.Handled = true; return; }
        _pressed = false;
        ReleaseMouseCapture();
        CompletePetClick(_pressPoint);
        e.Handled = true;
    }

    private void CompletePetClick(Point point)
    {
        _clickTimer.Stop();
        var x = point.X / Width; var y = point.Y / Height;
        // Face takes precedence over the surrounding hair; coordinates follow pet scaling.
        if (x is >= .30 and <= .70 && y is >= .25 and <= .41 && _pack.Clips.ContainsKey("cheek-block"))
        {
            _speechBubble?.Hide();
            PlayAction("cheek-block");
        }
        else if (x is >= .15 and <= .85 && y is >= .03 and <= .46 && _pack.Clips.ContainsKey("head-pat"))
        {
            _speechBubble?.Hide();
            PlayAction("head-pat");
        }
        else _clickTimer.Start();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        _pressed = false;
        if (_dragging) EndPetDrag();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        _clickTimer.Stop();
        var menu = CreatePetMenu();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    internal ContextMenu CreatePetMenu()
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "设置" };
        var original = CreateSettingsMenu();
        while (original.Items.Count > 0)
        {
            var item = original.Items[0]; original.Items.RemoveAt(0); settings.Items.Add(item);
        }
        var gacha = new MenuItem { Header = "扭蛋" };
        gacha.Click += (_, _) => ShowGacha();
        menu.Items.Add(settings); menu.Items.Add(gacha);
        return menu;
    }

    internal ContextMenu CreateSettingsMenu()
    {
        var menu = new ContextMenu();
        void Add(string title, Action action, bool? check = null)
        {
            var item = new MenuItem { Header = title, IsCheckable = check.HasValue, IsChecked = check == true };
            item.Click += (_, _) => { RecordInteraction(false); action(); }; menu.Items.Add(item);
        }
        Add("气泡对话", ShowBubble);
        Add("聊天窗口", ShowChat);
        Add("AI 接口与知识库", ShowAiSettings);
        Add("桌宠设置与动画", ShowSettings);
        menu.Items.Add(new Separator());
        Add("原地待机", () => SetMovement(MovementMode.Stay), _settings.Movement == MovementMode.Stay);
        Add("向右慢走", () => SetMovement(MovementMode.Walk, 1));
        Add("向左慢走", () => SetMovement(MovementMode.Walk, -1));
        Add("自由散步", () => SetMovement(MovementMode.Wander), _settings.Movement == MovementMode.Wander);
        Add("挥手", () => PlayAction("wave"));
        Add("摸头", () => PlayAction("head-pat"));
        Add("戳脸", () => PlayAction("cheek-block"));
        Add("跳跃", () => PlayAction("jump"));
        Add("打哈欠", () => PlayAction("yawn"));
        Add("敲击屏幕", () => PlayAction("knock"));
        Add("坐着晃脚", () => PlayAction("foot-swing"));
        Add("爬墙 · 左侧", () => PlayAction("climb-left"));
        Add("爬墙 · 右侧", () => PlayAction("climb-right"));
        Add("手动休息", () => SetSleeping(!_settings.Sleeping), _settings.Sleeping);
        menu.Items.Add(new Separator());
        Add("始终置顶", () => SetTopmost(!_settings.Topmost), _settings.Topmost);
        Add("鼠标穿透（从托盘恢复）", () => SetClickThrough(!_settings.ClickThrough), _settings.ClickThrough);
        Add("放大", () => SetScale(_settings.Scale + .1));
        Add("缩小", () => SetScale(_settings.Scale - .1));
        Add("重置位置", ResetPosition);
        Add("打开动画文件夹", OpenPackFolder);
        Add("重新加载动画", () => ReloadPack());
        menu.Items.Add(new Separator());
        Add("隐藏桌宠（从托盘恢复）", Hide);
        Add("退出", Close);
        return menu;
    }

    private void CreateTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "pet.ico");
        _icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "羽川桌宠", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        void Add(string title, Action action)
        {
            var item = new Forms.ToolStripMenuItem(title);
            item.Click += (_, _) => Dispatcher.Invoke(() => { RecordInteraction(false); action(); }); menu.Items.Add(item);
        }
        Add("显示桌宠 / 取消鼠标穿透", Reveal);
        Add("气泡对话", ShowBubble);
        Add("聊天窗口", ShowChat);
        Add("AI 接口与知识库", ShowAiSettings);
        Add("桌宠设置与动画", ShowSettings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("原地待机", () => SetMovement(MovementMode.Stay));
        Add("自由散步", () => SetMovement(MovementMode.Wander));
        Add("打开动画文件夹", OpenPackFolder);
        Add("重新加载动画", () => ReloadPack());
        Add("隐藏桌宠", Hide);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("退出", Close);
        var compact = new Forms.ContextMenuStrip();
        var settings = new Forms.ToolStripMenuItem("设置");
        while (menu.Items.Count > 0)
        {
            var item = menu.Items[0]; menu.Items.RemoveAt(0); settings.DropDownItems.Add(item);
        }
        menu.Dispose();
        var gacha = new Forms.ToolStripMenuItem("扭蛋");
        gacha.Click += (_, _) => Dispatcher.Invoke(ShowGacha);
        compact.Items.Add(settings); compact.Items.Add(gacha);
        _tray.ContextMenuStrip = compact;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        RecordInteraction();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var first = paths[0];
        if (Directory.Exists(first)) LoadPack(first);
        else if (Path.GetFileName(first).Equals("pet.json", StringComparison.OrdinalIgnoreCase)) LoadPack(Path.GetDirectoryName(first)!);
        else { ShowSettings(); _settingsWindow?.OfferImport(paths); }
        e.Handled = true;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!_closing) Dispatcher.InvokeAsync(() => { if (!_closing) { SetScale(_settings.Scale); DesktopPlatform.Clamp(this); } });
    }

    internal void LogChatError(Exception error) => _store.Log("对话未完成。", error);

    private void Changed() { ScheduleSave(); ModelChanged?.Invoke(this, EventArgs.Empty); }
    private void ScheduleSave() { if (_ready && !_closing) { _saveTimer.Stop(); _saveTimer.Start(); } }
    private void Save()
    {
        if (!_ready) return;
        _settings.Left = Left;
        _settings.Top = Top;
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _store.Log("保存设置失败。", ex); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _timer.Stop(); _clickTimer.Stop(); _reloadTimer.Stop(); _saveTimer.Stop();
        _watcher?.Dispose();
        _speechBubble?.Close();
        _chatWindow?.Close();
        _settingsWindow?.Close();
        _aiSettingsWindow?.Close();
        _gachaWindow?.Close();
        _backend?.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        Save();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _icon?.Dispose();
    }
}
