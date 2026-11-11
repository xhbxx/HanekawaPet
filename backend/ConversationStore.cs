using System.Text.Json;

namespace HanekawaPet.Backend;

public sealed class ConversationStore
{
    private readonly string _root;
    // All chat operations serialize locally, so simultaneous windows cannot overwrite history.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ConversationStore(string root) { _root = Path.Combine(root, "conversations"); Directory.CreateDirectory(_root); }
    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(_root, id + ".json") : throw new UserError("无效的对话标识。");
    public Conversation Create()
    {
        var value = new Conversation(Guid.NewGuid().ToString("N"), "新对话", DateTimeOffset.Now, []);
        JsonIO.Save(PathFor(value.Id), value); return value;
    }
    public Conversation Get(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new UserError("对话不存在，请新建对话。");
        return JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), JsonIO.Options) ?? throw new UserError("对话文件损坏。");
    }
    public object List() => Directory.GetFiles(_root, "*.json").Select(path =>
    {
        try { var c = Get(Path.GetFileNameWithoutExtension(path)); return new { c.Id, c.Title, c.UpdatedAt }; }
        catch { return null; }
    }).Where(c => c != null).OrderByDescending(c => c!.UpdatedAt).Take(100).ToArray();
    public void Save(Conversation conversation, string user, string assistant, List<Source> sources)
    {
        var messages = conversation.Messages.Concat(new[] { new ChatMessage("user", user), new ChatMessage("assistant", assistant, sources) }).TakeLast(200).ToList();
        var title = conversation.Messages.Count == 0 ? user[..Math.Min(user.Length, 24)] : conversation.Title;
        JsonIO.Save(PathFor(conversation.Id), conversation with { Title = title, UpdatedAt = DateTimeOffset.Now, Messages = messages });
    }
    public void Delete(string id) => File.Delete(PathFor(id));
}
