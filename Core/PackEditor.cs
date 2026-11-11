using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HanekawaPet.Core;

public static class PackEditor
{
    public static PetPack Replace(PetPack current, string storageRoot, string state,
        IReadOnlyList<string> files, int frameDurationMs)
    {
        if (files.Count is < 1 or > 512) throw new InvalidDataException("请选择 1–512 个图片文件。");
        if (!Regex.IsMatch(state, "^[a-z][a-z0-9-]{0,39}$")) throw new InvalidDataException("动作名称无效。");
        var isGif = files.Count == 1 && Path.GetExtension(files[0]).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        if (!isGif && files.Any(file => !Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请选择一个 GIF，或一组 PNG 序列帧。");
        if (frameDurationMs is < 20 or > 2000) throw new InvalidDataException("序列帧时长须为 20–2000 毫秒。");

        // Each import builds a fresh pack. A failed import cannot damage the active one.
        var target = Path.Combine(storageRoot, "pets", "custom-" + Guid.NewGuid().ToString("N"));
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(current.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择独立的资源包文件夹，不能将整个程序数据目录作为资源包。");
        Directory.CreateDirectory(target);
        CopyDirectory(current.Directory, target);
        var definition = JsonSerializer.Deserialize<PackDefinition>(
            JsonSerializer.Serialize(current.Definition, PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
        var assetDirectory = Path.Combine(target, "imported", state);
        Directory.CreateDirectory(assetDirectory);
        var imported = new List<string>();
        var ordered = files.OrderBy(file => Regex.Replace(Path.GetFileName(file), @"\d+", match => match.Value.PadLeft(16, '0')),
            StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var relative = $"imported/{state}/{index:0000}{(isGif ? ".gif" : ".png")}";
            File.Copy(ordered[index], Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)), true);
            imported.Add(relative);
        }
        definition.Animations[state] = isGif
            ? new() { File = imported[0], Loop = IsLoop(state) }
            : new() { Frames = imported.Select(file => new FrameDefinition { File = file, DurationMs = frameDurationMs }).ToList(), Loop = IsLoop(state) };
        if (state == "drag") { definition.Animations.Remove("drag-left"); definition.Animations.Remove("drag-right"); }
        File.WriteAllText(Path.Combine(target, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
        return PetPackLoader.Load(target);
    }

    private static bool IsLoop(string state) => state is not ("wave" or "jump" or "happy" or "yawn" or "knock" or "foot-swing" or "head-pat" or "cheek-block");

    public static PetPack ReplaceClimbSet(PetPack current, string storageRoot, string bundledDirectory)
    {
        var updated = Replace(current, storageRoot, "climb-left", [Path.Combine(bundledDirectory, "frames", "crawl-forward-v7.gif")], 180);
        updated.Definition.Animations["climb-right"] = new() { MirrorOf = "climb-left" };
        File.WriteAllText(Path.Combine(updated.Directory, "pet.json"), JsonSerializer.Serialize(updated.Definition, PetPackLoader.JsonOptions));
        return PetPackLoader.Load(updated.Directory);
    }

    public static PetPack ReplaceRestSet(PetPack current, string storageRoot, string bundledDirectory)
    {
        var bundled = PetPackLoader.Load(bundledDirectory);
        var target = Path.Combine(storageRoot, "pets", "rest-update-" + Guid.NewGuid().ToString("N"));
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(current.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包不能包含整个程序数据目录。");
        Directory.CreateDirectory(target); CopyDirectory(current.Directory, target);
        var definition = JsonSerializer.Deserialize<PackDefinition>(JsonSerializer.Serialize(current.Definition, PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
        string CopyAsset(string file)
        {
            var relative = "imported/rest-v1/" + file.Replace('\\', '/');
            var destination = PetPackLoader.Resolve(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(PetPackLoader.Resolve(bundledDirectory, file), destination, true);
            return relative;
        }
        foreach (var state in new[] { "sleep", "sleep-ear-twitch", "sleep-tail-flick", "yawn" })
        {
            var animation = JsonSerializer.Deserialize<AnimationDefinition>(JsonSerializer.Serialize(bundled.Definition.Animations[state], PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
            if (animation.File is { } file) animation.File = CopyAsset(file);
            if (animation.Frames is { } frames) foreach (var frame in frames) frame.File = CopyAsset(frame.File);
            if (animation.Sheet is { } sheet) sheet.File = CopyAsset(sheet.File);
            definition.Animations[state] = animation;
        }
        File.WriteAllText(Path.Combine(target, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
        return PetPackLoader.Load(target);
    }

    public static PetPack ReplaceDragSet(PetPack current, string storageRoot, string bundledDirectory)
    {
        var bundled = PetPackLoader.Load(bundledDirectory);
        var target = Path.Combine(storageRoot, "pets", "drag-update-" + Guid.NewGuid().ToString("N"));
        if (Path.GetFullPath(target).StartsWith(Path.GetFullPath(current.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源包不能包含整个程序数据目录。");
        Directory.CreateDirectory(target); CopyDirectory(current.Directory, target);
        var definition = JsonSerializer.Deserialize<PackDefinition>(JsonSerializer.Serialize(current.Definition, PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
        var imported = Path.Combine(target, "imported"); Directory.CreateDirectory(imported);
        var relative = "imported/supplied-drag-v1.png";
        File.Copy(Path.Combine(bundledDirectory, "frames", "drag-sprites.png"), Path.Combine(target, relative), true);
        foreach (var state in new[] { "drag", "drag-left", "drag-right" })
        {
            var animation = JsonSerializer.Deserialize<AnimationDefinition>(JsonSerializer.Serialize(bundled.Definition.Animations[state], PetPackLoader.JsonOptions), PetPackLoader.JsonOptions)!;
            animation.Sheet!.File = relative; definition.Animations[state] = animation;
        }
        File.WriteAllText(Path.Combine(target, "pet.json"), JsonSerializer.Serialize(definition, PetPackLoader.JsonOptions));
        return PetPackLoader.Load(target);
    }

    private static void CopyDirectory(string source, string target)
    {
        var count = 0;
        void Copy(string from, string to)
        {
            foreach (var path in Directory.EnumerateFiles(from))
            {
                if (++count > 5000) throw new InvalidDataException("资源包文件过多。");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("资源包中不能包含文件链接。");
                File.Copy(path, Path.Combine(to, Path.GetFileName(path)), true);
            }
            foreach (var path in Directory.EnumerateDirectories(from))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("资源包中不能包含目录链接。");
                var next = Path.Combine(to, Path.GetFileName(path));
                Directory.CreateDirectory(next);
                Copy(path, next);
            }
        }
        Copy(source, target);
    }
}
