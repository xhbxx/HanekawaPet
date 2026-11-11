using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using HanekawaPet.Core;
using HanekawaPet.Platform;

namespace HanekawaPet;

public partial class SpeechBubbleWindow : Window
{
    private readonly PetWindow _pet;
    private readonly BackendHost _backend;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HeadTextWindow _headText;
    private CancellationTokenSource? _generation;
    private string? _conversationId;
    private string? _error;
    private bool _positioning;
    internal HeadTextWindow HeadText => _headText;
    internal bool IsGenerating => _generation is { IsCancellationRequested: false };

    public SpeechBubbleWindow(PetWindow pet, BackendHost backend)
    {
        _pet = pet; _backend = backend;
        InitializeComponent();
        _headText = new HeadTextWindow(pet) { Owner = pet };
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Deactivated += (_, _) => Hide();
        Closed += (_, _) => { _generation?.Cancel(); _lifetime.Cancel(); _headText.Close(); };
    }
    private Task<JsonElement> Api(string method, string path, object? body = null) => _backend.Call(method, path, body, _lifetime.Token);
    internal async Task EnsureReady()
    {
        _conversationId = null;
        try
        {
            _error = null;
            if (_pet.ActiveConversationId is { } active) { _conversationId = active; return; }
            var conversations = await Api("GET", "/api/conversations");
            var conversation = conversations.GetArrayLength() > 0 ? conversations[0] : await Api("POST", "/api/conversations", new { });
            _conversationId = conversation.GetProperty("id").GetString();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _error = ex.Message; }
    }
    internal async Task Send()
    {
        var message = InputBox.Text.Trim();
        if (message.Length == 0) return;
        _pet.RecordInteraction();
        _generation?.Cancel();
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generation = generation;
        Hide(); InputBox.Clear(); _headText.Clear();
        var answer = new StringBuilder();
        var acquired = false;
        try
        {
            await _pet.ChatGate.WaitAsync(generation.Token); acquired = true;
            await EnsureReady();
            generation.Token.ThrowIfCancellationRequested();
            if (_conversationId == null) throw new InvalidOperationException(_error ?? "聊天暂时不可用。");
            await _backend.Stream(_conversationId, message, item =>
            {
                if (!ReferenceEquals(_generation, generation) || generation.IsCancellationRequested) return;
                if (item.GetProperty("type").GetString() == "delta")
                {
                    answer.Append(item.GetProperty("text").GetString());
                    _headText.Display(answer.ToString());
                }
            }, generation.Token);
            if (ReferenceEquals(_generation, generation)) _headText.Finish();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested && ReferenceEquals(_generation, generation))
            {
                _pet.LogChatError(ex);
                _headText.Display(answer.Length == 0 ? "暂时没能连上 AI，请稍后再试。" : answer.ToString());
                _headText.Finish();
            }
        }
        finally
        {
            if (acquired) _pet.ChatGate.Release();
            if (ReferenceEquals(_generation, generation)) _generation = null;
        }
        if (!_lifetime.IsCancellationRequested) await _pet.RefreshChat();
    }
    public void Open()
    {
        _generation?.Cancel(); _headText.Clear(); InputBox.Clear();
        Topmost = _pet.Preferences.Topmost;
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Reposition(); Activate(); InputBox.Focus();
    }
    public void Dismiss() { _generation?.Cancel(); Hide(); _headText.Clear(); }
    public void Reposition()
    {
        _headText.Reposition();
        if (_positioning || !IsVisible || !_pet.IsLoaded) return;
        _positioning = true;
        try
        {
            var area = DesktopPlatform.WorkArea(_pet);
            Width = Math.Min(260, Math.Max(120, area.Width - 16));
            var height = ActualHeight > 1 ? ActualHeight : 40;
            Left = Math.Clamp(_pet.Left + _pet.Width / 2 - Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - Width - 4));
            Top = Math.Clamp(_pet.Top - height - 5, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - height - 4));
        }
        finally { _positioning = false; }
    }
    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; await Send(); }
    }
    private void OnWindowKey(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Hide(); } }
}
