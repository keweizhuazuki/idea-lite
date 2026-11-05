using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdeaLite.Core;

namespace IdeaLite;

public static class Dialogs
{
    public static string? Prompt(Window owner, string title, string description, string value = "", bool multiline = false)
    {
        var window = new Window { Owner = owner, Title = title, Width = 620, Height = multiline ? 360 : 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var label = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75 };
        var ok = new Button { Content = "确定", IsDefault = !multiline, MinWidth = 75 };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var input = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top };
        panel.Children.Add(input); window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }

    public static bool PushPreview(Window owner, string destination, string commits)
    {
        var dialog = new Window { Title = "推送提交", Owner = owner, Width = 590, Height = 325, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var route = new TextBlock { Text = destination, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 5, 5, 10) };
        DockPanel.SetDock(route, Dock.Top); panel.Children.Add(route);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var push = new Button { Content = "推送", IsDefault = true, Style = (Style)owner.FindResource("Primary") };
        push.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); buttons.Children.Add(push);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { IsReadOnly = true, Text = string.IsNullOrWhiteSpace(commits) ? "没有待推送提交" : commits.TrimEnd(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    public static bool Confirm(Window owner, string message) => MessageBox.Show(owner, message, "Idea Lite · 确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
