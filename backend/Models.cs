using System.Text.Json;

namespace HanekawaPet.Backend;

public static class JsonIO
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Save<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
}

public record AiConfig(string BaseUrl = "", string Model = "", string? ProtectedApiKey = null,
    double? Temperature = null, int? MaxTokens = null, int TimeoutSeconds = 120);
public record ConfigUpdate(string BaseUrl, string Model, string? ApiKey = null, bool ClearApiKey = false,
    double? Temperature = null, int? MaxTokens = null, int TimeoutSeconds = 120);
public record ImportFile(string FileName, string Kind, string Content);
public record ChatInput(string ConversationId, string Message);
public record ChatMessage(string Role, string Content, List<Source>? Sources = null);
public record Conversation(string Id, string Title, DateTimeOffset UpdatedAt, List<ChatMessage> Messages);
public record Source(string Id, string FileName, string Location, string Excerpt);
public record KnowledgeFile(string Id, string FileName, string Kind, int Characters, int Chunks, string? Error);
public record Chunk(string FileName, string Location, string Content);
public record KnowledgeSnapshot(string Persona, List<Chunk> Chunks, List<KnowledgeFile> Files);

public sealed class UserError(string message) : Exception(message);
