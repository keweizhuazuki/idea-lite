using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;

namespace IdeaLite;

public sealed class DiffNumberMargin : AbstractMargin
{
    public int?[] Numbers { get; set; } = [];
    protected override void OnTextViewChanged(ICSharpCode.AvalonEdit.Rendering.TextView oldView, ICSharpCode.AvalonEdit.Rendering.TextView newView)
    {
        if (oldView != null) oldView.VisualLinesChanged -= Repaint;
        base.OnTextViewChanged(oldView, newView);
        if (newView != null) newView.VisualLinesChanged += Repaint;
    }
    private void Repaint(object? sender, EventArgs e) => InvalidateVisual();
    protected override Size MeasureOverride(Size availableSize) => new(38, 0);
    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid) return;
        var font = new Typeface("Consolas");
        foreach (var line in view.VisualLines)
        {
            int index = line.FirstDocumentLine.LineNumber - 1;
            if (index >= Numbers.Length || Numbers[index] is not int number) continue;
            var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, font, 12, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new(Math.Max(0, ActualWidth - text.Width - 6), line.VisualTop - view.VerticalOffset));
        }
    }
}
