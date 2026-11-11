using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class ChatWindow : Window
{
    private readonly PetWindow _pet;
    private readonly BackendHost _backend;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _loaded = new();
    private CancellationTokenSource? _generation;
    private string? _conversationId;
    private bool _sending, _loadingHistory, _switching;
    private record HistoryItem(string Id, string Title);
    internal Task Ready => _loaded.Task;
    internal bool IsGenerating => _sending;

    public ChatWindow(PetWindow pet, BackendHost backend)
    {
        _pet = pet; _backend = backend;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await LoadHistory();
                if (_conversationId == null) await NewConversation();
                var config = await Api("GET", "/api/config");
                StatusText.Text = string.IsNullOrWhiteSpace(config.GetProperty("model").GetString())
                    ? "请先在右上角填写 AI 接口、模型和密钥。" : "已就绪。";
                EnableChat(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
            finally { _loaded.TrySetResult(); }
        };
        Closed += (_, _) => { _generation?.Cancel(); _lifetime.Cancel(); };
    }
    private Task<JsonElement> Api(string method, string path, object? body = null) => _backend.Call(method, path, body, _lifetime.Token);
    private void EnableChat(bool enabled) => SendButton.IsEnabled = NewButton.IsEnabled = HistoryBox.IsEnabled = enabled;
    private void Report(Exception error)
    {
        if (_lifetime.IsCancellationRequested) return;
        StatusText.Text = error is OperationCanceledException ? "已取消。" : error.Message;
        _pet.LogChatError(error);
    }
    private async Task LoadHistory()
    {
        var response = await Api("GET", "/api/conversations");
        var items = response.EnumerateArray().Select(c => new HistoryItem(c.GetProperty("id").GetString()!, c.GetProperty("title").GetString()!)).ToList();
        _loadingHistory = true;
        try
        {
            HistoryBox.ItemsSource = items;
            HistoryBox.SelectedItem = items.FirstOrDefault(c => c.Id == _conversationId) ?? items.FirstOrDefault();
        }
        finally { _loadingHistory = false; }
        if (_conversationId == null && HistoryBox.SelectedItem is HistoryItem first) await LoadConversation(first.Id);
    }
    internal async Task RefreshConversation()
    {
        await Ready;
        if (_sending || _switching || _lifetime.IsCancellationRequested) return;
        _switching = true; EnableChat(false);
        try
        {
            await LoadHistory();
            if (_conversationId != null) await LoadConversation(_conversationId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
        finally { _switching = false; if (!_lifetime.IsCancellationRequested) EnableChat(true); }
    }
    internal async Task LoadConversation(string id)
    {
        var conversation = await Api("GET", "/api/conversations/" + id);
        _conversationId = id; MessagesPanel.Children.Clear();
        foreach (var message in conversation.GetProperty("messages").EnumerateArray())
            AddMessage(message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!);
        if (MessagesPanel.Children.Count == 0) MessagesPanel.Children.Add(new TextBlock { Text = "想聊点什么？", Foreground = Brushes.SlateGray, Margin = new Thickness(8) });
        ChatScroll.ScrollToEnd();
    }
    private async Task NewConversation()
    {
        if (_sending || _switching) return;
        _switching = true; EnableChat(false);
        try
        {
            var response = await Api("POST", "/api/conversations", new { });
            await LoadConversation(response.GetProperty("id").GetString()!);
            await LoadHistory(); StatusText.Text = "新对话已开始。";
        }
        finally { _switching = false; if (!_lifetime.IsCancellationRequested) EnableChat(true); }
    }
    private TextBox AddMessage(string role, string text)
    {
        var user = role == "user";
        if (!user) text = DisplayReply(text);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = user ? "你" : "小翼", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 6) });
        var body = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(35, 58, 89)) };
        content.Children.Add(body);
        MessagesPanel.Children.Add(new Border { Child = content, Padding = new Thickness(13), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(user ? Color.FromRgb(234, 242, 255) : Color.FromRgb(244, 246, 250)), Margin = new Thickness(user ? 35 : 0, 0, user ? 0 : 35, 12) });
        return body;
    }
    private static string DisplayReply(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\[K\d+\]", "").Trim();
    internal async Task Send()
    {
        var message = InputBox.Text.Trim();
        if (_sending || _switching || !SendButton.IsEnabled || _conversationId == null || message.Length == 0) return;
        _pet.RecordInteraction();
        _sending = true; EnableChat(false); StopButton.IsEnabled = true;
        _generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        InputBox.Clear();
        if (MessagesPanel.Children.Count == 1 && MessagesPanel.Children[0] is TextBlock) MessagesPanel.Children.Clear();
        AddMessage("user", message); var body = AddMessage("assistant", ""); var answer = new StringBuilder();
        StatusText.Text = "正在回复…";
        var acquired = false;
        try
        {
            await _pet.ChatGate.WaitAsync(_generation.Token); acquired = true;
            await _backend.Stream(_conversationId, message, item =>
            {
                if (item.GetProperty("type").GetString() == "status")
                { StatusText.Text = item.GetProperty("text").GetString(); return; }
                if (item.GetProperty("type").GetString() != "delta") return;
                answer.Append(item.GetProperty("text").GetString()); body.Text = DisplayReply(answer.ToString()); ChatScroll.ScrollToEnd();
            }, _generation.Token);
            await LoadHistory(); await LoadConversation(_conversationId); StatusText.Text = "回复完成。";
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                StatusText.Text = "已停止，未完成回复没有写入历史。";
                if (InputBox.Text.Length == 0) InputBox.Text = message;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(ex);
            if (!_lifetime.IsCancellationRequested && InputBox.Text.Length == 0) InputBox.Text = message;
        }
        finally
        {
            if (acquired) _pet.ChatGate.Release();
            _generation.Dispose(); _generation = null; _sending = false;
            if (!_lifetime.IsCancellationRequested) { EnableChat(true); StopButton.IsEnabled = false; InputBox.Focus(); }
        }
    }
    internal string? ConversationId => _conversationId;
    private async void OnSend(object sender, RoutedEventArgs e) => await Send();
    private void OnStop(object sender, RoutedEventArgs e) => _generation?.Cancel();
    private void OnSettings(object sender, RoutedEventArgs e) => _pet.ShowAiSettings();
    private async void OnNew(object sender, RoutedEventArgs e)
    { try { await NewConversation(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); } }
    private async void OnHistoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingHistory || _sending || _switching || HistoryBox.SelectedItem is not HistoryItem item) return;
        _switching = true; EnableChat(false);
        try { await LoadConversation(item.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report(ex); }
        finally { _switching = false; if (!_lifetime.IsCancellationRequested) EnableChat(true); }
    }
    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; await Send(); }
    }
}
