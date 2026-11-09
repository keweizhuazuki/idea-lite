using System.ComponentModel;
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
    private TaskCompletionSource<bool>? _completion;
    private bool _closing;
    public SearchHit? Result { get; private set; }

    public Task<bool> ShowSearchAsync()
    {
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Owner.PreviewMouseDown += OwnerMouseDown;
        Show(); return _completion.Task;
    }
    private void OwnerMouseDown(object sender, MouseButtonEventArgs e) => Dismiss();

    private void Dismiss()
    {
        if (!_closing && IsVisible) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
        if (e.Cancel) _closing = false;
    }

    public SearchWindow(Window owner, WorkspaceSnapshot snapshot, string mode, string initial = "",
        IReadOnlyList<SearchHit>? choices = null, IReadOnlyList<SearchHit>? actions = null, string? choiceTitle = null)
    {
        InitializeComponent();
        Owner = owner; _snapshot = snapshot; _mode = mode; _choices = choices; _actions = actions ?? [];
        Width = Math.Min(Width, Math.Max(500, owner.ActualWidth - 90));
        Height = Math.Min(Height, Math.Max(280, owner.ActualHeight - 90));
        if (choiceTitle != null)
        {
            Title = choiceTitle;
            Height = Math.Min(Height, 320);
            Scopes.Children.Add(new TextBlock { Text = choiceTitle, Margin = new Thickness(6, 5, 6, 5), FontWeight = FontWeights.SemiBold });
        }
        else foreach (var category in Categories)
        {
            if (choices != null && category.Id != "symbols") continue;
            var button = new Button { Content = category.Label, Uid = category.Id, Style = (Style)FindResource("DockTab") }.Localize("Content");
            button.Click += async (_, _) => await SelectScopeAsync(category.Id);
            Scopes.Children.Add(button);
        }
        Loaded += async (_, _) =>
        {
            Query.Text = initial; Query.Focus(); Query.SelectAll();
            await SelectScopeAsync(mode);
        };
        // WPF can send Deactivated while destroying an owned window. Wait for the current message to finish.
        Deactivated += (_, _) =>
        {
            if (_completion == null || _closing) return;
            Dispatcher.BeginInvoke(() => { if (!IsActive) Dismiss(); });
        };
        Closed += (_, _) => { Owner.PreviewMouseDown -= OwnerMouseDown; _search?.Cancel(); _search?.Dispose(); _completion?.TrySetResult(Result != null); };
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
        UiText.Set(SearchState, "Text", "正在搜索…");
        try
        {
            IReadOnlyList<SearchHit> results;
            if (_choices != null) results = _choices.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
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
            UiText.Set(SearchState, "Text", results.Count == 0 ? (query.Length == 0 && mode == "text" ? "输入要查找的文本" : "无匹配结果") : "");
            UpdateFooter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) UiText.Set(SearchState, "Text", ex.Message); }
    }
    private void UpdateFooter()
    {
        UiText.Set(SearchFooter, "Text", _mode != "usages" && Results.SelectedItem is SearchHit hit
            ? $"{Results.Items.Count} 个结果    {UiText.Current.Translate(hit.Kind)}    {(hit.Description == hit.Kind ? UiText.Current.Translate(hit.Description) : hit.Description)}" : "↑↓ 选择    Enter 打开    Esc 关闭");
    }
    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); return; }
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
    private void Accept() { if (Results.SelectedItem is SearchHit hit) { Result = hit; Dismiss(); } }
    private async void QueryChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) await UpdateAsync(); }
    private async void OptionClick(object sender, RoutedEventArgs e) => await UpdateAsync();
    private void ResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem) { e.Handled = true; Accept(); }
    }
    private void ResultChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateFooter(); }
}
