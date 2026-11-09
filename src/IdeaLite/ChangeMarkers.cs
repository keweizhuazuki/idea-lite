using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiffPlex;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;

namespace IdeaLite;

public sealed record LineChange(int FirstLine, int LastLine, ChangeType Kind);

public static class LineChanges
{
    public static readonly Brush Added = Brush("#6A8759"), Modified = Brush("#4E789A"), Deleted = Brush("#C75450");
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public static Brush Color(ChangeType kind) => kind == ChangeType.Inserted ? Added : kind == ChangeType.Deleted ? Deleted : Modified;

    public static LineChange[] Compare(string baseline, string current)
    {
        static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
        int count = current.Count(character => character == '\n') + 1;
        return new Differ().CreateLineDiffs(Normalize(baseline), Normalize(current), false).DiffBlocks.Select(block =>
            new LineChange(Math.Clamp(block.InsertStartB + 1, 1, count), Math.Clamp(block.InsertStartB + Math.Max(1, block.InsertCountB), 1, count),
                block.InsertCountB == 0 ? ChangeType.Deleted : block.DeleteCountA == 0 ? ChangeType.Inserted : ChangeType.Modified)).ToArray();
    }

    public static LineChange[] Aligned(IReadOnlyList<DiffPiece> lines, bool oldSide)
    {
        var changes = new List<LineChange>();
        for (int index = 0; index < lines.Count; index++)
        {
            var kind = lines[index].Type;
            if (kind == ChangeType.Unchanged) continue;
            if (kind == ChangeType.Imaginary) kind = oldSide ? ChangeType.Inserted : ChangeType.Deleted;
            if (changes.LastOrDefault() is { } previous && previous.Kind == kind && previous.LastLine == index)
                changes[^1] = previous with { LastLine = index + 1 };
            else changes.Add(new(index + 1, index + 1, kind));
        }
        return changes.ToArray();
    }
}

/// <summary>
/// 在行号与源码之间显示当前缓冲区相对 Git HEAD 的改动，包含尚未保存的编辑。
/// </summary>
public sealed class LineChangeMargin : AbstractMargin
{
    public IReadOnlyList<LineChange> Changes { get; set; } = [];
    protected override Size MeasureOverride(Size availableSize) => new(8, 0);
    protected override void OnTextViewChanged(ICSharpCode.AvalonEdit.Rendering.TextView oldView, ICSharpCode.AvalonEdit.Rendering.TextView newView)
    {
        if (oldView != null) oldView.VisualLinesChanged -= Repaint;
        base.OnTextViewChanged(oldView, newView);
        if (newView != null) newView.VisualLinesChanged += Repaint;
    }
    private void Repaint(object? sender, EventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext drawing)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid) return;
        foreach (var visual in view.VisualLines)
            foreach (var change in Changes)
            {
                if (change.LastLine < visual.FirstDocumentLine.LineNumber || change.FirstLine > visual.LastDocumentLine.LineNumber) continue;
                double top = visual.VisualTop - view.VerticalOffset;
                drawing.DrawRectangle(LineChanges.Color(change.Kind), null, new Rect(2, top, change.Kind == ChangeType.Deleted ? 6 : 3, change.Kind == ChangeType.Deleted ? 2 : visual.Height));
            }
    }
}

/// <summary>
/// 将整份差异的变更压缩到独立色带；点击色块可同步定位两侧文档。
/// </summary>
public sealed class DiffOverview : FrameworkElement
{
    private TextEditor? _editor;
    private Action<int>? _navigate;
    public IReadOnlyList<LineChange> Changes { get; private set; } = [];
    public DiffOverview() { Width = 12; Cursor = Cursors.Hand; ClipToBounds = true; }
    public void Attach(TextEditor editor, Action<int> navigate)
    {
        _editor = editor; _navigate = navigate;
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        editor.SizeChanged += (_, _) => InvalidateVisual();
        UiText.Set(this, "ToolTip", "差异总览 · 点击跳转");
    }
    public void SetChanges(IReadOnlyList<DiffPiece> lines, bool oldSide) => SetChanges(LineChanges.Aligned(lines, oldSide));
    public void SetChanges(IReadOnlyList<LineChange> changes)
    {
        Changes = changes;
        InvalidateVisual();
    }
    private double MapHeight => Math.Max(1, Math.Min(ActualHeight, _editor?.TextArea.TextView.ActualHeight ?? ActualHeight));
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_editor == null) return;
        double height = MapHeight, count = Math.Max(1, _editor.Document.LineCount);
        var view = _editor.TextArea.TextView;
        double documentHeight = Math.Max(height, count * view.DefaultLineHeight);
        if (documentHeight > height)
        {
            double top = Math.Min(height - 1, view.VerticalOffset / documentHeight * height);
            double viewport = Math.Min(height - top, Math.Max(8, height * height / documentHeight));
            drawing.DrawRectangle(null, new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(67, 70, 76)), 1), new Rect(.5, top, Math.Max(0, ActualWidth - 1), viewport));
        }
        foreach (var change in Changes)
        {
            double top = (change.FirstLine - 1) / count * height;
            double length = Math.Max(3, (change.LastLine - change.FirstLine + 1) / count * height);
            drawing.DrawRectangle(LineChanges.Color(change.Kind), null, new Rect(2, Math.Min(top, height - 3), Math.Max(1, ActualWidth - 4), Math.Min(length, height)));
        }
    }
    internal void Navigate(double y)
    {
        if (_editor == null || Changes.Count == 0) return;
        double line = Math.Clamp(y / MapHeight, 0, 1) * _editor.Document.LineCount + 1;
        var closest = Changes.MinBy(change => Math.Abs((change.FirstLine + change.LastLine) / 2.0 - line))!;
        _navigate?.Invoke(closest.FirstLine);
        _editor.TextArea.Focus();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); Navigate(e.GetPosition(this).Y); e.Handled = true; }
}
