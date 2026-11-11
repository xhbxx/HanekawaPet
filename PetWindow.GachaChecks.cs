using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class PetWindow
{
    internal async Task RunGachaSmoke(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>(); var failed = false;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failed = true; }
        bool Rejected(Action action) { try { action(); return false; } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return true; } }
        void Capture(string name, Window window)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); png.Save(output);
        }
        void ImageFixture(string path)
        {
            var pixels = Enumerable.Range(0, 64 * 64).SelectMany(_ => new byte[] { 202, 170, 108, 255 }).ToArray();
            var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); png.Save(output);
        }
        async Task WaitResult(GachaWindow window)
        {
            for (var i = 0; i < 800 && window.ResultWindow == null; i++) await Task.Delay(25);
            if (window.ResultWindow == null) throw new InvalidDataException("结果弹窗没有出现。");
        }
        try
        {
            var catalogRoot = Path.Combine(directory, "fixtures"); Directory.CreateDirectory(catalogRoot);
            var catalogFile = Path.Combine(catalogRoot, "catalog.json");
            void Catalog(string? animation = null) => File.WriteAllText(catalogFile, JsonSerializer.Serialize(new GachaCatalog
            { Animation = animation, Prizes = [new("test-item", "测试藏品", "item.png", "仅用于隔离测试的插图。")] }, GachaStore.Json));
            File.WriteAllText(catalogFile, "{\"prizes\":[]}");
            var now = new DateTimeOffset(2026, 10, 24, 23, 59, 59, TimeSpan.FromHours(8));
            var coreRoot = Path.Combine(directory, "core-data");
            var store = new GachaStore(coreRoot, () => now, _ => 0);
            Check("empty prize pool preserves the daily allowance", Rejected(() => store.Draw(catalogRoot)) && store.Read().Draws.Count == 0 && !store.HasDrawnToday(store.Read()));
            ImageFixture(Path.Combine(catalogRoot, "item.png")); Catalog();
            var won = store.Draw(catalogRoot);
            Check("draw persists result, pending presentation, and a durable illustration copy", store.Read().Draws.Count == 1 && store.Read().PendingDrawId == won.Id && File.Exists(GachaCatalog.Resolve(store.Root, won.Image)));
            var reopened = new GachaStore(coreRoot, () => now, _ => 0);
            Check("restart and repeated requests cannot draw twice on the same day", Rejected(() => reopened.Draw(catalogRoot)) && reopened.Read().Draws.Count == 1);
            now = now.AddSeconds(1);
            Check("Beijing midnight refreshes the allowance", store.Today == "2026-10-25" && !store.HasDrawnToday(store.Read()));
            store.Draw(catalogRoot);
            Check("repeated prizes accumulate without replacing earlier wins", store.Read().Draws.Count == 2 && store.Read().Draws.Select(d => d.PrizeId).Distinct().Count() == 1);
            now = now.AddDays(-1);
            Check("clock rollback does not reopen a used daily allowance", Rejected(() => store.Draw(catalogRoot)));
            var pending = store.Read().PendingDrawId!; store.Acknowledge(pending);
            Check("acknowledging a result preserves collection and daily usage", store.Read().PendingDrawId == null && store.Read().Draws.Count == 2 && store.HasDrawnToday(store.Read()));
            File.WriteAllText(Path.Combine(catalogRoot, "item.png"), "image replaced in source pool");
            GachaCatalog.ValidateImage(GachaCatalog.Resolve(store.Root, won.Image));
            Check("collection artwork survives source asset replacement", File.Exists(GachaCatalog.Resolve(store.Root, won.Image)));
            ImageFixture(Path.Combine(catalogRoot, "item.png"));
            var concurrentRoot = Path.Combine(directory, "concurrent-data");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try { new GachaStore(concurrentRoot, () => now, _ => 0).Draw(catalogRoot); return true; }
                catch (InvalidOperationException) { return false; }
            })));
            Check("simultaneous stores commit exactly one draw per day", concurrent.Count(x => x) == 1 && new GachaStore(concurrentRoot).Read().Draws.Count == 1);
            var brokenRoot = Path.Combine(directory, "broken-data"); var broken = new GachaStore(brokenRoot);
            File.WriteAllText(broken.StatePath, "{broken");
            Check("corrupt records are preserved and never silently reset the quota", Rejected(() => broken.Read()) && File.ReadAllText(broken.StatePath) == "{broken");
            File.WriteAllText(catalogFile, "{\"prizes\":[{\"id\":\"bad\",\"name\":\"bad\",\"image\":\"../outside.png\"}]}");
            Check("catalog rejects image path traversal", Rejected(() => GachaCatalog.Load(catalogRoot)));
            Catalog();

            var menu = CreatePetMenu();
            Check("right-click root has exactly settings and gacha", menu.Items.OfType<MenuItem>().Select(i => i.Header.ToString()).SequenceEqual(new[] { "设置", "扭蛋" }));
            var settingsMenu = (MenuItem)menu.Items[0];
            Check("settings submenu retains chat, AI, animation, movement, and exit", new[] { "聊天窗口", "AI 接口与知识库", "桌宠设置与动画", "向右慢走", "退出" }.All(h => settingsMenu.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == h)));
            ShowGacha(); var window = _gachaWindow!; await window.Ready; await Task.Delay(50);
            Check("production prize pool enables drawing without consuming a chance", window.DrawButton.IsEnabled && !_gachaStore!.HasDrawnToday(_gachaStore.Read()));
            var productionRoot = Path.Combine(AppContext.BaseDirectory, "gacha");
            var productionCatalog = GachaCatalog.Load(productionRoot);
            Check("ten supplied prize illustrations have equal weights and stable IDs", productionCatalog.Prizes.Count == 10 && productionCatalog.Prizes.All(p => p.Weight == 1) && productionCatalog.Prizes.Select(p => p.Id).Distinct().Count() == 10);
            Check("all four supplied arcade animations are reachable by the uniform selector", productionCatalog.AnimationFiles.Count == 4 && Enumerable.Range(0, 4).Select(i => productionCatalog.SelectAnimation(_ => i)).Distinct().Count() == 4);
            foreach (var animation in productionCatalog.AnimationFiles)
            {
                var frames = GifFrames.Load(GachaCatalog.Resolve(productionRoot, animation), 320);
                Check("arcade GIF preserves 240 frames and eight-second timing: " + animation, frames.Count == 240 && frames.Sum(f => f.DurationMs) == 8000 && frames.All(f => f.Image.PixelWidth == 320 && f.Image.PixelHeight == 320));
            }
            var preview = window.Preview(); await Task.Delay(120);
            Check("preview runs without committing a prize or enabling a parallel draw", window.IsDrawing && window.AnimationImage.Visibility == Visibility.Visible && !window.PreviewButton.IsEnabled && !_gachaStore!.HasDrawnToday(_gachaStore.Read()));
            Capture("gacha-arcade-playing", window);
            await window.Preview(); await preview;
            Check("preview finishes without changing daily quota or collection", !window.IsDrawing && window.ResultWindow == null && _gachaStore!.Read().Draws.Count == 0 && window.PreviewButton.IsEnabled);
            Capture("gacha-empty", window); window.Pages.SelectedIndex = 1; Capture("cabinet-empty", window);
            Check("empty display cabinet keeps three shelves with four slots each", window.CabinetRows.Children.Count == 3);
            SetMovement(MovementMode.Walk, 1); var left = Left; var time = Now; _routine.Interact(time - 8 * 60000);
            Advance(time, TimeSpan.FromHours(10), climbSample: 0);
            Check("gacha window pauses wandering and random actions", Left == left && _action == null && _climbSide == 0);
            SetMovement(MovementMode.Stay); window.Close();

            now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));
            var uiRoot = Path.Combine(directory, "ui-data"); var uiStore = new GachaStore(uiRoot, () => now, _ => 0);
            window = new GachaWindow(uiStore, catalogRoot) { Owner = this }; _gachaWindow = window;
            window.Show(); await window.Ready;
            var draw = window.Draw(); await Task.Delay(80);
            Check("drawing reserves quota and plays before opening the result", window.IsDrawing && window.DrawingVeil.Visibility == Visibility.Visible && window.ResultWindow == null && uiStore.Read().Draws.Count == 1);
            await window.Draw(); Check("rapid repeated clicks cannot consume another draw", uiStore.Read().Draws.Count == 1);
            await WaitResult(window); Capture("gacha-result", window.ResultWindow!); window.ResultWindow!.Close(); await draw;
            Check("result popup finishes before collection refresh and disables today's draw", !window.IsDrawing && !window.DrawButton.IsEnabled && uiStore.Read().PendingDrawId == null);
            window.Pages.SelectedIndex = 1; Capture("cabinet-collected", window);
            Check("collected illustration appears in a shelf with quantity", window.CollectionText.Text.Contains("1 种") && window.CollectionText.Text.Contains("1 件"));
            now = now.AddDays(1); Catalog("machine.gif");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "pets/default/frames/screen-knock.gif"), Path.Combine(catalogRoot, "machine.gif"), true);
            window.RefreshView(); window.Pages.SelectedIndex = 0;
            draw = window.Draw(); await Task.Delay(80);
            Check("configured GIF replaces the reserved animation area", window.AnimationImage.Source != null && window.AnimationImage.Visibility == Visibility.Visible && window.ResultWindow == null);
            window.Close(); await draw;
            Check("closing during animation keeps the earned prize and used chance", uiStore.Read().Draws.Count == 2 && uiStore.Read().PendingDrawId != null && uiStore.HasDrawnToday(uiStore.Read()));
            window = new GachaWindow(new GachaStore(uiRoot, () => now), catalogRoot) { Owner = this }; _gachaWindow = window;
            window.Show(); await window.Ready;
            Check("reopened window offers the saved result and keeps duplicate quantity", window.PendingButton.Visibility == Visibility.Visible && !window.DrawButton.IsEnabled && window.CollectionText.Text.Contains("2 件"));
            var pendingOpen = Dispatcher.InvokeAsync(() => window.PendingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitResult(window); window.ResultWindow!.Close(); await pendingOpen.Task;
            Check("saved result can be opened and acknowledged after restart", uiStore.Read().PendingDrawId == null);
            now = now.AddDays(1);
            foreach (var animation in productionCatalog.AnimationFiles)
                File.Copy(GachaCatalog.Resolve(productionRoot, animation), Path.Combine(catalogRoot, Path.GetFileName(animation)), true);
            File.WriteAllText(catalogFile, JsonSerializer.Serialize(new GachaCatalog { Animations = productionCatalog.AnimationFiles.Select(Path.GetFileName).Select(x => x!).ToList(), Prizes = [new("test-item", "测试藏品", "item.png")] }, GachaStore.Json));
            window.RefreshView(); window.Pages.SelectedIndex = 0;
            var playback = System.Diagnostics.Stopwatch.StartNew(); draw = window.Draw(); await Task.Delay(120);
            Check("real draw randomly selects one of four GIFs before showing its result", window.IsDrawing && window.ResultWindow == null && productionCatalog.AnimationFiles.Any(a => Path.GetFileName(a) == window.LastAnimation) && uiStore.Read().Draws.Count == 3);
            await WaitResult(window);
            Check("eight-second arcade animation completes before result popup", playback.ElapsedMilliseconds >= 7900);
            window.ResultWindow!.Close(); await draw;
            Check("arcade draw preserves once-per-day quota and cabinet quantity", !window.DrawButton.IsEnabled && uiStore.Read().Draws.Count == 3 && window.CollectionText.Text.Contains("3 件"));
            window.Close(); _gachaWindow = null;
            var prizeDay = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)); var prizeIndex = 0;
            var prizeStore = new GachaStore(Path.Combine(directory, "ten-prizes-data"), () => prizeDay, _ => prizeIndex);
            for (prizeIndex = 0; prizeIndex < 10; prizeIndex++)
            {
                var earned = prizeStore.Draw(productionRoot); prizeStore.Acknowledge(earned.Id); prizeDay = prizeDay.AddDays(1);
            }
            Check("all ten production prizes can be drawn and saved independently", prizeStore.Read().Draws.Select(d => d.PrizeId).Distinct().Count() == 10 && prizeStore.Read().Draws.All(d => File.Exists(GachaCatalog.Resolve(prizeStore.Root, d.Image))));
            window = new GachaWindow(prizeStore, productionRoot) { Owner = this }; _gachaWindow = window;
            window.Show(); await window.Ready; window.Pages.SelectedIndex = 1; Capture("cabinet-ten-prizes", window);
            Check("production illustrations fill ten cabinet slots", window.CollectionText.Text.Contains("10 种") && window.CollectionText.Text.Contains("10 件") && window.CabinetRows.Children.Count == 3);
            window.Close(); _gachaWindow = null;
        }
        catch (Exception ex) { failed = true; checks.Add(new { name = "gacha runtime", passed = false, error = ex.ToString() }); }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failed ? 1 : 0);
    }
}
