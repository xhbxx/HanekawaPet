using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HanekawaPet.Core;

public sealed class BackendHost : IDisposable
{
    private readonly SettingsStore _store;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private HttpClient? _client;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string DataRoot => Path.Combine(_store.DataDirectory, "backend");
    internal Uri? ServiceAddress => _client?.BaseAddress;
    public BackendHost(SettingsStore store) => _store = store;

    private async Task<HttpClient> Client(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false } && _client != null) return _client;
            _client?.Dispose(); _client = null; _process?.Dispose();
            var dll = Path.Combine(AppContext.BaseDirectory, "backend", "HanekawaPet.Backend.dll");
            if (!File.Exists(dll)) throw new InvalidOperationException("缺少 backend 文件夹，请使用完整桌宠压缩包，或先运行 build.ps1。");
            Directory.CreateDirectory(DataRoot);
            var ready = Path.Combine(DataRoot, "ready-" + Guid.NewGuid().ToString("N") + ".json");
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "dotnet.exe");
            var info = new ProcessStartInfo(File.Exists(bundled) ? bundled : "dotnet")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(dll)! };
            foreach (var value in new[] { dll, "--data-dir", DataRoot, "--port", "0", "--ready-file", ready, "--parent-pid", Environment.ProcessId.ToString() }) info.ArgumentList.Add(value);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            info.Environment["HANEKAWA_LOCAL_TOKEN"] = token;
            _process = Process.Start(info) ?? throw new InvalidOperationException("本地 AI 后端未能启动。");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                while (!File.Exists(ready))
                {
                    if (_process.HasExited) throw new InvalidOperationException("本地 AI 后端启动失败。请检查数据文件或重新解压完整程序。");
                    await Task.Delay(100, deadline.Token);
                }
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token));
                var url = document.RootElement.GetProperty("url").GetString()!;
                _client = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
                _client.DefaultRequestHeaders.Add("X-Pet-Token", token);
                try
                {
                    File.WriteAllText(Path.Combine(DataRoot, "runtime-info.json"), JsonSerializer.Serialize(new
                    {
                        started = DateTimeOffset.Now, appVersion = typeof(BackendHost).Assembly.GetName().Version?.ToString(),
                        appPath = Environment.ProcessPath, backendPath = dll, backendProcessId = _process.Id
                    }, JsonOptions));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return _client;
            }
            catch
            {
                if (!_process.HasExited) _process.Kill(true);
                throw;
            }
            finally { try { File.Delete(ready); } catch (IOException) { } }
        }
        finally { _startGate.Release(); }
    }
    public async Task<JsonElement> Call(string method, string path, object? body, CancellationToken ct)
    {
        var client = await Client(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(11)); // A connection test may use the configured provider timeout.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, deadline.Token);
        var content = await response.Content.ReadAsStringAsync(deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Error(content));
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement.Clone();
    }
    public async Task Stream(string conversationId, string message, Action<JsonElement> receive, CancellationToken ct)
    {
        var client = await Client(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        { Content = new StringContent(JsonSerializer.Serialize(new { conversationId, message }, JsonOptions), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Error(await response.Content.ReadAsStringAsync(ct)));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var completed = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line[6..]);
            var item = doc.RootElement;
            var type = item.GetProperty("type").GetString();
            if (type == "error") throw new InvalidOperationException(item.GetProperty("message").GetString());
            if (type == "done") completed = true;
            receive(item);
        }
        if (!completed) throw new InvalidOperationException("连接已中断，本次回复可能未保存，请重新打开对话确认。");
    }
    private static string Error(string content)
    {
        try { using var json = JsonDocument.Parse(content); return json.RootElement.GetProperty("error").GetString() ?? "本地服务请求失败。"; }
        catch { return "本地服务请求失败，请重试。"; }
    }
    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch (InvalidOperationException) { }
        _process?.Dispose();
    }
}
