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

    public DiffScrollSynchronizer(TextEditor left, TextEditor right)
    {
        _left = left; _right = right;
        foreach (var editor in new[] { left, right })
        {
            editor.PreviewMouseWheel += (_, _) => Drive(editor);
            editor.PreviewMouseLeftButtonDown += (_, _) => Drive(editor);
            editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Drive(editor);
            };
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Queue(editor);
        }
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
            follower.ScrollToVerticalOffset(_lastOffset);
        });
    }

    public void RevealLine(int line)
    {
        Reset();
        long generation = _generation;
        _left.ScrollToLine(line);
        _right.ScrollToLine(line);
        _left.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation == _generation) _right.ScrollToVerticalOffset(_left.VerticalOffset);
        });
    }
}
