using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HanekawaPet.Platform;

namespace HanekawaPet;

public sealed class HeadTextWindow : Window
{
    private readonly PetWindow _pet;
    private readonly TextBlock _text;
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _dismiss = new();
    internal string Reply => _text.Text;
    public HeadTextWindow(PetWindow pet)
    {
        _pet = pet;
        Title = ""; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; IsHitTestVisible = false;
        Width = 320; SizeToContent = SizeToContent.Height;
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Padding = new Thickness(7),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Opacity = 1 } };
        _scroll = new ScrollViewer { Content = _text, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
        SourceInitialized += (_, _) => DesktopPlatform.SetClickThrough(this, true);
        SizeChanged += (_, _) => Reposition();
        _dismiss.Tick += (_, _) => Clear();
        Closed += (_, _) => _dismiss.Stop();
    }
    public void Display(string text)
    {
        _dismiss.Stop(); _text.Text = System.Text.RegularExpressions.Regex.Replace(text, @"\[K\d+\]", "").Trim();
        Topmost = _pet.Preferences.Topmost;
        if (!IsVisible) Show();
        UpdateLayout(); _scroll.ScrollToEnd(); Reposition();
    }
    public void Finish()
    {
        _dismiss.Interval = TimeSpan.FromSeconds(Math.Clamp(_text.Text.Length / 5d + 4, 8, 45));
        _dismiss.Start();
    }
    public void Clear() { _dismiss.Stop(); _text.Text = ""; Hide(); }
    public void Reposition()
    {
        if (!IsVisible || !_pet.IsLoaded) return;
        var area = DesktopPlatform.WorkArea(_pet);
        Width = Math.Min(320, Math.Max(120, area.Width - 16));
        Left = Math.Clamp(_pet.Left + _pet.Width / 2 - Width / 2, area.Left + 4, Math.Max(area.Left + 4, area.Right - Width - 4));
        Top = Math.Clamp(_pet.Top - ActualHeight, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - ActualHeight - 4));
    }
}
