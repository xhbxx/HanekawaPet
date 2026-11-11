using System.IO;
using System.Windows;
using System.Windows.Threading;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;
    private EventWaitHandle? _chatEvent;
    private RegisteredWaitHandle? _chatRegistration;
    private EventWaitHandle? _aiSettingsEvent;
    private RegisteredWaitHandle? _aiSettingsRegistration;
    private SettingsStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Argument(string flag)
        {
            var index = Array.IndexOf(e.Args, flag);
            return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null;
        }
        var data = Argument("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HanekawaPet");
        _store = new SettingsStore(data);
        DispatcherUnhandledException += (_, args) =>
        {
            _store.Log("未处理的应用异常。", args.Exception);
            MessageBox.Show("桌宠遇到了错误，详情已保存到：\n" + _store.DataDirectory, "羽川桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        try
        {
            if (Argument("--self-test") is { } tests)
            {
                Shutdown(FrameworkChecks.Run(tests));
                return;
            }
            var smoke = Argument("--smoke-test");
            var chatSmoke = Argument("--chat-smoke-test");
            var routineSmoke = Argument("--routine-smoke-test");
            var searchSmoke = Argument("--search-smoke-test");
            var gachaSmoke = Argument("--gacha-smoke-test");
            if (gachaSmoke != null && Argument("--data-dir") == null) throw new ArgumentException("扭蛋验证必须指定独立的数据目录。");
            if (searchSmoke != null && Argument("--data-dir") == null) throw new ArgumentException("搜索验证必须指定独立的数据目录。");
            if (smoke is null && chatSmoke is null && routineSmoke is null && searchSmoke is null && gachaSmoke is null)
            {
                _mutex = new Mutex(true, "Local\\HanekawaPet.Desktop.v2", out var first);
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HanekawaPet.Show.v2");
                _chatEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HanekawaPet.Chat.v4");
                _aiSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HanekawaPet.AiSettings.v4");
                if (!first)
                {
                    if (e.Args.Contains("--ai-settings")) _aiSettingsEvent.Set();
                    else if (e.Args.Contains("--chat")) _chatEvent.Set();
                    else _showEvent.Set();
                    _mutex.Dispose(); _mutex = null; Shutdown(); return;
                }
            }
            var settings = _store.Load();
            var defaultDirectory = Path.Combine(AppContext.BaseDirectory, "pets", "default");
            // Follow the bundled default across release folders, preserving separately imported packs.
            if (settings.PackDirectory is { } previous && IsBundledDefault(previous)) settings.PackDirectory = defaultDirectory;
            PetPack pack;
            try { pack = PetPackLoader.Load(Argument("--pack") ?? settings.PackDirectory ?? defaultDirectory); }
            catch (Exception ex)
            {
                _store.Log("指定资源包不可用，使用内置资源。", ex);
                pack = PetPackLoader.Load(defaultDirectory);
                settings.PackDirectory = defaultDirectory;
            }
            if (settings.DragAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.ReplaceDragSet(pack, _store.DataDirectory, defaultDirectory);
                    settings.PackDirectory = pack.Directory;
                    settings.DragAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("拖拽资源升级未完成，保留当前资源包。", ex); }
            }
            if (settings.RestAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.ReplaceRestSet(pack, _store.DataDirectory, defaultDirectory);
                    settings.PackDirectory = pack.Directory;
                    settings.RestAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("休息与哈欠资源升级未完成，保留当前资源包。", ex); }
            }
            if (settings.KnockAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.Replace(pack, _store.DataDirectory, "knock", [Path.Combine(defaultDirectory, "frames", "screen-knock.gif")], 180);
                    settings.PackDirectory = pack.Directory;
                    settings.KnockAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("敲击屏幕资源升级未完成，保留当前资源包。", ex); }
            }
            if (settings.ThinkingAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.Replace(pack, _store.DataDirectory, "thinking", [Path.Combine(defaultDirectory, "frames", "thinking.gif")], 180);
                    settings.PackDirectory = pack.Directory;
                    settings.ThinkingAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("对话思考动画升级未完成，保留当前资源包。", ex); }
            }
            if (settings.FootSwingAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.Replace(pack, _store.DataDirectory, "foot-swing", [Path.Combine(defaultDirectory, "frames", "foot-swing.gif")], 180);
                    settings.PackDirectory = pack.Directory;
                    settings.FootSwingAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("坐着晃脚动画升级未完成，保留当前资源包。", ex); }
            }
            if (settings.ClimbAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.ReplaceClimbSet(pack, _store.DataDirectory, defaultDirectory);
                    settings.PackDirectory = pack.Directory;
                    settings.ClimbAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("爬墙动画升级未完成，保留当前资源包。", ex); }
            }
            if (settings.HeadPatAnimationRevision < 2)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.Replace(pack, _store.DataDirectory, "head-pat", [Path.Combine(defaultDirectory, "frames", "head-pat.gif")], 180);
                    settings.PackDirectory = pack.Directory;
                    settings.HeadPatAnimationRevision = 2;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("摸头动画升级未完成，保留当前资源包。", ex); }
            }
            if (settings.CheekAnimationRevision < 1)
            {
                try
                {
                    if (!pack.Directory.Equals(Path.GetFullPath(defaultDirectory), StringComparison.OrdinalIgnoreCase))
                        pack = PackEditor.Replace(pack, _store.DataDirectory, "cheek-block", [Path.Combine(defaultDirectory, "frames", "cheek-block.gif")], 180);
                    settings.PackDirectory = pack.Directory;
                    settings.CheekAnimationRevision = 1;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _store.Log("戳脸动画升级未完成，保留当前资源包。", ex); }
            }
            var pet = new PetWindow(_store, settings, pack);
            MainWindow = pet;
            if (_showEvent is not null)
                _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                    (_, _) => Dispatcher.InvokeAsync(pet.Reveal), null, Timeout.Infinite, false);
            if (_chatEvent != null)
                _chatRegistration = ThreadPool.RegisterWaitForSingleObject(_chatEvent,
                    (_, _) => Dispatcher.InvokeAsync(pet.ShowChat), null, Timeout.Infinite, false);
            if (_aiSettingsEvent != null)
                _aiSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(_aiSettingsEvent,
                    (_, _) => Dispatcher.InvokeAsync(pet.ShowAiSettings), null, Timeout.Infinite, false);
            pet.Show();
            if (int.TryParse(Argument("--quit-after-ms"), out var lifetime))
            {
                var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(lifetime, 100, 60000)) };
                exitTimer.Tick += (_, _) => { exitTimer.Stop(); pet.Close(); };
                exitTimer.Start();
            }
            if (e.Args.Contains("--chat")) pet.ShowChat();
            if (e.Args.Contains("--settings")) pet.ShowSettings();
            if (e.Args.Contains("--ai-settings")) pet.ShowAiSettings();
            if (e.Args.Contains("--gacha")) pet.ShowGacha();
            if (chatSmoke != null) Dispatcher.InvokeAsync(async () => await pet.RunChatSmoke(chatSmoke));
            if (searchSmoke != null) Dispatcher.InvokeAsync(async () => await pet.RunSearchSmoke(searchSmoke));
            if (gachaSmoke != null) Dispatcher.InvokeAsync(async () => await pet.RunGachaSmoke(gachaSmoke));
            if (routineSmoke != null) Dispatcher.InvokeAsync(() => pet.RunRoutineSmoke(routineSmoke));
            if (smoke is not null) FrameworkChecks.Smoke(pet, _store, smoke);
        }
        catch (Exception ex)
        {
            _store.Log("桌宠启动失败。", ex);
            MessageBox.Show("桌宠未能启动：\n" + ex.Message + "\n\n请把 pets 文件夹与程序放在一起。", "羽川桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _registration?.Unregister(null);
        _chatRegistration?.Unregister(null);
        _aiSettingsRegistration?.Unregister(null);
        _aiSettingsEvent?.Dispose();
        _chatEvent?.Dispose();
        _showEvent?.Dispose();
        if (_mutex is not null) { _mutex.ReleaseMutex(); _mutex.Dispose(); }
        base.OnExit(e);
    }

    private static bool IsBundledDefault(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Name.Equals("default", StringComparison.OrdinalIgnoreCase) && info.Parent?.Name.Equals("pets", StringComparison.OrdinalIgnoreCase) == true &&
                info.Parent.Parent is { } applicationDirectory && File.Exists(Path.Combine(applicationDirectory.FullName, "HanekawaPet.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }
}
