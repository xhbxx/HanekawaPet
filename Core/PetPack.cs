using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HanekawaPet.Core;

public sealed class PackDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "桌宠";
    public CanvasDefinition Canvas { get; set; } = new();
    public Dictionary<string, AnimationDefinition> Animations { get; set; } = new();
}

public sealed class CanvasDefinition
{
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 220;
}

public sealed class AnimationDefinition
{
    public string? File { get; set; }
    public List<FrameDefinition>? Frames { get; set; }
    public SheetDefinition? Sheet { get; set; }
    public string? MirrorOf { get; set; }
    public bool Loop { get; set; } = true;
    public int DefaultFrameDurationMs { get; set; } = 180;
}

public sealed class FrameDefinition
{
    public string File { get; set; } = "";
    public int DurationMs { get; set; } = 180;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}

public sealed class SheetDefinition
{
    public string File { get; set; } = "";
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public int Columns { get; set; }
    public int Start { get; set; }
    public int Count { get; set; }
    public List<int>? DurationsMs { get; set; }
}

public sealed record AnimationFrame(BitmapSource Image, int DurationMs, double OffsetX = 0, double OffsetY = 0);

public sealed class AnimationClip(IReadOnlyList<AnimationFrame> frames, bool loop, bool mirror = false)
{
    public IReadOnlyList<AnimationFrame> Frames { get; } = frames;
    public bool Loop { get; } = loop;
    public bool Mirror { get; } = mirror;
    public double DurationMs { get; } = frames.Sum(frame => (double)frame.DurationMs);

    public AnimationFrame At(double elapsedMs)
    {
        var time = Math.Max(0, elapsedMs);
        if (Loop) time %= DurationMs;
        else if (time >= DurationMs) return Frames[^1];
        foreach (var frame in Frames)
        {
            if (time < frame.DurationMs) return frame;
            time -= frame.DurationMs;
        }
        return Frames[^1];
    }

    public AnimationClip Mirrored() => new(Frames, Loop, !Mirror);
}

public sealed class PetPack(string directory, PackDefinition definition, Dictionary<string, AnimationClip> clips)
{
    public string Directory { get; } = directory;
    public PackDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public IReadOnlyDictionary<string, AnimationClip> Clips => clips;
    public AnimationClip Get(string state)
    {
        if (clips.TryGetValue(state, out var clip)) return clip;
        if (state == "walk-left" && clips.TryGetValue("walk-right", out clip)) return clip.Mirrored();
        if (state == "walk-right" && clips.TryGetValue("walk-left", out clip)) return clip.Mirrored();
        return clips["idle"];
    }
}

public static class PetPackLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static PetPack Load(string directory)
    {
        directory = Path.GetFullPath(directory);
        var definition = JsonSerializer.Deserialize<PackDefinition>(
            File.ReadAllText(Path.Combine(directory, "pet.json")), JsonOptions)
            ?? throw new InvalidDataException("pet.json 内容为空。");
        if (definition.SchemaVersion != 1) throw new InvalidDataException("不支持这个资源包版本。");
        if (definition.Canvas is null || definition.Canvas.Width is < 32 or > 2048 || definition.Canvas.Height is < 32 or > 2048)
            throw new InvalidDataException("画布宽高须在 32–2048 之间。");
        if (definition.Animations is null || !definition.Animations.ContainsKey("idle"))
            throw new InvalidDataException("资源包需要 idle 待机动作。");
        if (definition.Animations.Count > 64) throw new InvalidDataException("动作数量不能超过 64。");
        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        var loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        long decodedPixels = 0;

        AnimationClip Read(string state)
        {
            if (clips.TryGetValue(state, out var existing)) return existing;
            if (!definition.Animations.TryGetValue(state, out var config) || config is null)
                throw new InvalidDataException($"找不到动作：{state}");
            if (!loading.Add(state)) throw new InvalidDataException($"动作引用形成循环：{state}");
            AnimationClip clip;
            if (!string.IsNullOrWhiteSpace(config.MirrorOf)) clip = Read(config.MirrorOf).Mirrored();
            else
            {
                var modes = (config.File is null ? 0 : 1) + (config.Frames is null ? 0 : 1) + (config.Sheet is null ? 0 : 1);
                if (modes != 1) throw new InvalidDataException($"动作 {state} 须且只能选择 file、frames、sheet 中的一种。");
                var frames = new List<AnimationFrame>();
                if (config.Frames is { } sequence)
                {
                    foreach (var frame in sequence)
                    {
                        if (frame is null || !double.IsFinite(frame.OffsetX) || !double.IsFinite(frame.OffsetY))
                            throw new InvalidDataException($"动作 {state} 的偏移量无效。");
                        frames.Add(new(LoadStill(frame.File), Duration(frame.DurationMs), frame.OffsetX, frame.OffsetY));
                    }
                }
                else if (config.Sheet is { } sheet)
                {
                    var image = LoadStill(sheet.File);
                    if (sheet.FrameWidth <= 0 || sheet.FrameHeight <= 0 || sheet.Columns <= 0 ||
                        sheet.Start < 0 || sheet.Count is < 1 or > 512 || sheet.Columns > image.PixelWidth / sheet.FrameWidth ||
                        (long)sheet.Start + sheet.Count > (long)sheet.Columns * (image.PixelHeight / sheet.FrameHeight))
                        throw new InvalidDataException($"动作 {state} 的精灵图格子超出了图片范围。");
                    if (sheet.DurationsMs is not null && sheet.DurationsMs.Count != sheet.Count)
                        throw new InvalidDataException($"动作 {state} 的时长数量与帧数不一致。");
                    for (var index = 0; index < sheet.Count; index++)
                    {
                        var cell = sheet.Start + index;
                        var crop = new CroppedBitmap(image, new Int32Rect(cell % sheet.Columns * sheet.FrameWidth,
                            cell / sheet.Columns * sheet.FrameHeight, sheet.FrameWidth, sheet.FrameHeight));
                        crop.Freeze();
                        frames.Add(new(crop, Duration(sheet.DurationsMs?[index] ?? config.DefaultFrameDurationMs)));
                    }
                }
                else
                {
                    var path = Resolve(directory, config.File!);
                    if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                        frames.AddRange(GifFrames.Load(path));
                    else frames.Add(new(LoadStill(config.File!), Duration(config.DefaultFrameDurationMs)));
                }
                if (frames.Count is < 1 or > 512) throw new InvalidDataException($"动作 {state} 需要 1–512 帧。");
                foreach (var frame in frames)
                {
                    decodedPixels += (long)frame.Image.PixelWidth * frame.Image.PixelHeight;
                    if (decodedPixels > 100_000_000) throw new InvalidDataException("动画解码尺寸过大，请缩小图片或减少帧数。");
                }
                clip = new(frames, config.Loop);
            }
            loading.Remove(state);
            clips.Add(state, clip);
            return clip;
        }

        BitmapSource LoadStill(string relative)
        {
            var path = Resolve(directory, relative);
            if (cache.TryGetValue(path, out var image)) return image;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                throw new InvalidDataException("序列帧和精灵图请使用 PNG；动画文件请通过 file 字段使用 GIF。");
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) throw new InvalidDataException("单张图片边长不能超过 4096。");
            frame.Freeze();
            cache[path] = frame;
            return frame;
        }

        foreach (var state in definition.Animations.Keys) Read(state);
        return new(directory, definition, clips);
    }

    public static string Resolve(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("图片路径须是资源包内的相对路径。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("图片路径超出了资源包目录。");
        return path;
    }

    private static int Duration(int duration) => duration is >= 20 and <= 60000
        ? duration : throw new InvalidDataException("每帧时长须在 20–60000 毫秒之间。");
}

internal static class GifFrames
{
    public static IReadOnlyList<AnimationFrame> Load(string path, int maxDimension = 2048)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[10];
        stream.ReadExactly(header);
        stream.Position = 0;
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count is < 1 or > 512) throw new InvalidDataException("GIF 需要 1–512 帧。");
        var width = header[6] | header[7] << 8;
        var height = header[8] | header[9] << 8;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048 || (long)width * height * decoder.Frames.Count > 100_000_000)
            throw new InvalidDataException("GIF 解码尺寸过大，请缩小动画。");
        var scale = Math.Min(1d, Math.Clamp(maxDimension, 1, 2048) / (double)Math.Max(width, height));
        BitmapSource? canvas = null;
        var result = new List<AnimationFrame>();
        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            var rect = new Rect(Query(meta, "/imgdesc/Left", 0), Query(meta, "/imgdesc/Top", 0),
                Query(meta, "/imgdesc/Width", frame.PixelWidth), Query(meta, "/imgdesc/Height", frame.PixelHeight));
            if (rect.Right > width || rect.Bottom > height || rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidDataException("GIF 的帧位置超出了画布。");
            var previous = canvas;
            var composite = Draw(width, height, dc =>
            {
                if (previous is not null) dc.DrawImage(previous, new Rect(0, 0, width, height));
                dc.DrawImage(frame, rect);
            }, scale);
            result.Add(new(composite, Math.Clamp(Query(meta, "/grctlext/Delay", 10) * 10, 20, 60000)));
            var disposal = Query(meta, "/grctlext/Disposal", 0);
            canvas = disposal switch
            {
                2 => Draw(width, height, dc =>
                {
                    dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(rect)));
                    dc.DrawImage(composite, new Rect(0, 0, width, height));
                    dc.Pop();
                }, scale),
                3 => previous,
                _ => composite
            };
        }
        return result;
    }

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint, double scale = 1)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) paint(context);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static int Query(BitmapMetadata? metadata, string path, int fallback)
    {
        try { return metadata?.GetQuery(path) is { } value ? Convert.ToInt32(value) : fallback; }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or FormatException)
        { return fallback; }
    }
}
