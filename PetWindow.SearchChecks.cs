using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HanekawaPet;

public partial class PetWindow
{
    // Runs the actual window send methods and its owned backend, with caller-provided isolated data.
    internal async Task RunSearchSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>(); var failed = false;
        void Capture(string name, Window window)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); png.Save(output);
        }
        try
        {
            ShowChat(); var chat = _chatWindow!; await chat.Ready;
            foreach (var question in new[] { "赛博朋克边缘行者2", "再查呢" })
            {
                chat.InputBox.Text = question; await chat.Send();
                var saved = await _backend!.Call("GET", "/api/conversations/" + chat.ConversationId, null, CancellationToken.None);
                var turns = saved.GetProperty("messages").EnumerateArray().ToArray();
                var reply = turns.Last();
                var sources = reply.GetProperty("sources").EnumerateArray().Where(s => s.GetProperty("id").GetString()!.StartsWith("W")).Select(s => s.Clone()).ToArray();
                var answer = reply.GetProperty("content").GetString();
                var displayed = chat.MessagesPanel.Children.OfType<Border>().Select(b => ((StackPanel)b.Child).Children.OfType<TextBox>().First().Text).Last();
                var passed = turns[^2].GetProperty("content").GetString() == question && sources.Length > 0 && displayed == answer && !string.IsNullOrWhiteSpace(answer);
                if (!passed) failed = true;
                results.Add(new { surface = "chat", question, answer, sources, displayed, passed });
            }
            Capture("search-chat", chat);
            ShowBubble(); var bubble = _speechBubble!; await bubble.EnsureReady();
            bubble.InputBox.Text = "边缘行者2"; await bubble.Send();
            var conversation = await _backend!.Call("GET", "/api/conversations/" + chat.ConversationId, null, CancellationToken.None);
            var last = conversation.GetProperty("messages").EnumerateArray().Last();
            var web = last.GetProperty("sources").EnumerateArray().Where(s => s.GetProperty("id").GetString()!.StartsWith("W")).Select(s => s.Clone()).ToArray();
            var bubblePassed = bubble.HeadText.IsVisible && web.Length > 0 && bubble.HeadText.Reply == last.GetProperty("content").GetString();
            if (!bubblePassed) failed = true;
            results.Add(new { surface = "bubble", question = "边缘行者2", answer = bubble.HeadText.Reply, sources = web, passed = bubblePassed });
            Capture("search-head-reply", bubble.HeadText);
        }
        catch (Exception ex) { failed = true; results.Add(new { passed = false, error = ex.ToString() }); }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Application.Current.Shutdown(failed ? 1 : 0);
    }
}
