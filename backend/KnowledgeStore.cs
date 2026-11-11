using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HanekawaPet.Backend;

public sealed class KnowledgeStore
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    public string Root { get; }
    public KnowledgeStore(string dataRoot)
    {
        Root = Path.Combine(dataRoot, "knowledge");
        foreach (var kind in new[] { "persona", "documents" }) Directory.CreateDirectory(Path.Combine(Root, kind));
        var sample = Path.Combine(Root, "persona", "角色设定.md");
        var initialized = Path.Combine(dataRoot, ".knowledge-initialized");
        if (!File.Exists(initialized))
        {
            if (!File.Exists(sample)) File.WriteAllText(sample, "# 桌宠角色设定\n\n名字：小翼。\n你是陪伴用户的猫耳桌宠，聪明、温柔，偶尔俏皮。使用自然的中文，默认简短回答。\n记住自己是桌宠角色；不要编造用户经历或资料中没有的事实。\n用户可以修改或删除本文件，换成自己的角色设定。\n");
            File.WriteAllText(initialized, "1");
        }
    }

    private static bool Supported(string name) => Path.GetExtension(name).ToLowerInvariant() is ".md" or ".json" or ".jsonl";
    private static string Kind(string kind) => kind is "persona" or "documents" ? kind : throw new UserError("文件分类应为 persona 或 documents。");
    private string FilePath(string id)
    {
        var parts = id.Split('/');
        if (parts.Length != 2 || parts[1] != Path.GetFileName(parts[1]) || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Supported(parts[1]))
            throw new UserError("无效的知识库文件标识。");
        return Path.Combine(Root, Kind(parts[0]), parts[1]);
    }

    public KnowledgeSnapshot Import(ImportFile file)
    {
        lock (_gate)
        {
            Kind(file.Kind);
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 160 || file.FileName != Path.GetFileName(file.FileName) || file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Supported(file.FileName))
                throw new UserError("只支持文件名不含路径的 .md、.json、.jsonl 文件。");
            if (Encoding.UTF8.GetByteCount(file.Content) > MaxFileBytes) throw new UserError("每个文件最多 2 MB。");
            Parse(file.FileName, file.Content); // Validate before replacing an existing file.
            var path = FilePath(file.Kind + "/" + file.FileName);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UserError("不支持链接文件。");
            var before = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                File.WriteAllText(path, file.Content, new UTF8Encoding(false));
                var snapshot = Read();
                var error = snapshot.Files.FirstOrDefault(f => f.Id == file.Kind + "/" + file.FileName)?.Error;
                if (error != null) throw new UserError(error);
                return snapshot;
            }
            catch
            {
                if (before is null) File.Delete(path); else File.WriteAllText(path, before);
                throw;
            }
        }
    }
    public void Delete(string id) { lock (_gate) File.Delete(FilePath(id)); }

    public KnowledgeSnapshot Read()
    {
        lock (_gate)
        {
            var files = new List<KnowledgeFile>(); var chunks = new List<Chunk>(); var persona = new StringBuilder();
            long total = 0;
            foreach (var kind in new[] { "persona", "documents" })
            {
                var directory = Path.Combine(Root, kind);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new UserError("知识库目录不能是链接。");
                foreach (var path in Directory.GetFiles(directory).Where(Supported).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (files.Count >= 300) throw new UserError("知识库最多支持 300 个文件。");
                    var name = Path.GetFileName(path); var id = kind + "/" + name;
                    try
                    {
                        var info = new FileInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserError("不支持链接文件。");
                        if (info.Length > MaxFileBytes || (total += info.Length) > 12 * 1024 * 1024) throw new UserError("每个文件最多 2 MB，全部文件最多 12 MB。");
                        var text = File.ReadAllText(path, new UTF8Encoding(false, true)).TrimStart('\uFEFF');
                        var entries = Parse(name, text);
                        var parsed = entries.SelectMany(entry => Split(name, entry.Location, entry.Content)).ToList();
                        if (kind == "persona")
                        {
                            var rendered = "\n\n--- 角色设定文件：" + name + " ---\n" + string.Join("\n\n", entries.Select(e => e.Content));
                            if (persona.Length + rendered.Length > 24000) throw new UserError("角色设定总量超过 24000 字符，请精简或将背景资料改为问答资料。");
                            persona.Append(rendered);
                        }
                        else chunks.AddRange(parsed);
                        files.Add(new(id, name, kind, text.Length, kind == "persona" ? 0 : parsed.Count, null));
                    }
                    catch (Exception ex) when (ex is UserError or JsonException or IOException or UnauthorizedAccessException or DecoderFallbackException)
                    { files.Add(new(id, name, kind, 0, 0, ex is JsonException ? "JSON 格式错误，请检查括号、逗号与引号。" : ex.Message)); }
                }
            }
            return new(persona.ToString(), chunks, files);
        }
    }

    private static List<(string Location, string Content)> Parse(string name, string text)
    {
        try
        {
            if (Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase)) return [("正文", text)];
            var output = new List<(string, string)>();
            if (Path.GetExtension(name).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try { using var doc = JsonDocument.Parse(lines[i]); Extract(doc.RootElement, "第 " + (i + 1) + " 行", output); }
                    catch (JsonException) { throw new UserError($"{name} 第 {i + 1} 行不是有效的 JSON。"); }
                }
            }
            else { using var doc = JsonDocument.Parse(text); Extract(doc.RootElement, "JSON", output); }
            return output;
        }
        catch (JsonException) { throw new UserError(name + " 不是有效的 JSON，请检查括号、逗号与引号。"); }
    }

    private static void Extract(JsonElement value, string location, List<(string, string)> output)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0; foreach (var item in value.EnumerateArray()) Extract(item, location + "[" + i++ + "]", output);
        }
        else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("documents", out var documents) && documents.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in value.EnumerateObject().Where(p => p.Name != "documents")) output.Add((location + "." + field.Name, Flatten(field.Value, field.Name)));
            Extract(documents, location + ".documents", output);
        }
        else output.Add((location, Flatten(value, "")));
    }
    private static string Flatten(JsonElement value, string path) => value.ValueKind switch
    {
        JsonValueKind.Object => string.Join("\n", value.EnumerateObject().Select(p => Flatten(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name))),
        JsonValueKind.Array => string.Join("\n", value.EnumerateArray().Select((v, i) => Flatten(v, path + "[" + i + "]"))),
        JsonValueKind.String => (path.Length == 0 ? "" : path + "：") + value.GetString(),
        _ => (path.Length == 0 ? "" : path + "：") + value.GetRawText()
    };
    private static IEnumerable<Chunk> Split(string name, string location, string text)
    {
        // Overlap keeps answers spanning two adjacent slices retrievable.
        for (var offset = 0; offset < text.Length; offset += 900)
            yield return new(name, location + " · 字符 " + (offset + 1), text.Substring(offset, Math.Min(1100, text.Length - offset)));
    }
    private static string[] Tokens(string text)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9_]+|[\\p{IsCJKUnifiedIdeographs}]+").Select(m => m.Value);
        return words.SelectMany(w => w[0] >= 0x4e00 ? (w.Length == 1 ? new[] { w } : Enumerable.Range(0, w.Length - 1).Select(i => w.Substring(i, 2))) : new[] { w }).Distinct().ToArray();
    }
    public static List<Source> Search(KnowledgeSnapshot snapshot, string query)
    {
        var terms = Tokens(query).Take(200).ToArray(); if (terms.Length == 0) return [];
        var docs = snapshot.Chunks.Select(c => (Chunk: c, Tokens: Tokens(c.Content + " " + c.FileName).ToHashSet())).ToList();
        var weights = terms.ToDictionary(t => t, t => Math.Log(1 + (docs.Count + 1d) / (1 + docs.Count(d => d.Tokens.Contains(t)))));
        return docs.Select(d => (d.Chunk, Score: terms.Where(d.Tokens.Contains).Sum(t => weights[t])))
            .Where(d => d.Score > 0).OrderByDescending(d => d.Score).Take(5)
            .Select((d, i) => new Source("K" + (i + 1), d.Chunk.FileName, d.Chunk.Location, d.Chunk.Content)).ToList();
    }
}
