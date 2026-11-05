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

    public static bool Confirm(Window owner, string message) => MessageBox.Show(owner, message, "Idea Lite · 确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed class SearchWindow : Window
{
    private readonly WorkspaceSnapshot _snapshot;
    private readonly TextBox _query = new() { FontSize = 18, Margin = new Thickness(0, 4, 0, 12) };
    private readonly ListBox _results = new();
    private readonly CheckBox _regex = new() { Content = "正则" };
    private readonly CheckBox _case = new() { Content = "区分大小写" };
    private readonly TextBlock _hint = new() { Foreground = Brushes.Gray, Margin = new Thickness(6, 4, 0, 10) };
    private CancellationTokenSource? _search;
    private readonly string _mode;
    public SearchHit? Result { get; private set; }

    public SearchWindow(Window owner, WorkspaceSnapshot snapshot, string mode, string initial = "", IReadOnlyList<SearchHit>? choices = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner; _snapshot = snapshot; _mode = mode;
        Title = mode == "text" ? "在项目中搜索 · Ctrl+Shift+F" : mode == "symbols" ? "搜索代码符号 · Ctrl+Alt+Shift+N" : "Search Everywhere · Shift × 2";
        Width = 830; Height = 580; MinWidth = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold });
        top.Children.Add(_query);
        if (mode == "text")
        {
            var options = new StackPanel { Orientation = Orientation.Horizontal }; options.Children.Add(_regex); options.Children.Add(_case); top.Children.Add(options);
            _regex.Click += async (_, _) => await UpdateAsync(); _case.Click += async (_, _) => await UpdateAsync();
        }
        top.Children.Add(_hint); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        _results.DisplayMemberPath = "";
        var template = new DataTemplate(typeof(SearchHit));
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SearchHit.DisplayName))); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); stack.AppendChild(name);
        var path = new FrameworkElementFactory(typeof(TextBlock)); path.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SearchHit.Location))); path.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(141, 152, 172))); path.SetValue(TextBlock.FontSizeProperty, 11.0); path.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0)); stack.AppendChild(path);
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SearchHit.Description))); detail.SetValue(TextBlock.FontSizeProperty, 11.0); detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); stack.AppendChild(detail);
        template.VisualTree = stack; _results.ItemTemplate = template;
        panel.Children.Add(_results); Content = panel;
        _results.MouseDoubleClick += (_, _) => Accept();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            if (e.Key == Key.Down && _results.Items.Count > 0) { _results.SelectedIndex = Math.Min(_results.Items.Count - 1, _results.SelectedIndex + 1); _results.ScrollIntoView(_results.SelectedItem); e.Handled = true; }
            if (e.Key == Key.Up && _results.Items.Count > 0) { _results.SelectedIndex = Math.Max(0, _results.SelectedIndex - 1); _results.ScrollIntoView(_results.SelectedItem); e.Handled = true; }
        };
        if (choices != null) { _results.ItemsSource = choices; _results.SelectedIndex = 0; _hint.Text = "存在多个候选定义，选择后按 Enter 跳转"; }
        else _query.TextChanged += async (_, _) => await UpdateAsync();
        Loaded += async (_, _) => { _query.Text = initial; _query.Focus(); _query.SelectAll(); if (choices == null) await UpdateAsync(); };
        Closed += (_, _) => { _search?.Cancel(); _search?.Dispose(); };
    }

    private async Task UpdateAsync()
    {
        _search?.Cancel(); _search?.Dispose(); _search = new();
        var token = _search.Token;
        try
        {
            var query = _query.Text;
            if (_mode == "text") { _hint.Text = "搜索中… · 最多显示 250 条 · Esc 关闭"; await Task.Delay(220, token); }
            var results = _mode == "text" ? await WorkspaceService.SearchTextAsync(_snapshot, query, _case.IsChecked == true, _regex.IsChecked == true, token) : WorkspaceService.Search(_snapshot, query, _mode);
            token.ThrowIfCancellationRequested();
            _results.ItemsSource = results; _results.SelectedIndex = results.Count > 0 ? 0 : -1;
            _hint.Text = $"{results.Count} 条结果 · ↑↓ 选择 · Enter 打开 · Esc 关闭" + (_mode == "text" ? " · 搜索单文件上限 512 KiB" : " · 文件名支持模糊匹配");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _hint.Text = ex.Message; }
    }

    private void Accept()
    {
        if (_results.SelectedItem is SearchHit hit) { Result = hit; DialogResult = true; }
    }
}
