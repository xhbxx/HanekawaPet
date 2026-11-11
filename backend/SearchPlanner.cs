using System.Text;
using System.Text.Json;

namespace HanekawaPet.Backend;

public sealed class SearchPlanner(AiClient ai)
{
    public async Task<string?> Query(string question, Conversation conversation, CancellationToken ct)
    {
        if (!WebSearch.ContextRequest(question)) return WebSearch.Query(question);
        var fallback = WebSearch.PreviousTopic(conversation);
        var prompt = new List<ChatMessage>
        {
            new("system", "你是联网搜索的检索词规划器。只输出 JSON：{\"query\":\"精简检索词\"}。从用户最近的话中识别当前要查的实体和事实主题。‘再搜’、‘说话之前联网’、‘为什么搜不到这个’、‘把搜到的告诉我’都指前面的话题，绝不能原句搜索。优先找最近的明确作品名、人名、产品名等，跨过没有主题的抱怨和指令。只读用户消息，不根据以前助手的否认来判断东西是否存在。不能回答问题、不能自行补写事实；主题完全不清楚时输出 {\"query\":\"\"}。")
        };
        prompt.AddRange(conversation.Messages.Where(m => m.Role == "user").TakeLast(10).Select(m => new ChatMessage("user", m.Content)));
        prompt.Add(new("user", question));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var text = new StringBuilder();
            await foreach (var delta in ai.Stream(prompt, deadline.Token, 256))
            {
                text.Append(delta);
                if (text.Length > 4096) return fallback;
            }
            var value = text.ToString(); var first = value.IndexOf('{'); var last = value.LastIndexOf('}');
            if (first < 0 || last < first) return fallback;
            using var document = JsonDocument.Parse(value[first..(last + 1)]);
            if (!document.RootElement.TryGetProperty("query", out var field) || field.ValueKind != JsonValueKind.String) return fallback;
            var query = field.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(query) || WebSearch.ContextRequest(query)) return fallback;
            if (query is "你" or "我" or "就是" or "这个" or "那个" or "为什么" or "联网搜索" or "搜索") return fallback;
            if (fallback != null && Compact(query).Contains(Compact(fallback), StringComparison.OrdinalIgnoreCase)) query = fallback;
            return query[..Math.Min(query.Length, 500)];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return fallback; }
        catch (Exception ex) when (ex is UserError or HttpRequestException or JsonException or IOException) { return fallback; }
    }

    private static string Compact(string value)
    {
        value = System.Text.RegularExpressions.Regex.Replace(value, @"第([一二三四五六七八九十0-9]+)[季部代辑集卷]", match =>
        {
            var number = match.Groups[1].Value;
            if (int.TryParse(number, out _)) return number;
            const string digits = "零一二三四五六七八九";
            if (number == "十") return "10";
            if (number.Length == 1 && digits.IndexOf(number[0]) > 0) return digits.IndexOf(number[0]).ToString();
            if (number.Contains('十'))
            {
                var parts = number.Split('十');
                var tens = parts[0].Length == 0 ? 1 : digits.IndexOf(parts[0][0]);
                var units = parts[1].Length == 0 ? 0 : digits.IndexOf(parts[1][0]);
                if (tens > 0 && units >= 0) return (tens * 10 + units).ToString();
            }
            return number;
        });
        return System.Text.RegularExpressions.Regex.Replace(value, @"[\s《》“”"":：·-]", "");
    }
}
