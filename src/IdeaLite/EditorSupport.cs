using System.IO;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using DiffPlex.DiffBuilder.Model;

namespace IdeaLite;

public static class EditorSupport
{
    public static void Configure(TextEditor editor, bool readOnly = true)
    {
        editor.Background = new SolidColorBrush(Color.FromRgb(17, 19, 24));
        editor.Foreground = new SolidColorBrush(Color.FromRgb(217, 222, 232));
        editor.FontFamily = new FontFamily("Cascadia Code, Consolas"); editor.FontSize = 14;
        editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(89, 100, 119));
        editor.ShowLineNumbers = true; editor.IsReadOnly = readOnly;
        editor.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        editor.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        editor.Padding = new Thickness(8, 6, 8, 6);
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = !readOnly;
        editor.Options.AllowScrollBelowDocument = true;
        editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(125, 226, 178));
        editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(150, 64, 91, 114));
        editor.TextArea.IndentationStrategy = new BraceIndentation();
        var search = SearchPanel.Install(editor);
        search.MarkerBrush = new SolidColorBrush(Color.FromArgb(120, 196, 151, 53));
        search.Resources[SystemColors.WindowBrushKey] = editor.Background;
        search.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(Color.FromRgb(66, 78, 96));
        search.Resources[SystemColors.ControlTextBrushKey] = editor.Foreground;
        var searchButton = new Style(typeof(System.Windows.Controls.Button), (Style)Application.Current.FindResource(typeof(System.Windows.Controls.Button)));
        searchButton.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(1)));
        search.Resources[typeof(System.Windows.Controls.Button)] = searchButton;
        var searchInput = new Style(typeof(System.Windows.Controls.TextBox), (Style)Application.Current.FindResource(typeof(System.Windows.Controls.TextBox)));
        searchInput.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(3, 1, 3, 1)));
        search.Resources[typeof(System.Windows.Controls.TextBox)] = searchInput;
    }

    public static void Highlight(TextEditor editor, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var builtin = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (builtin != null)
        {
            var definition = builtin;
            foreach (var color in definition.NamedHighlightingColors)
            {
                var name = color.Name.ToLowerInvariant();
                var hex = name.Contains("comment") ? "#748779" : name.Contains("string") || name.Contains("char") ? "#C3DEA0" :
                    name.Contains("number") || name.Contains("digit") ? "#E6BA82" : name.Contains("keyword") || name.Contains("modifier") || name.Contains("type") ? "#B7A2EA" : "#8DCBE8";
                color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            editor.SyntaxHighlighting = definition;
        }
        else editor.SyntaxHighlighting = null;
    }

    public static void SetLineColors(TextEditor editor, IReadOnlyList<DiffPiece> lines, bool oldSide)
    {
        foreach (var renderer in editor.TextArea.TextView.BackgroundRenderers.OfType<DiffBackground>().ToArray()) editor.TextArea.TextView.BackgroundRenderers.Remove(renderer);
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(lines.Select(l => l.Type).ToArray(), oldSide));
        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }
}

public sealed class DiffBackground(ChangeType[] changes, bool oldSide) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;
    public void Draw(TextView view, DrawingContext context)
    {
        if (!view.VisualLinesValid) return;
        foreach (var visual in view.VisualLines)
        {
            var number = visual.FirstDocumentLine.LineNumber - 1;
            if (number >= changes.Length || changes[number] == ChangeType.Unchanged) continue;
            var hex = changes[number] == ChangeType.Imaginary ? "#1B1F27" : oldSide ? "#40292F" : "#203B31";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, visual.FirstDocumentLine))
                context.DrawRectangle(brush, null, new Rect(0, rect.Top, Math.Max(view.ActualWidth, 0), rect.Height));
        }
    }
}

public sealed class BraceIndentation : DefaultIndentationStrategy
{
    public override void IndentLine(TextDocument document, DocumentLine line)
    {
        base.IndentLine(document, line);
        if (line.PreviousLine == null) return;
        var previous = document.GetText(line.PreviousLine).TrimEnd();
        if (!previous.EndsWith('{')) return;
        var editor = MainWindow.ActiveEditor;
        var indentation = editor?.Options.ConvertTabsToSpaces == false ? "\t" : new string(' ', editor?.Options.IndentationSize ?? 4);
        document.Insert(line.Offset, indentation);
    }
}

public sealed class PatchColorizer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        var color = text.StartsWith('+') ? "#7DE2B2" : text.StartsWith('-') ? "#E5A1AD" : text.StartsWith("@@") ? "#BEA6EF" : null;
        if (color != null) ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))));
    }
}
