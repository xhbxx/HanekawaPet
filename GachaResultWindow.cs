using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HanekawaPet.Core;

namespace HanekawaPet;

internal sealed class GachaResultWindow : Window
{
    public GachaResultWindow(GachaStore store, GachaDraw draw, int count, bool newlyDrawn)
    {
        Title = newlyDrawn ? "今天的小惊喜" : "收藏详情";
        Width = 380; Height = 500; MinWidth = 300; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(250, 247, 240)); FontFamily = new FontFamily("Microsoft YaHei UI");
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = newlyDrawn ? "你抽中了" : "收藏柜里的小东西", FontSize = 15, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) });
        stack.Children.Add(GachaWindow.Artwork(store, draw, 240));
        stack.Children.Add(new TextBlock { Text = draw.Name, FontSize = 23, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        stack.Children.Add(new TextBlock { Text = draw.Description, FontSize = 13, Foreground = Brushes.SlateGray, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = count == 1 ? "第一件收藏" : $"已经收集了 {count} 件", Foreground = Brushes.SlateGray, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 16) });
        var button = new Button { Content = newlyDrawn ? "放入展示柜" : "收好", Padding = new Thickness(22, 9, 22, 9), HorizontalAlignment = HorizontalAlignment.Center, IsDefault = true };
        button.Click += (_, _) => Close(); stack.Children.Add(button);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
