using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace HanekawaPet.Core;

public sealed record GachaPrize(string Id, string Name, string Image, string Description = "", int Weight = 1);
public sealed class GachaCatalog
{
    public string? Animation { get; set; }
    public List<string> Animations { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> AnimationFiles => Animations.Count > 0 ? Animations : string.IsNullOrEmpty(Animation) ? Array.Empty<string>() : new[] { Animation };
    public string? SelectAnimation(Func<int, int>? pick = null) => AnimationFiles.Count == 0 ? null : AnimationFiles[(pick ?? RandomNumberGenerator.GetInt32)(AnimationFiles.Count)];
    public List<GachaPrize> Prizes { get; set; } = [];
    public static GachaCatalog Load(string directory)
    {
        var path = Path.Combine(directory, "catalog.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("奖池配置过大。");
        var value = JsonSerializer.Deserialize<GachaCatalog>(File.ReadAllText(path), GachaStore.Json) ?? throw new InvalidDataException("奖池配置为空。");
        if (value.Prizes == null || value.Prizes.Count > 500) throw new InvalidDataException("奖池配置无效。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long weight = 0;
        foreach (var prize in value.Prizes)
        {
            if (prize == null || string.IsNullOrEmpty(prize.Id) || !Regex.IsMatch(prize.Id, "^[a-zA-Z0-9_-]{1,64}$") || !ids.Add(prize.Id) ||
                string.IsNullOrWhiteSpace(prize.Name) || prize.Name.Length > 80 || (prize.Description?.Length ?? 0) > 500 || prize.Weight < 1)
                throw new InvalidDataException("奖品名称、编号或权重无效。");
            weight += prize.Weight;
            if (weight > 1000000) throw new InvalidDataException("奖池权重过大。");
            ValidateImage(Resolve(directory, prize.Image));
        }
        if (value.Animations == null || value.Animations.Count > 16) throw new InvalidDataException("扭蛋动画列表无效。");
        foreach (var animation in value.AnimationFiles)
        {
            var file = Resolve(directory, animation);
            if (!Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase) || !File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024)
                throw new InvalidDataException("扭蛋动画需要一个有效的 GIF 文件。");
        }
        return value;
    }

    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("素材路径必须位于扭蛋文件夹内。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材路径不能超出扭蛋文件夹。");
        return full;
    }

    public static void ValidateImage(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024 ||
            !new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidDataException("奖品插图缺失或格式无效，请准备 PNG、JPG、GIF 或 BMP 图片。");
        using var file = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames[0].PixelWidth > 4096 || decoder.Frames[0].PixelHeight > 4096) throw new InvalidDataException("奖品插图尺寸不能超过 4096 像素。");
    }
}

public sealed record GachaDraw(string Id, string Day, DateTimeOffset DrawnAt, string PrizeId, string Name, string Image, string Description);
public sealed class GachaState
{
    public int Version { get; set; } = 1;
    public string? LastDrawDay { get; set; }
    public string? PendingDrawId { get; set; }
    public List<GachaDraw> Draws { get; set; } = [];
}

public sealed class GachaStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, int> _pick;
    private readonly string _mutexName;
    public string Root { get; }
    public string StatePath => Path.Combine(Root, "state.json");
    public string Today => _now().ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public DateTimeOffset NextRefresh => new(DateTime.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1), TimeSpan.FromHours(8));
    public GachaStore(string root, Func<DateTimeOffset>? now = null, Func<int, int>? pick = null)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pick = pick ?? RandomNumberGenerator.GetInt32;
        _mutexName = "Local\\HanekawaPet.Gacha." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName); var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("正在保存扭蛋记录，请稍后再试。");
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }
    public GachaState Read() => Locked(ReadCore);
    private GachaState ReadCore()
    {
        if (!File.Exists(StatePath)) return new();
        try
        {
            if (new FileInfo(StatePath).Length > 16 * 1024 * 1024) throw new InvalidDataException();
            var state = JsonSerializer.Deserialize<GachaState>(File.ReadAllText(StatePath), Json) ?? throw new InvalidDataException();
            if (state.Version != 1 || state.Draws == null || state.Draws.Any(d => d == null || !ValidDay(d.Day)) ||
                state.LastDrawDay != null && !ValidDay(state.LastDrawDay)) throw new InvalidDataException();
            var latest = state.Draws.Select(d => d.Day).Append(state.LastDrawDay ?? "").Max(StringComparer.Ordinal);
            state.LastDrawDay = string.IsNullOrEmpty(latest) ? null : latest;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException("扭蛋记录暂时无法读取，已暂停抽取并保留原文件。", ex); }
    }
    private static bool ValidDay(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    public bool HasDrawnToday(GachaState state) => state.LastDrawDay != null && string.CompareOrdinal(Today, state.LastDrawDay) <= 0;
    public GachaDraw Draw(string catalogRoot) => Locked(() =>
    {
        var state = ReadCore();
        if (HasDrawnToday(state)) throw new InvalidOperationException("今天已经抽过啦，明天 00:00 再来。" );
        var catalog = GachaCatalog.Load(catalogRoot);
        if (catalog.Prizes.Count == 0) throw new InvalidOperationException("奖品素材还没有准备好，今天的次数会为你保留。");
        var sample = _pick(catalog.Prizes.Sum(p => p.Weight));
        if (sample < 0 || sample >= catalog.Prizes.Sum(p => p.Weight)) throw new InvalidDataException("抽取结果无效，本次没有扣除次数。");
        var prize = catalog.Prizes[0];
        foreach (var candidate in catalog.Prizes) { prize = candidate; if (sample < candidate.Weight) break; sample -= candidate.Weight; }
        var source = GachaCatalog.Resolve(catalogRoot, prize.Image);
        var bytes = File.ReadAllBytes(source);
        var image = "items/" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + Path.GetExtension(source).ToLowerInvariant();
        var savedImage = GachaCatalog.Resolve(Root, image); Directory.CreateDirectory(Path.GetDirectoryName(savedImage)!);
        if (!File.Exists(savedImage)) File.WriteAllBytes(savedImage, bytes);
        var draw = new GachaDraw(Guid.NewGuid().ToString("N"), Today, _now(), prize.Id, prize.Name, image, prize.Description ?? "");
        state.Draws.Add(draw); state.LastDrawDay = draw.Day; state.PendingDrawId = draw.Id;
        Save(state);
        return draw;
    });
    public void Acknowledge(string id) => Locked(() =>
    {
        var state = ReadCore();
        if (state.PendingDrawId == id) { state.PendingDrawId = null; Save(state); }
        return true;
    });
    private void Save(GachaState state)
    {
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, StatePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
