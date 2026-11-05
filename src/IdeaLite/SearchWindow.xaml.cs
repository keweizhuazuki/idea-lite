using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IdeaLite.Core;

namespace IdeaLite;

public partial class SearchWindow : Window
{
    private static readonly (string Id, string Label)[] Categories =
        [("all", "全部"), ("classes", "类"), ("files", "文件"), ("symbols", "符号"), ("actions", "操作"), ("text", "文本")];
    private readonly WorkspaceSnapshot _snapshot;
    private readonly IReadOnlyList<SearchHit>? _choices;
    private readonly IReadOnlyList<SearchHit> _actions;
    private CancellationTokenSource? _search;
    private string _mode;
    public SearchHit? Result { get; private set; }

    public SearchWindow(Window owner, WorkspaceSnapshot snapshot, string mode, string initial = "",
        IReadOnlyList<SearchHit>? choices = null, IReadOnlyList<SearchHit>? actions = null)
    {
        InitializeComponent();
        Owner = owner; _snapshot = snapshot; _mode = mode; _choices = choices; _actions = actions ?? [];
        Width = Math.Min(Width, Math.Max(500, owner.ActualWidth - 90));
        Height = Math.Min(Height, Math.Max(280, owner.ActualHeight - 90));
        foreach (var category in Categories)
        {
            if (choices != null && category.Id != "symbols") continue;
            var button = new Button { Content = category.Label, Uid = category.Id, Style = (Style)FindResource("DockTab") };
            button.Click += async (_, _) => await SelectScopeAsync(category.Id);
            Scopes.Children.Add(button);
        }
        Loaded += async (_, _) =>
        {
            Query.Text = initial; Query.Focus(); Query.SelectAll();
            await SelectScopeAsync(mode);
        };
        Closed += (_, _) => { _search?.Cancel(); _search?.Dispose(); };
    }
    private async Task SelectScopeAsync(string mode)
    {
        _mode = mode;
        foreach (var button in Scopes.Children.OfType<Button>()) button.Tag = button.Uid == mode ? "active" : null;
        TextOptions.Visibility = mode == "text" ? Visibility.Visible : Visibility.Collapsed;
        Query.Focus();
        await UpdateAsync();
    }
    private async Task UpdateAsync()
    {
        _search?.Cancel(); _search?.Dispose(); _search = new();
        var token = _search.Token;
        string query = Query.Text, mode = _mode;
        Results.ItemsSource = null;
        SearchState.Text = "正在搜索…";
        try
        {
            IReadOnlyList<SearchHit> results;
            if (_choices != null) results = _choices.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            else if (mode == "text")
            {
                await Task.Delay(180, token);
                results = await WorkspaceService.SearchTextAsync(_snapshot, query, MatchCase.IsChecked == true, UseRegex.IsChecked == true, token);
            }
            else
            {
                var actions = _actions.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(query, StringComparison.OrdinalIgnoreCase));
                var matches = mode == "actions" ? [] : await Task.Run(() => WorkspaceService.Search(_snapshot, query, mode), token);
                results = mode is "actions" or "all" ? matches.Concat(actions).Take(150).ToArray() : matches;
            }
            token.ThrowIfCancellationRequested();
            Results.ItemsSource = results; Results.SelectedIndex = results.Count > 0 ? 0 : -1;
            SearchState.Text = results.Count == 0 ? (query.Length == 0 && mode == "text" ? "输入要查找的文本" : "无匹配结果") : "";
            UpdateFooter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SearchState.Text = ex.Message; }
    }
    private void UpdateFooter()
    {
        SearchFooter.Text = Results.SelectedItem is SearchHit hit
            ? $"{Results.Items.Count} 个结果    {hit.Kind}    {hit.Description}" : "↑↓ 选择    Enter 打开    Esc 关闭";
    }
    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key == Key.Enter) { e.Handled = true; Accept(); return; }
        if (e.Key == Key.Tab && _choices == null)
        {
            e.Handled = true;
            int i = Array.FindIndex(Categories, c => c.Id == _mode);
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            await SelectScopeAsync(Categories[(i + step + Categories.Length) % Categories.Length].Id);
        }
        if (e.Key is Key.Down or Key.Up && Results.Items.Count > 0)
        {
            e.Handled = true;
            Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
            Results.ScrollIntoView(Results.SelectedItem);
        }
    }
    private void Accept() { if (Results.SelectedItem is SearchHit hit) { Result = hit; DialogResult = true; } }
    private async void QueryChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) await UpdateAsync(); }
    private async void OptionClick(object sender, RoutedEventArgs e) => await UpdateAsync();
    private void ResultDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void ResultChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateFooter(); }
}
