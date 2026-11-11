using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class PetWindow
{
    internal async Task RunChatSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var failed = false;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failed = true; }
        void Capture(string name, Window window)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, name + ".png")); png.Save(output);
        }
        async Task ObserveThinking(Task send)
        {
            for (var i = 0; i < 200 && !send.IsCompleted && _state != "thinking"; i++) await Task.Delay(10);
        }
        try
        {
            SetMovement(MovementMode.Stay);
            var area = Platform.DesktopPlatform.WorkArea(this);
            Left = area.Left + (area.Width - Width) / 2; Top = area.Bottom - Height - 20;
            _clickTimer.Start(); await Task.Delay(350);
            var bubble = _speechBubble!; await bubble.EnsureReady();
            Check("single click opens only a blank head input", bubble.IsVisible && bubble.InputBox.Text.Length == 0 && _chatWindow == null);
            Capture("blank-input", bubble);
            bubble.InputBox.Text = "星光咖啡店什么时候营业？"; var bubbleTurn = bubble.Send(); await ObserveThinking(bubbleTurn);
            Check("bubble send switches to original looping thinking GIF", _state == "thinking" && _clip!.Loop && _clip.Frames.Count == 6 && _clip.DurationMs == 4000);
            Capture("thinking-pet", this);
            var firstThinkingFrame = _renderedFrame; RenderFrame(_stateStarted + 2750);
            Check("thinking animation advances and loops using source timings", !ReferenceEquals(firstThinkingFrame, _renderedFrame) && ReferenceEquals(_clip!.At(50), _clip.At(4050)));
            BeginPetDrag(); MovePetDrag(Left + 1, Top); Advance(Now, TimeSpan.FromHours(10));
            Check("dragging takes priority during an active reply", _state == "drag-right" && bubble.IsGenerating);
            EndPetDrag();
            Check("drag release resumes thinking until the reply completes", _state == "thinking");
            await bubbleTurn; Advance(Now, TimeSpan.FromHours(10));
            Check("bubble reply completion restores idle", !IsThinking && _state == "idle");
            Check("bubble send hides input and displays reply above pet", !bubble.IsVisible && bubble.HeadText.IsVisible && bubble.HeadText.Reply.Contains("晚上九点") && bubble.HeadText.Top + bubble.HeadText.ActualHeight <= Top + 1);
            Check("head reply hides context and allows mouse clicks through", !bubble.HeadText.Reply.Contains("[K1]") && Platform.DesktopPlatform.IsClickThrough(bubble.HeadText));
            Capture("head-reply", bubble.HeadText);
            var replyLeft = bubble.HeadText.Left; Left -= 20;
            Check("head reply follows pet position", Math.Abs(bubble.HeadText.Left - replyLeft + 20) < 1);
            ShowChat();
            var chat = _chatWindow!; await chat.Ready;
            await chat.RefreshConversation();
            Check("independent resizable chat remains available", chat.IsVisible && chat.ShowInTaskbar && chat.ResizeMode == ResizeMode.CanResize && chat.WindowStyle == WindowStyle.SingleBorderWindow);
            File.WriteAllText(Path.Combine(directory, "backend-address.txt"), _backend!.ServiceAddress!.AbsoluteUri);
            var menu = CreatePetMenu();
            Check("right-click shows only settings and gacha", menu.Items.OfType<MenuItem>().Select(i => i.Header.ToString()).SequenceEqual(new[] { "设置", "扭蛋" }));
            var entries = CreateSettingsMenu().Items.OfType<MenuItem>().ToDictionary(m => m.Header.ToString()!);
            Check("right-click restores chat, AI, animation and movement controls", new[] { "聊天窗口", "AI 接口与知识库", "桌宠设置与动画", "向右慢走", "鼠标穿透（从托盘恢复）" }.All(entries.ContainsKey));
            entries["桌宠设置与动画"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check("right-click settings opens restored window", _settingsWindow is { IsVisible: true } && _settingsWindow.ActionBox.Items.Count >= 10);
            Capture("desktop-settings", _settingsWindow!); _settingsWindow!.Close();
            entries["AI 接口与知识库"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var ai = _aiSettingsWindow!; await ai.Ready;
            Check("AI settings loads saved compatible model and knowledge", ai.IsVisible && ai.ModelBox.Text == "mock-chat" && ai.FilesBox.Items.Count == 2);
            Capture("ai-settings", ai); ai.Tabs.SelectedIndex = 1; Capture("knowledge-settings", ai); ai.Close();
            chat.InputBox.Text = "星光咖啡店什么时候营业？"; var chatTurn = chat.Send(); await ObserveThinking(chatTurn);
            Check("standalone chat also switches to thinking while streaming", _state == "thinking" && chat.IsGenerating);
            await chatTurn; Advance(Now, TimeSpan.FromHours(10));
            Check("standalone reply completion restores idle", _state == "idle" && !IsThinking);
            string ChatText() => string.Join("\n", chat.MessagesPanel.Children.OfType<Border>().Select(b => ((StackPanel)b.Child).Children.OfType<TextBox>().First().Text));
            Check("standalone window displays streamed reply", ChatText().Contains("晚上九点") && chat.InputBox.Text.Length == 0 && chat.SendButton.IsEnabled);
            Check("internal source markers stay out of the chat display", !ChatText().Contains("[K1]"));
            chat.InputBox.Text = "那你喜欢什么？"; await chat.Send();
            var id = chat.ConversationId!;
            var conversation = await _backend.Call("GET", "/api/conversations/" + id, null, CancellationToken.None);
            Check("bubble and window share saved multi-turn history", conversation.GetProperty("messages").GetArrayLength() == 6 && chat.HistoryBox.Items.Count > 0);
            chat.InputBox.Text = "思考动作取消验证"; var cancelledTurn = chat.Send(); await ObserveThinking(cancelledTurn);
            Check("cancel test begins in thinking", _state == "thinking" && chat.IsGenerating);
            chat.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await cancelledTurn; Advance(Now, TimeSpan.FromHours(10));
            Check("stopping generation clears thinking", _state == "idle" && !IsThinking);
            chat.InputBox.Text = "思考动作错误验证"; await chat.Send(); Advance(Now, TimeSpan.FromHours(10));
            Check("upstream error clears thinking without saving an incomplete turn", !IsThinking && _state == "idle" && (await _backend.Call("GET", "/api/conversations/" + id, null, CancellationToken.None)).GetProperty("messages").GetArrayLength() == 6);
            ShowBubble();
            Check("next click opens fresh blank input and clears previous reply", bubble.IsVisible && bubble.InputBox.Text.Length == 0 && !bubble.HeadText.IsVisible);
            bubble.InputBox.Text = "再聊一句。";
            await bubble.Send();
            Check("visible standalone chat refreshes after a bubble reply", chat.MessagesPanel.Children.OfType<Border>().Count() == 8 && ChatText().Contains("再聊一句"));
            // Both surfaces can be used rapidly; the shared gate preserves complete turns.
            chat.InputBox.Text = "窗口发一条。"; var windowSend = chat.Send();
            bubble.InputBox.Text = "气泡发一条。"; var bubbleSend = bubble.Send();
            await Task.WhenAll(windowSend, bubbleSend);
            conversation = await _backend.Call("GET", "/api/conversations/" + id, null, CancellationToken.None);
            Check("concurrent chat surfaces serialize complete conversation turns", conversation.GetProperty("messages").GetArrayLength() == 12 && bubble.HeadText.Reply.Contains("晚上九点"));
            Capture("standalone-chat", chat);
            chat.Close(); ShowChat(); chat = _chatWindow!; await chat.Ready;
            Check("reopening chat restores persisted conversation", chat.ConversationId == id && ChatText().Contains("晚上九点"));
            chat.WindowState = WindowState.Minimized; ShowChat();
            Check("existing minimized chat is reused and restored", ReferenceEquals(chat, _chatWindow) && chat.WindowState == WindowState.Normal);
            chat.InputBox.Text = "思考动作取消验证"; var closedTurn = chat.Send(); await ObserveThinking(closedTurn);
            chat.Close(); await closedTurn; Advance(Now, TimeSpan.FromHours(10));
            Check("closing a generating chat releases thinking state", !IsThinking && _state == "idle");
            Check("latest idle preserved", _pack.Get("idle").Frames.Count == 12 && _pack.Get("idle").DurationMs == 3460);
            BeginPetDrag(); Check("suspended blink uses supplied six frames", _pack.Get("drag").Frames.Count == 6);
            MovePetDrag(Left + 10, Top); Check("right drag uses supplied eight frames", _state == "drag-right" && _clip!.Frames.Count == 8);
            MovePetDrag(Left - 10, Top); Check("left drag uses supplied eight frames", _state == "drag-left" && _clip!.Frames.Count == 8);
            var frame = _renderedFrame;
            for (var i = 0; i < 30 && ReferenceEquals(frame, _renderedFrame); i++)
            {
                MovePetDrag(Left - 1, Top); await Task.Delay(40);
            }
            Check("drag animation advances", !ReferenceEquals(frame, _renderedFrame));
            for (var i = 0; i < 30 && _state != "drag"; i++) await Task.Delay(40);
            Check("stationary drag switches to suspended blink", _state == "drag");
            EndPetDrag(); await Task.Delay(50);
            Check("drag release returns to idle without opening chat", _chatWindow == null && _state == "idle");
        }
        catch (Exception ex) { failed = true; checks.Add(new { name = "restored UI smoke", passed = false, error = ex.ToString() }); }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failed ? 1 : 0);
    }
}
