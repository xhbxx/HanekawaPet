using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HanekawaPet.Backend;

string? Argument(string name)
{
    var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var root = Path.GetFullPath(Argument("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HanekawaPet", "backend"));
Directory.CreateDirectory(root);
var port = int.TryParse(Argument("--port"), out var suppliedPort) ? suppliedPort : 17281;
if (port is < 0 or > 65535) throw new ArgumentException("Port must be between 0 and 65535.");
var token = Environment.GetEnvironmentVariable("HANEKAWA_LOCAL_TOKEN");
if (string.IsNullOrEmpty(token))
{
    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    File.WriteAllText(Path.Combine(root, "local-token.txt"), token);
}
if (token.Length < 32) throw new ArgumentException("HANEKAWA_LOCAL_TOKEN must have at least 32 characters.");
var configs = new ConfigStore(root);
var knowledge = new KnowledgeStore(root);
var conversations = new ConversationStore(root);
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
var ai = new AiClient(configs, http);
using var searchHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = Timeout.InfiniteTimeSpan };
var webSearch = new WebSearch(searchHttp, root);
var searchPlanner = new SearchPlanner(ai);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // Request/configuration bodies and credentials never enter logs.
builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, port); k.Limits.MaxRequestBodySize = 8 * 1024 * 1024; });
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path != "/health")
    {
        var supplied = context.Request.Headers["X-Pet-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(token)))
        { context.Response.StatusCode = 401; return; }
    }
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = ex is UserError or BadHttpRequestException or JsonException ? 400 : 500;
        await context.Response.WriteAsJsonAsync(new { error = ex is UserError ? ex.Message : "本地服务处理失败，请检查文件格式、磁盘权限或重试。" });
    }
});
app.MapGet("/health", () => new { status = "ok", service = "HanekawaPet.Backend", version = typeof(WebSearch).Assembly.GetName().Version?.ToString() });
app.MapGet("/api/config", () => configs.Public());
app.MapPut("/api/config", (ConfigUpdate update) => { configs.Update(update); return configs.Public(); });
app.MapPost("/api/config/test", async (HttpContext context) =>
{
    try
    {
        var reply = new StringBuilder();
        await foreach (var delta in ai.Stream([new("user", "请只回复 OK。")], context.RequestAborted)) reply.Append(delta);
        if (reply.Length == 0) throw new UserError("接口已连接，但没有返回文本。请确认模型支持文本对话。");
        return Results.Ok(new { message = "连接成功，模型已返回文本。" });
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { throw new UserError("AI 连接超时，请检查网络或增加超时时间。"); }
    catch (HttpRequestException) { throw new UserError("无法连接 AI 服务，请检查接口地址、网络和证书。"); }
});
app.MapGet("/api/knowledge", () => new { files = knowledge.Read().Files, root = knowledge.Root });
app.MapPost("/api/knowledge/import", (ImportFile file) => new { files = knowledge.Import(file).Files, root = knowledge.Root });
app.MapDelete("/api/knowledge", (string id) => { knowledge.Delete(id); return Results.Ok(new { files = knowledge.Read().Files }); });
app.MapGet("/api/conversations", () => conversations.List());
app.MapPost("/api/conversations", () => conversations.Create());
app.MapGet("/api/conversations/{id}", async (string id, CancellationToken ct) =>
{
    await conversations.Gate.WaitAsync(ct);
    try { return Results.Ok(conversations.Get(id)); } finally { conversations.Gate.Release(); }
});
app.MapDelete("/api/conversations/{id}", async (string id, CancellationToken ct) =>
{
    await conversations.Gate.WaitAsync(ct);
    try { conversations.Delete(id); return Results.NoContent(); } finally { conversations.Gate.Release(); }
});
app.MapPost("/api/chat/stream", async (ChatInput input, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 8000) throw new UserError("消息不能为空，且最多 8000 字符。");
    var ct = context.RequestAborted;
    await conversations.Gate.WaitAsync(ct);
    try
    {
        var conversation = conversations.Get(input.ConversationId);
        var snapshot = knowledge.Read();
        if (snapshot.Files.Any(f => f.Error != null)) throw new UserError("知识库有文件读取失败，请在知识库页面修正或移除后重试。");
        var sources = KnowledgeStore.Search(snapshot, input.Message);
        var prompt = AiClient.Prompt(snapshot, sources, conversation, input.Message.Trim());
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        async Task Send(object data)
        {
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
        try
        {
            string? searchFailure = null;
            if (await searchPlanner.Query(input.Message, conversation, ct) is { } query)
            {
                await Send(new { type = "status", text = "正在联网搜索…", query });
                var web = await webSearch.Search(query, ct);
                WebSearch.AddToPrompt(prompt, web);
                sources.AddRange(web.Pages.Select((p, i) => new Source("W" + (i + 1), p.Title, p.Url, p.Summary)));
                if (web.Pages.Count == 0) searchFailure = "这次没能查到与「" + query + "」相关的可靠联网结果，暂时无法核实。这不代表它不存在。" + web.Failure;
            }
            await Send(new { type = "sources", sources });
            var answer = new StringBuilder();
            if (searchFailure != null)
            {
                answer.Append(searchFailure);
                await Send(new { type = "delta", text = searchFailure });
            }
            else await foreach (var delta in ai.Stream(prompt, ct))
            {
                if (answer.Length + delta.Length > 100000) throw new UserError("回复过长，已停止生成。请设置输出上限或缩短问题。");
                answer.Append(delta); await Send(new { type = "delta", text = delta });
            }
            if (string.IsNullOrWhiteSpace(answer.ToString())) throw new UserError("模型没有返回文本，请检查模型类型或调整参数。");
            ct.ThrowIfCancellationRequested();
            conversations.Save(conversation, input.Message.Trim(), answer.ToString(), sources);
            await Send(new { type = "done", conversationId = conversation.Id });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await Send(new { type = "error", message = "AI 响应超时，本次未完成回复未保存。可增加超时后重试。" }); }
        catch (HttpRequestException) { await Send(new { type = "error", message = "连接 AI 失败，请检查地址、网络和证书后重试。" }); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await Send(new { type = "error", message = ex is UserError ? ex.Message : "生成失败，本次未完成回复未保存，请重试。" }); }
    }
    finally { conversations.Gate.Release(); }
});
await app.StartAsync();
if (int.TryParse(Argument("--parent-pid"), out var parentId))
{
    // Exit if the owning desktop process crashes or is forcibly terminated.
    _ = Task.Run(async () =>
    {
        try { using var parent = Process.GetProcessById(parentId); await parent.WaitForExitAsync(app.Lifetime.ApplicationStopping); app.Lifetime.StopApplication(); }
        catch (ArgumentException) { app.Lifetime.StopApplication(); }
        catch (OperationCanceledException) { }
    });
}
if (Argument("--ready-file") is { } readyFile)
    JsonIO.Save(readyFile, new { url = app.Urls.Single(), processId = Environment.ProcessId });
await app.WaitForShutdownAsync();
