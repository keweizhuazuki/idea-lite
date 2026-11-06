using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdeaLite;

/// <summary>
/// 仅在可见代码行后绘制缩进线，不改动文档、光标或字符布局。
/// </summary>
public sealed class IndentGuides(TextEditor editor) : IBackgroundRenderer
{
    private static readonly Pen Guide = CreatePen("#2F3237");
    private static readonly Pen ActiveGuide = CreatePen("#505967");
    public bool Enabled { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    private static Pen CreatePen(string hex)
    {
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), 1); pen.Freeze(); return pen;
    }
    internal static int Columns(string text, int tabSize)
    {
        int columns = 0;
        foreach (char character in text)
        {
            if (character == ' ') columns++;
            else if (character == '\t') columns += tabSize - columns % tabSize;
            else break;
        }
        return columns;
    }
    public void Draw(TextView view, DrawingContext drawing)
    {
        if (!Enabled || !view.VisualLinesValid || view.Document == null) return;
        var document = view.Document;
        int size = Math.Max(1, editor.Options.IndentationSize);
        int Indent(DocumentLine line) => Columns(document.GetText(line), size);
        var current = document.GetLineByOffset(Math.Min(editor.CaretOffset, document.TextLength));
        int activeIndent = Indent(current) / size * size;
        int activeColumn = activeIndent - size;
        int first = current.LineNumber, last = current.LineNumber;
        if (activeIndent > 0)
        {
            for (var line = current.PreviousLine; line != null && first > current.LineNumber - 300; line = line.PreviousLine)
            {
                if (line.Length > 0 && !string.IsNullOrWhiteSpace(document.GetText(line)) && Indent(line) < activeIndent) break;
                first = line.LineNumber;
            }
            for (var line = current.NextLine; line != null && last < current.LineNumber + 300; line = line.NextLine)
            {
                if (line.Length > 0 && !string.IsNullOrWhiteSpace(document.GetText(line)) && Indent(line) < activeIndent) break;
                last = line.LineNumber;
            }
        }
        foreach (var visual in view.VisualLines)
        {
            var line = visual.FirstDocumentLine;
            int indent = Indent(line);
            if (string.IsNullOrWhiteSpace(document.GetText(line)))
            {
                int previous = 0, next = 0;
                var neighbor = line.PreviousLine;
                for (int scan = 0; neighbor != null && scan < 50; scan++, neighbor = neighbor.PreviousLine)
                    if (!string.IsNullOrWhiteSpace(document.GetText(neighbor))) { previous = Indent(neighbor); break; }
                neighbor = line.NextLine;
                for (int scan = 0; neighbor != null && scan < 50; scan++, neighbor = neighbor.NextLine)
                    if (!string.IsNullOrWhiteSpace(document.GetText(neighbor))) { next = Indent(neighbor); break; }
                indent = Math.Min(previous, next);
            }
            double y = visual.VisualTop - view.VerticalOffset;
            for (int column = 0; column < indent; column += size)
            {
                double x = Math.Round(column * view.WideSpaceWidth - view.HorizontalOffset) + .5;
                if (x < 0 || x > view.ActualWidth) continue;
                bool active = column == activeColumn && line.LineNumber >= first && line.LineNumber <= last;
                drawing.DrawLine(active ? ActiveGuide : Guide, new Point(x, y), new Point(x, y + visual.Height));
            }
        }
    }
}
