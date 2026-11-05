using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MergeWindow : Window
{
    private readonly Func<string, Task> _apply;
    private readonly string _initial;
    private readonly DispatcherTimer _update = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _applying, _applied;

    public MergeWindow(Window owner, string path, GitDiff sides, string draft, EditorStyle style, Func<string, Task> apply)
    {
        InitializeComponent();
        Owner = owner; Title = "合并冲突 — " + path;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        FileLabel.Text = path; _initial = draft; _apply = apply;
        foreach (var editor in new[] { Ours, ResultEditor, Theirs })
        {
            EditorSupport.Configure(editor, editor != ResultEditor);
            EditorSupport.Highlight(editor, path);
        }
        Ours.Text = sides.OldText; Theirs.Text = sides.NewText; ResultEditor.Text = draft;
        ResultEditor.Options.IndentationSize = style.IndentSize;
        ResultEditor.Options.ConvertTabsToSpaces = !style.UseTabs;
        ResultEditor.TextChanged += (_, _) => { _update.Stop(); _update.Start(); };
        _update.Tick += (_, _) => { _update.Stop(); UpdateStatus(); };
        Loaded += (_, _) => { Navigate(0); ResultEditor.TextArea.Focus(); };
        Closed += (_, _) => _update.Stop();
    }
    private IReadOnlyList<ConflictBlock> Blocks() => ConflictParser.Parse(ResultEditor.Text);
    private int CurrentIndex(IReadOnlyList<ConflictBlock> blocks)
    {
        var position = ResultEditor.CaretOffset;
        for (int i = 0; i < blocks.Count; i++)
            if (position < blocks[i].Start + blocks[i].Length) return i;
        return 0;
    }
    private void UpdateStatus()
    {
        try
        {
            int remaining = Blocks().Count;
            MergeStatus.Text = remaining == 0 ? "所有文本冲突已处理" : $"剩余 {remaining} 处冲突";
            ApplyButton.IsEnabled = remaining == 0 && !_applying;
        }
        catch (FormatException ex) { MergeStatus.Text = ex.Message; ApplyButton.IsEnabled = false; }
    }
    private void Navigate(int direction)
    {
        try
        {
            var blocks = Blocks();
            if (blocks.Count > 0)
            {
                var index = (CurrentIndex(blocks) + direction + blocks.Count) % blocks.Count;
                var block = blocks[index];
                ResultEditor.CaretOffset = block.Start;
                ResultEditor.ScrollToLine(ResultEditor.Document.GetLineByOffset(block.Start).LineNumber);
                Locate(Ours, block.Ours); Locate(Theirs, block.Theirs);
            }
            UpdateStatus();
        }
        catch (FormatException ex) { MergeStatus.Text = ex.Message; ApplyButton.IsEnabled = false; }
    }
    private static void Locate(TextEditor editor, string text)
    {
        if (text.Length == 0) return;
        int offset = editor.Text.IndexOf(text, StringComparison.Ordinal);
        if (offset < 0) return;
        editor.Select(offset, text.Length);
        editor.ScrollToLine(editor.Document.GetLineByOffset(offset).LineNumber);
    }
    private void Choose(string choice)
    {
        try
        {
            var blocks = Blocks();
            if (blocks.Count == 0) return;
            var block = blocks[CurrentIndex(blocks)];
            var replacement = choice switch { "ours" => block.Ours, "theirs" => block.Theirs, _ => block.Ours + block.Theirs };
            ResultEditor.Document.Replace(block.Start, block.Length, replacement);
            ResultEditor.CaretOffset = Math.Min(block.Start, ResultEditor.Document.TextLength);
            Navigate(0);
        }
        catch (FormatException ex) { MergeStatus.Text = ex.Message; }
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        try
        {
            if (Blocks().Count > 0) { UpdateStatus(); return; }
            _applying = true; IsEnabled = false;
            await _apply(ResultEditor.Text);
            _applied = true; DialogResult = true;
        }
        catch (Exception ex) { MergeStatus.Text = ex.Message; MergeStatus.ToolTip = ex.Message; }
        finally { _applying = false; IsEnabled = true; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_applied) return;
        if (_applying || (ResultEditor.Text != _initial && !Dialogs.Confirm(this, "放弃合并窗口中尚未应用的编辑？"))) e.Cancel = true;
    }
    private void OursClick(object sender, RoutedEventArgs e) => Choose("ours");
    private void TheirsClick(object sender, RoutedEventArgs e) => Choose("theirs");
    private void BothClick(object sender, RoutedEventArgs e) => Choose("both");
    private void NextClick(object sender, RoutedEventArgs e) => Navigate(1);
    private void PreviousClick(object sender, RoutedEventArgs e) => Navigate(-1);
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
}
