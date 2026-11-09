using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace IdeaLite;

/// <summary>The pane receiving user input drives the aligned view. Deferred follower events never drive back.</summary>
public sealed class DiffScrollSynchronizer
{
    private readonly TextEditor _left, _right;
    private TextEditor? _driver;
    private long _generation;
    private bool _queued;
    private double _lastOffset = double.NaN;
    private int[]? _alignedToRight, _rightToAligned;

    public DiffScrollSynchronizer(TextEditor left, TextEditor right)
    {
        _left = left; _right = right;
        foreach (var editor in new[] { left, right })
        {
            editor.PreviewMouseWheel += (_, _) => Drive(editor);
            editor.PreviewMouseLeftButtonDown += (_, _) => Drive(editor);
            editor.PreviewTextInput += (_, _) => Drive(editor);
            editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Drive(editor);
            };
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Queue(editor);
        }
    }

    public void SetRightLines(int?[]? positions)
    {
        Reset();
        _alignedToRight = _rightToAligned = null;
        if (positions == null || positions.Length == 0) return;
        _alignedToRight = new int[positions.Length];
        _rightToAligned = new int[positions.Count(line => line.HasValue)];
        int next = Math.Max(1, _rightToAligned.Length);
        for (int row = positions.Length - 1; row >= 0; row--)
        {
            if (positions[row] is int line)
            {
                next = line;
                _rightToAligned[line - 1] = row + 1;
            }
            _alignedToRight[row] = next;
        }
    }

    public int RightLine(int row) => _alignedToRight == null ? row :
        _alignedToRight[Math.Clamp(row - 1, 0, _alignedToRight.Length - 1)];

    public int AlignedLine(int line) => _rightToAligned is not { Length: > 0 } rows ? line :
        rows[Math.Clamp(line - 1, 0, rows.Length - 1)];

    private double CorrespondingOffset(TextEditor source, TextEditor follower)
    {
        if (_alignedToRight == null) return source.VerticalOffset;
        var view = source.TextArea.TextView;
        int line = view.GetDocumentLineByVisualTop(source.VerticalOffset).LineNumber;
        int target = ReferenceEquals(source, _left) ? RightLine(line) : AlignedLine(line);
        double withinLine = source.VerticalOffset - view.GetVisualTopByDocumentLine(line);
        return follower.TextArea.TextView.GetVisualTopByDocumentLine(Math.Clamp(target, 1, follower.Document.LineCount)) + withinLine;
    }

    public void AlignFromRight()
    {
        Drive(_right);
        Queue(_right);
    }

    private void Drive(TextEditor editor)
    {
        if (ReferenceEquals(_driver, editor)) return;
        Reset();
        _driver = editor;
    }

    public void Reset()
    {
        ++_generation;
        _queued = false;
        _driver = null;
        _lastOffset = double.NaN;
    }

    private void Queue(TextEditor source)
    {
        if (!ReferenceEquals(source, _driver) || _queued || Math.Abs(source.VerticalOffset - _lastOffset) < .1) return;
        _queued = true;
        long generation = _generation;
        source.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (generation != _generation) return;
            _queued = false;
            _lastOffset = source.VerticalOffset;
            var follower = ReferenceEquals(source, _left) ? _right : _left;
            follower.ScrollToVerticalOffset(CorrespondingOffset(source, follower));
        });
    }

    public void RevealLine(int line)
    {
        Reset();
        long generation = _generation;
        _left.ScrollToLine(line);
        _right.ScrollToLine(RightLine(line));
        _left.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation == _generation) _right.ScrollToVerticalOffset(CorrespondingOffset(_left, _right));
        });
    }
}
