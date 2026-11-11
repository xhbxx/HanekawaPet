using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HanekawaPet.Core;

public enum MovementMode { Stay, Walk, Wander }

public sealed class PetSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Scale { get; set; } = 1;
    public double Speed { get; set; } = 28;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool Sleeping { get; set; }
    public MovementMode Movement { get; set; } = MovementMode.Stay;
    public string? PackDirectory { get; set; }
    public int ImportFrameDurationMs { get; set; } = 180;
    public int DragAnimationRevision { get; set; }
    public int RestAnimationRevision { get; set; }
    public int KnockAnimationRevision { get; set; }
    public int ThinkingAnimationRevision { get; set; }
    public int FootSwingAnimationRevision { get; set; }
    public int ClimbAnimationRevision { get; set; }
    public int HeadPatAnimationRevision { get; set; }
    public int CheekAnimationRevision { get; set; }

    public void Normalize()
    {
        if (Left is { } left && !double.IsFinite(left)) Left = null;
        if (Top is { } top && !double.IsFinite(top)) Top = null;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .5, 2.5) : 1;
        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, 5, 160) : 28;
        if (!Enum.IsDefined(Movement)) Movement = MovementMode.Stay;
        ImportFrameDurationMs = Math.Clamp(ImportFrameDurationMs, 20, 2000);
    }
}

public sealed class SettingsStore(string dataDirectory)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string FilePath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions Options = new(PetPackLoader.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public PetSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(FilePath), Options) ?? new() : new();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log("读取设置失败，恢复默认设置。", ex);
            return new();
        }
    }

    public void Save(PetSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, FilePath, true);
    }

    public void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "desktop-pet.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {exception}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
