using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using HanekawaPet.Core;

namespace HanekawaPet;

public partial class AiSettingsWindow : Window
{
    private readonly BackendHost _backend;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _configBusy, _knowledgeBusy;
    private readonly TaskCompletionSource _loaded = new();
    internal Task Ready => _loaded.Task;
    private record FileItem(string Id, string FileName, string Detail, string? Error);
    public AiSettingsWindow(BackendHost backend)
    {
        _backend = backend;
        InitializeComponent();
        Loaded += async (_, _) => await Guard(async () =>
        {
            try { await LoadConfig(); await LoadKnowledge(); StatusText.Text = "保存接口后，单击桌宠或右键选择‘聊天窗口’开始对话。"; }
            finally { _loaded.TrySetResult(); }
        });
        Closed += (_, _) => _lifetime.Cancel();
    }
    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "操作已取消。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_lifetime.IsCancellationRequested) StatusText.Text = ex.Message; }
    }
    private Task<JsonElement> Api(string method, string path, object? data = null) => _backend.Call(method, path, data, _lifetime.Token);
    private async Task LoadConfig()
    {
        var config = await Api("GET", "/api/config");
        BaseUrlBox.Text = config.GetProperty("baseUrl").GetString(); ModelBox.Text = config.GetProperty("model").GetString();
        KeyHint.Text = config.GetProperty("hasApiKey").GetBoolean() ? "API 密钥（已保存，留空保留）" : "API 密钥（本地免密模型可留空）";
        TemperatureBox.Text = config.GetProperty("temperature").ToString(); MaxTokensBox.Text = config.GetProperty("maxTokens").ToString();
        TimeoutBox.Text = config.GetProperty("timeoutSeconds").ToString(); KeyBox.Clear(); ClearKeyBox.IsChecked = false;
    }
    private async Task SaveConfig(bool test)
    {
        if (_configBusy) return;
        _configBusy = true; SaveConfigButton.IsEnabled = TestConfigButton.IsEnabled = false;
        try
        {
            double? temperature = string.IsNullOrWhiteSpace(TemperatureBox.Text) ? null : double.Parse(TemperatureBox.Text, CultureInfo.InvariantCulture);
            int? maxTokens = string.IsNullOrWhiteSpace(MaxTokensBox.Text) ? null : int.Parse(MaxTokensBox.Text, CultureInfo.InvariantCulture);
            if (!int.TryParse(TimeoutBox.Text, out var timeout)) throw new InvalidOperationException("超时需要填写整数秒。");
            await Api("PUT", "/api/config", new { baseUrl = BaseUrlBox.Text, model = ModelBox.Text, apiKey = KeyBox.Password, clearApiKey = ClearKeyBox.IsChecked == true, temperature, maxTokens, timeoutSeconds = timeout });
            await LoadConfig();
            StatusText.Text = test ? "设置已保存，正在向模型发送连接测试…" : "AI 设置已保存。";
            if (test) { var result = await Api("POST", "/api/config/test", new { }); StatusText.Text = result.GetProperty("message").GetString(); }
        }
        catch (FormatException) { throw new InvalidOperationException("请检查可选参数：Temperature 为小数，输出上限为整数，也可以留空。"); }
        finally { _configBusy = false; SaveConfigButton.IsEnabled = TestConfigButton.IsEnabled = true; }
    }
    private async void OnSaveConfig(object sender, RoutedEventArgs e) => await Guard(() => SaveConfig(false));
    private async void OnTestConfig(object sender, RoutedEventArgs e) => await Guard(() => SaveConfig(true));

    private async Task LoadKnowledge()
    {
        var response = await Api("GET", "/api/knowledge");
        FilesBox.ItemsSource = response.GetProperty("files").EnumerateArray().Select(f => new FileItem(f.GetProperty("id").GetString()!, f.GetProperty("fileName").GetString()!,
            (f.GetProperty("kind").GetString() == "persona" ? "角色设定 · 每轮生效" : "问答资料 · " + f.GetProperty("chunks").GetInt32() + " 个片段") + " · " + f.GetProperty("characters").GetInt32() + " 字符", f.GetProperty("error").GetString())).ToList();
        KnowledgePathText.Text = response.GetProperty("root").GetString();
    }
    private async Task Import(string kind)
    {
        if (_knowledgeBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "知识库文件|*.md;*.json;*.jsonl", Multiselect = true, Title = kind == "persona" ? "选择角色设定文件" : "选择问答资料文件" };
        if (dialog.ShowDialog(this) != true) return;
        _knowledgeBusy = true; PersonaButton.IsEnabled = DocumentsButton.IsEnabled = RemoveFileButton.IsEnabled = false;
        try
        {
            var count = 0;
            foreach (var path in dialog.FileNames)
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidOperationException(Path.GetFileName(path) + " 超过 2 MB。");
                var content = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), _lifetime.Token);
                await Api("POST", "/api/knowledge/import", new { fileName = Path.GetFileName(path), kind, content = content.TrimStart('\uFEFF') }); count++;
                StatusText.Text = "已导入 " + count + " 个文件。";
            }
        }
        finally { _knowledgeBusy = false; PersonaButton.IsEnabled = DocumentsButton.IsEnabled = RemoveFileButton.IsEnabled = true; await LoadKnowledge(); }
    }
    private async void OnImportPersona(object sender, RoutedEventArgs e) => await Guard(() => Import("persona"));
    private async void OnImportDocuments(object sender, RoutedEventArgs e) => await Guard(() => Import("documents"));
    private async void OnRefreshKnowledge(object sender, RoutedEventArgs e) => await Guard(async () => { await LoadKnowledge(); StatusText.Text = "知识库已刷新，文件修改将在下一条消息生效。"; });
    private async void OnOpenKnowledge(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await LoadKnowledge(); Process.Start(new ProcessStartInfo(Path.Combine(_backend.DataRoot, "knowledge")) { UseShellExecute = true });
    });
    private async void OnRemoveFile(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_knowledgeBusy || FilesBox.SelectedItem is not FileItem item) return;
        await Api("DELETE", "/api/knowledge?id=" + Uri.EscapeDataString(item.Id)); await LoadKnowledge(); StatusText.Text = "已从知识库移除：" + item.FileName;
    });
}
