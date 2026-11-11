using System.Runtime.InteropServices;
using System.Text;

namespace HanekawaPet.Backend;

public sealed class ConfigStore
{
    private readonly string _path;
    private AiConfig _value;
    private readonly object _gate = new();
    public ConfigStore(string root)
    {
        _path = Path.Combine(root, "ai-config.json");
        _value = File.Exists(_path)
            ? System.Text.Json.JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(_path), JsonIO.Options) ?? new()
            : new();
    }
    public AiConfig Current { get { lock (_gate) return _value; } }
    public string Key(AiConfig config) => config.ProtectedApiKey is { Length: > 0 }
        ? WindowsSecret.Unprotect(config.ProtectedApiKey) : Environment.GetEnvironmentVariable("HANEKAWA_AI_API_KEY") ?? "";
    public object Public()
    {
        var c = Current;
        return new { c.BaseUrl, c.Model, hasApiKey = !string.IsNullOrEmpty(c.ProtectedApiKey) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HANEKAWA_AI_API_KEY")), c.Temperature, c.MaxTokens, c.TimeoutSeconds };
    }
    public static Uri Endpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UserError("接口地址必须是 HTTP/HTTPS 地址，不能含密钥、查询参数或片段。");
        var value = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? value : value + "/chat/completions");
    }
    public void Update(ConfigUpdate update)
    {
        Endpoint(update.BaseUrl);
        if (string.IsNullOrWhiteSpace(update.Model) || update.Model.Length > 200) throw new UserError("请填写模型名称。");
        if (update.Temperature is { } temperature && (!double.IsFinite(temperature) || temperature < 0 || temperature > 2))
            throw new UserError("Temperature 应为 0～2，或留空使用模型默认值。");
        if (update.MaxTokens is < 1 or > 131072 || update.TimeoutSeconds is < 10 or > 600)
            throw new UserError("输出上限应为 1～131072，超时应为 10～600 秒。");
        if (update.ApiKey?.Length > 8192) throw new UserError("密钥过长。");
        lock (_gate)
        {
            var secret = update.ClearApiKey ? null : string.IsNullOrWhiteSpace(update.ApiKey) ? _value.ProtectedApiKey : WindowsSecret.Protect(update.ApiKey.Trim());
            var value = new AiConfig(update.BaseUrl.Trim().TrimEnd('/'), update.Model.Trim(), secret, update.Temperature, update.MaxTokens, update.TimeoutSeconds);
            JsonIO.Save(_path, value);
            _value = value;
        }
    }
}

// Windows DPAPI binds the saved credential to the current Windows account.
internal static class WindowsSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Convert(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new UserError("保存密钥需要 Windows；其他系统请使用 HANEKAWA_AI_API_KEY 环境变量。");
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new UserError("密钥无法加密或解密，请在 AI 设置中重新填写密钥。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public static string Protect(string value) => System.Convert.ToBase64String(Convert(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Convert(System.Convert.FromBase64String(value), false));
}
