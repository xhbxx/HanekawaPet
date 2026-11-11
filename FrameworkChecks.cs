using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HanekawaPet.Core;
using HanekawaPet.Platform;

namespace HanekawaPet;

internal static class FrameworkChecks
{
    public static int Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var cases = Path.Combine(output, "cases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cases);
        var results = new List<object>();
        var failed = false;
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed = true; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
            throw new InvalidDataException("无效输入未被拒绝。");
        }
        var original = Path.Combine(AppContext.BaseDirectory, "pets", "default");
        PackDefinition Fresh() => JsonSerializer.Deserialize<PackDefinition>(File.ReadAllText(Path.Combine(original, "pet.json")), PetPackLoader.JsonOptions)!;
        string Fixture(string name, Action<PackDefinition>? change = null)
        {
            var path = Path.Combine(cases, name);
            CopyPack(original, path);
            var definition = Fresh();
            change?.Invoke(definition);
            File.WriteAllText(Path.Combine(path, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
            return path;
        }

        Check("daily rest boundaries use local 24-hour time", () =>
        {
            foreach (var hour in new[] { 0d, 1d, 5.999, 13d, 13.999 }) Assert(PetRoutine.IsRestTime(TimeSpan.FromHours(hour)), "休息时段漏触发。");
            foreach (var hour in new[] { 6d, 12.999, 14d, 23.999 }) Assert(!PetRoutine.IsRestTime(TimeSpan.FromHours(hour)), "非休息时段误触发。");
        });
        Check("random yawns repeat without resetting three-hour inactivity", () =>
        {
            var routine = new PetRoutine(0, () => .5);
            Assert(!routine.YawnDue(20 * 60000 - 1) && routine.YawnDue(20 * 60000), "哈欠阈值错误。");
            routine.YawnPlayed(20 * 60000);
            Assert(!routine.YawnDue(40 * 60000 - 1) && routine.YawnDue(40 * 60000), "哈欠没有重新随机计时。");
            Assert(!routine.ShouldRest(PetRoutine.LongIdleMs, TimeSpan.FromHours(10)) && routine.ShouldRest(PetRoutine.LongIdleMs + 1, TimeSpan.FromHours(10)), "三小时闲置阈值错误。");
            var lower = new PetRoutine(0, () => 0); var upper = new PetRoutine(0, () => .999999);
            Assert(lower.NextYawnAt == 15 * 60000 && upper.NextYawnAt > 24.99 * 60000 && upper.NextYawnAt < 25 * 60000, "随机哈欠范围错误。");
        });
        Check("interaction wakes scheduled and long-idle rest and resets yawns", () =>
        {
            var routine = new PetRoutine(0, () => .5);
            Assert(routine.ShouldRest(0, TimeSpan.Zero), "在休息时段启动未休息。");
            routine.Interact(100000);
            Assert(!routine.ShouldRest(159999, TimeSpan.Zero) && routine.ShouldRest(160000, TimeSpan.Zero), "互动唤醒宽限错误。");
            Assert(!routine.YawnDue(100000 + 20 * 60000 - 1), "互动未重置哈欠。");
            routine.Interact(PetRoutine.LongIdleMs + 100001);
            Assert(!routine.ShouldRest(PetRoutine.LongIdleMs + 100002, TimeSpan.FromHours(10)), "互动后仍然处于三小时闲置休息。");
        });
        Check("rest cycle selection is 80 percent still and 10 percent each variant", () =>
        {
            var states = Enumerable.Range(0, 100).Select(i => PetRoutine.RestVariant((i + .5) / 100)).ToArray();
            Assert(states.Count(s => s == "sleep") == 80 && states.Count(s => s == "sleep-ear-twitch") == 10 && states.Count(s => s == "sleep-tail-flick") == 10, "休息动作权重错误。");
        });
        Check("rest and yawn imports preserve timing and custom animations", () =>
        {
            var bundled = PetPackLoader.Load(original);
            foreach (var state in new[] { "sleep", "sleep-ear-twitch", "sleep-tail-flick" })
                Assert(bundled.Get(state).Frames.Count == 6 && bundled.Get(state).DurationMs == 4000, "休息循环时长或帧数错误。");
            Assert(bundled.Get("yawn").Frames.Count == 12 && bundled.Get("yawn").DurationMs == 5300 && !bundled.Get("yawn").Loop, "哈欠应当只播放一次原始 12 帧。");
            var path = Fixture("rest-upgrade"); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var current = PetPackLoader.Load(path);
            var upgraded = PackEditor.ReplaceRestSet(current, Path.Combine(cases, "rest-storage"), original);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before, "升级修改了原包。");
            foreach (var state in new[] { "idle", "walk-right", "drag", "drag-left", "drag-right" })
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(upgraded.Definition.Animations[state]), "升级覆盖了其他动画。");
            Assert(upgraded.Get("sleep").DurationMs == 4000 && upgraded.Get("yawn").DurationMs == 5300, "自定义包没有接入新资源。");
        });
        Check("daily knock uses 3-7 minute intervals without delaying yawn or long-idle rest", () =>
        {
            var routine = new PetRoutine(0, () => .5); var yawnAt = routine.NextYawnAt;
            Assert(!routine.KnockDue(5 * 60000 - 1) && routine.KnockDue(5 * 60000), "敲击计时错误。");
            routine.KnockPlayed(5 * 60000);
            Assert(routine.NextKnockAt == 10 * 60000 && routine.NextYawnAt == yawnAt, "敲击干扰了哈欠计时。");
            Assert(routine.ShouldRest(PetRoutine.LongIdleMs + 1, TimeSpan.FromHours(10)), "敲击重置了三小时闲置。");
            routine.Interact(10 * 60000);
            Assert(routine.NextKnockAt == 15 * 60000 && !routine.KnockDue(10 * 60000), "互动没有重置敲击计时。");
            Assert(new PetRoutine(0, () => 0).NextKnockAt == 3 * 60000 && new PetRoutine(0, () => .999999).NextKnockAt < 7 * 60000, "敲击随机范围错误。");
        });
        Check("original knock GIF plays once and imports without replacing other actions", () =>
        {
            var pack = PetPackLoader.Load(original); var clip = pack.Get("knock");
            Assert(clip.Frames.Count == 6 && clip.DurationMs == 2180 && !clip.Loop, "敲击原始帧数、时长或循环标记错误。");
            var path = Fixture("knock-upgrade"); var current = PetPackLoader.Load(path); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var updated = PackEditor.Replace(current, Path.Combine(cases, "knock-storage"), "knock", [Path.Combine(original, "frames", "screen-knock.gif")], 180);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before && !updated.Get("knock").Loop && updated.Get("knock").DurationMs == 2180, "敲击导入修改了原包或时长。");
            foreach (var state in current.Definition.Animations.Keys.Where(s => s != "knock"))
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(updated.Definition.Animations[state]), "敲击导入覆盖了其他动作。");
        });
        Check("foot swing keeps original timing and upgrades a custom pack without changing other actions", () =>
        {
            var clip = PetPackLoader.Load(original).Get("foot-swing");
            Assert(clip.Frames.Count == 8 && clip.DurationMs == 2800 && !clip.Loop, "晃脚原始时长或循环标记错误。");
            var path = Fixture("foot-swing-upgrade", d => d.Animations.Remove("foot-swing"));
            var current = PetPackLoader.Load(path); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var updated = PackEditor.Replace(current, Path.Combine(cases, "foot-swing-storage"), "foot-swing", [Path.Combine(original, "frames", "foot-swing.gif")], 180);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before && !updated.Get("foot-swing").Loop && updated.Get("foot-swing").DurationMs == 2800, "晃脚升级修改了原包或播放方式。");
            foreach (var state in current.Definition.Animations.Keys)
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(updated.Definition.Animations[state]), "晃脚升级覆盖了其他动作。");
        });
        Check("wall climbing preserves supplied GIF and upgrades both sides without changing other actions", () =>
        {
            var pack = PetPackLoader.Load(original); var clip = pack.Get("climb-left");
            Assert(clip.Frames.Count == 9 && clip.DurationMs == 880 && clip.Loop && pack.Get("climb-right").Mirror, "爬墙帧数、时长或左右朝向错误。");
            var path = Fixture("climb-upgrade", d => { d.Animations.Remove("climb-left"); d.Animations.Remove("climb-right"); });
            var current = PetPackLoader.Load(path); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var updated = PackEditor.ReplaceClimbSet(current, Path.Combine(cases, "climb-storage"), original);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before && updated.Get("climb-left").DurationMs == 880 && updated.Get("climb-right").Mirror, "爬墙升级改动了原包或缺少左右动作。");
            foreach (var state in current.Definition.Animations.Keys)
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(updated.Definition.Animations[state]), "爬墙升级覆盖了其他动作。");
        });
        Check("head pat preserves transparent source timings and upgrades only that action", () =>
        {
            var pack = PetPackLoader.Load(original); var clip = pack.Get("head-pat");
            Assert(clip.Frames.Count == 12 && clip.DurationMs == 4860 && !clip.Loop, "摸头帧数、时长或循环标记错误。");
            Assert(clip.Frames.Select(f => f.DurationMs).SequenceEqual(new[] {600,160,300,350,650,450,450,350,350,300,300,600}) && Pixel(clip.Frames[0].Image, 0, 0)[3] == 0, "摸头透明度或原始时长被改变。");
            var path = Fixture("head-pat-upgrade", d => d.Animations.Remove("head-pat"));
            var current = PetPackLoader.Load(path); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var updated = PackEditor.Replace(current, Path.Combine(cases, "head-pat-storage"), "head-pat", [Path.Combine(original, "frames/head-pat.gif")], 180);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before && !updated.Get("head-pat").Loop, "摸头升级改动了原包或循环方式。");
            foreach (var state in current.Definition.Animations.Keys)
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(updated.Definition.Animations[state]), "摸头升级覆盖了其他动作。");
        });
        Check("cheek poke preserves supplied transparent GIF timings and other custom actions", () =>
        {
            var pack = PetPackLoader.Load(original); var clip = pack.Get("cheek-block");
            Assert(clip.Frames.Count == 12 && clip.DurationMs == 4250 && !clip.Loop, "戳脸帧数、时长或循环方式错误。");
            Assert(clip.Frames.Select(f => f.DurationMs).SequenceEqual(new[] {550,250,250,250,300,400,550,350,250,250,300,550}) && Pixel(clip.Frames[0].Image, 0, 0)[3] == 0, "戳脸透明度或原始时长被改变。");
            var path = Fixture("cheek-upgrade", d => d.Animations.Remove("cheek-block"));
            var current = PetPackLoader.Load(path); var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var updated = PackEditor.Replace(current, Path.Combine(cases, "cheek-storage"), "cheek-block", [Path.Combine(original, "frames/cheek-block.gif")], 180);
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before && !updated.Get("cheek-block").Loop && updated.Get("cheek-block").DurationMs == 4250, "戳脸升级改动了原包或播放方式。");
            foreach (var state in current.Definition.Animations.Keys)
                Assert(JsonSerializer.Serialize(current.Definition.Animations[state]) == JsonSerializer.Serialize(updated.Definition.Animations[state]), "戳脸升级覆盖了其他动作。");
        });
        Check("default pack and optional-state fallback", () =>
        {
            var pack = PetPackLoader.Load(original);
            Assert(pack.Clips.Count >= 7, "默认动作缺失。");
            Assert(pack.Get("walk-right").Frames.Count == 8, "走路帧数错误。");
            Assert(ReferenceEquals(pack.Get("unknown-state"), pack.Get("idle")), "待机回退错误。");
            foreach (var clip in pack.Clips.Values) Assert(clip.DurationMs > 0 && clip.Frames.All(frame => frame.Image.IsFrozen), "图片未冻结或时长无效。");
        });
        Check("supplied drag set preserves other custom actions and original pack", () =>
        {
            var path = Fixture("drag-upgrade");
            var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var current = PetPackLoader.Load(path);
            var updated = PackEditor.ReplaceDragSet(current, Path.Combine(cases, "drag-storage"), original);
            Assert(updated.Get("drag").Frames.Count == 6 && updated.Get("drag-left").Frames.Count == 8 && updated.Get("drag-right").Frames.Count == 8, "拖拽帧数错误。");
            Assert(updated.Get("walk-right").Frames.Count == current.Get("walk-right").Frames.Count && updated.Get("idle").DurationMs == current.Get("idle").DurationMs, "升级改动了其他动作。");
            Assert(before == File.ReadAllText(Path.Combine(path, "pet.json")), "原有资源包被修改。");
        });
        Check("variable frame durations and loop seam", () =>
        {
            var image = PetPackLoader.Load(original).Get("idle").Frames[0].Image;
            var frames = new[] { new AnimationFrame(image, 40), new AnimationFrame(image, 80), new AnimationFrame(image, 160) };
            var clip = new AnimationClip(frames, true);
            Assert(ReferenceEquals(clip.At(39), frames[0]) && ReferenceEquals(clip.At(40), frames[1]), "第一帧边界错误。");
            Assert(ReferenceEquals(clip.At(119), frames[1]) && ReferenceEquals(clip.At(120), frames[2]), "第二帧边界错误。");
            Assert(ReferenceEquals(clip.At(280), frames[0]), "循环衔接错误。");
            Assert(ReferenceEquals(new AnimationClip(frames, false).At(10000), frames[2]), "非循环动画未停在末帧。");
        });
        Check("leftward mirror preserves original frame order", () =>
        {
            var pack = PetPackLoader.Load(Fixture("mirror", d => d.Animations.Remove("walk-left")));
            var right = pack.Get("walk-right");
            var left = pack.Get("walk-left");
            Assert(left.Mirror && ReferenceEquals(right.Frames, left.Frames), "镜像颠倒了时间顺序。");
            Assert(pack.Get("idle").Mirror == false, "镜像影响了待机。");
        });
        Check("spritesheet bounds and frame ordering", () =>
        {
            var path = Fixture("sheet");
            WriteColorSheet(Path.Combine(path, "sheet.png"));
            var definition = Fresh();
            definition.Animations["test"] = new() { Sheet = new() { File = "sheet.png", FrameWidth = 10, FrameHeight = 10, Columns = 3, Count = 3, DurationsMs = [40, 80, 120] } };
            File.WriteAllText(Path.Combine(path, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
            var clip = PetPackLoader.Load(path).Get("test");
            Assert(clip.Frames.Count == 3 && Pixel(clip.Frames[0].Image, 5, 5)[2] == 255 && Pixel(clip.Frames[2].Image, 5, 5)[0] == 255, "精灵图帧顺序错误。");
            definition.Animations["test"].Sheet!.Count = 4;
            File.WriteAllText(Path.Combine(path, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
            Reject(() => PetPackLoader.Load(path));
        });
        Check("reject traversal, mirror cycles and invalid durations", () =>
        {
            Reject(() => PetPackLoader.Load(Fixture("traversal", d => d.Animations["idle"] = new() { File = "../outside.png" })));
            Reject(() => PetPackLoader.Load(Fixture("cycle", d => { d.Animations["a"] = new() { MirrorOf = "b" }; d.Animations["b"] = new() { MirrorOf = "a" }; })));
            Reject(() => PetPackLoader.Load(Fixture("duration", d => d.Animations["idle"].Frames![0].DurationMs = 0)));
            Reject(() => PetPackLoader.Load(Fixture("schema", d => d.SchemaVersion = 99)));
        });
        Check("loaded images release disk files for replacement", () =>
        {
            var path = Fixture("unlocked");
            PetPackLoader.Load(path);
            using var exclusive = new FileStream(Path.Combine(path, "frames", "idle.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert(exclusive.Length > 0, "图片文件未释放。");
        });
        Check("settings round trip, invalid numbers and broken JSON recovery", () =>
        {
            var store = new SettingsStore(Path.Combine(cases, "settings"));
            store.Save(new() { Left = double.NaN, Scale = double.NaN, Speed = double.PositiveInfinity, Movement = (MovementMode)999 });
            var restored = store.Load();
            Assert(restored.Left is null && restored.Scale == 1 && restored.Speed == 28 && restored.Movement == MovementMode.Stay, "设置规范化失败。");
            restored.Scale = 1.4; restored.ClickThrough = true; restored.Movement = MovementMode.Wander;
            store.Save(restored);
            Assert(store.Load().Scale == 1.4 && store.Load().ClickThrough && store.Load().Movement == MovementMode.Wander, "设置恢复失败。");
            File.WriteAllText(store.FilePath, "{");
            Assert(store.Load().Scale == 1, "损坏设置未恢复。");
        });
        Check("PNG action import is reversible and naturally sorted", () =>
        {
            var path = Fixture("import-source");
            var before = File.ReadAllText(Path.Combine(path, "pet.json"));
            var incoming = Path.Combine(cases, "incoming"); Directory.CreateDirectory(incoming);
            WriteSolid(Path.Combine(incoming, "frame1.png"), 255, 0, 0);
            WriteSolid(Path.Combine(incoming, "frame2.png"), 0, 255, 0);
            WriteSolid(Path.Combine(incoming, "frame10.png"), 0, 0, 255);
            var pack = PackEditor.Replace(PetPackLoader.Load(path), Path.Combine(cases, "imports"), "walk-right",
                [Path.Combine(incoming, "frame10.png"), Path.Combine(incoming, "frame1.png"), Path.Combine(incoming, "frame2.png")], 120);
            var frames = pack.Get("walk-right").Frames;
            Assert(frames.Count == 3 && Pixel(frames[0].Image, 0, 0)[2] == 255 && Pixel(frames[1].Image, 0, 0)[1] == 255 && Pixel(frames[2].Image, 0, 0)[0] == 255, "导入排序错误。");
            Assert(File.ReadAllText(Path.Combine(path, "pet.json")) == before, "导入覆盖了原始资源。");
            Assert(pack.Get("walk-left").Mirror && pack.Get("walk-left").Frames.Count == 3, "导入后镜像未更新。");
        });
        Check("GIF timing, transparency and disposal composition", () =>
        {
            var fixtures = Path.Combine(output, "fixtures");
            var frames = GifFrames.Load(Path.Combine(fixtures, "disposal.gif"));
            Assert(frames.Count == 4, "GIF 帧数错误。");
            var delays = new[] { 40, 90, 130, 70 };
            for (var index = 0; index < frames.Count; index++)
            {
                var expected = new BitmapImage(); expected.BeginInit(); expected.UriSource = new Uri(Path.Combine(fixtures, $"expected-{index}.png")); expected.CacheOption = BitmapCacheOption.OnLoad; expected.EndInit();
                Assert(frames[index].DurationMs == delays[index], "GIF 时长错误。");
                for (var y = 0; y < expected.PixelHeight; y++) for (var x = 0; x < expected.PixelWidth; x++)
                {
                    var actualPixel = Pixel(frames[index].Image, x, y); var expectedPixel = Pixel(expected, x, y);
                    Assert(actualPixel[3] == expectedPixel[3] && (expectedPixel[3] == 0 || actualPixel.SequenceEqual(expectedPixel)), $"GIF 合成错误：帧 {index}，({x},{y})。");
                }
            }
        });
        File.WriteAllText(Path.Combine(output, "framework-checks.json"), JsonSerializer.Serialize(new { ok = !failed, checks = results }, PetPackLoader.JsonOptions));
        return failed ? 1 : 0;
    }

    public static void Smoke(PetWindow pet, SettingsStore store, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var checks = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        var tick = 0;
        double start = 0;
        PetPack? lastValid = null;
        PetPack? beforeReload = null;
        string? validJson = null;
        var livePack = Path.Combine(outputDirectory, "live-pack");
        void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add(message); }
        void Finish(Exception? error)
        {
            timer.Stop();
            File.WriteAllText(Path.Combine(outputDirectory, "smoke-report.json"), JsonSerializer.Serialize(new { ok = error is null, checks, error = error?.ToString() }, PetPackLoader.JsonOptions));
            pet.Close();
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                switch (tick++)
                {
                    case 0:
                        var area = DesktopPlatform.WorkArea(pet);
                        pet.Left = area.Left + 30; pet.Top = area.Bottom - pet.Height - 15;
                        start = pet.Left;
                        pet.SetSpeed(50); pet.SetMovement(MovementMode.Walk, 1);
                        break;
                    case 4:
                        Assert(pet.Left > start + 10, "rightward movement is time based and advances");
                        pet.SetMovement(MovementMode.Stay);
                        pet.SetClickThrough(true); Assert(DesktopPlatform.IsClickThrough(pet), "native click-through enabled");
                        pet.SetClickThrough(false); Assert(!DesktopPlatform.IsClickThrough(pet), "native click-through recovers");
                        pet.SetScale(1.15); pet.SetTopmost(false); pet.PlayAction("wave");
                        Capture(pet, Path.Combine(outputDirectory, "desktop-pet.png"));
                        CopyPack(Path.Combine(AppContext.BaseDirectory, "pets", "default"), livePack);
                        Assert(pet.LoadPack(livePack, false), "resource pack switches without restart");
                        beforeReload = pet.Pack;
                        var config = JsonSerializer.Deserialize<PackDefinition>(JsonSerializer.Serialize(pet.Pack.Definition, PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
                        config.Name = "Smoke reload verification";
                        validJson = JsonSerializer.Serialize(config, PetPackLoader.JsonOptions);
                        File.WriteAllText(Path.Combine(livePack, "pet.json"), validJson);
                        break;
                    case 8:
                        Assert(pet.Pack.Name == "Smoke reload verification" && !ReferenceEquals(beforeReload, pet.Pack), "file watcher reloads modified pack");
                        lastValid = pet.Pack;
                        File.WriteAllText(Path.Combine(livePack, "pet.json"), "{");
                        break;
                    case 12:
                        Assert(ReferenceEquals(lastValid, pet.Pack), "invalid live edit preserves last valid pack");
                        File.WriteAllText(Path.Combine(livePack, "pet.json"), validJson);
                        break;
                    case 16:
                        Assert(pet.Pack.Name == "Smoke reload verification" && !ReferenceEquals(lastValid, pet.Pack), "live reload recovers after repair");
                        Assert(File.Exists(store.FilePath), "preferences saved during session");
                        Finish(null);
                        break;
                }
            }
            catch (Exception ex) { Finish(ex); }
        };
        timer.Start();
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var result = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), result, 4, 0); return result;
    }

    private static void WriteColorSheet(string path)
    {
        var pixels = new byte[30 * 10 * 4];
        for (var y = 0; y < 10; y++) for (var x = 0; x < 30; x++)
        { var p = (y * 30 + x) * 4; pixels[p + (x < 10 ? 2 : x < 20 ? 1 : 0)] = 255; pixels[p + 3] = 255; }
        WritePng(path, BitmapSource.Create(30, 10, 96, 96, PixelFormats.Bgra32, null, pixels, 120));
    }

    private static void WriteSolid(string path, byte red, byte green, byte blue)
    {
        var data = Enumerable.Range(0, 16).SelectMany(_ => new byte[] { blue, green, red, 255 }).ToArray();
        WritePng(path, BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, data, 16));
    }

    private static void WritePng(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void CopyPack(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
    }
}
