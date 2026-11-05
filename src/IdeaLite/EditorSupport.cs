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
        editor.Background = new SolidColorBrush(Color.FromRgb(30, 31, 34));
        editor.Foreground = new SolidColorBrush(Color.FromRgb(188, 190, 196));
        editor.FontFamily = new FontFamily("JetBrains Mono, Consolas"); editor.FontSize = 14;
        editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(89, 100, 119));
        editor.ShowLineNumbers = true; editor.IsReadOnly = readOnly;
        editor.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        editor.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        editor.Padding = new Thickness(8, 6, 8, 6);
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = !readOnly;
        editor.Options.AllowScrollBelowDocument = true;
        editor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(218, 220, 224));
        editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(185, 48, 73, 110));
        editor.TextArea.IndentationStrategy = new BraceIndentation(editor);
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
                var hex = name.Contains("comment") ? "#7A7E85" : name.Contains("string") || name.Contains("char") ? "#6AAB73" :
                    name.Contains("number") || name.Contains("digit") ? "#2AACB8" : name.Contains("keyword") || name.Contains("modifier") || name.Contains("type") ? "#CF8E6D" : "#A7C4E4";
                color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            editor.SyntaxHighlighting = definition;
        }
        else editor.SyntaxHighlighting = null;
    }

    public static void SetDiffText(TextEditor editor, IReadOnlyList<DiffPiece> lines, string path, bool oldSide)
    {
        editor.Text = string.Join('\n', lines.Select(l => l.Text ?? ""));
        Highlight(editor, path);
        SetLineColors(editor, lines, oldSide);
        editor.ShowLineNumbers = false;
        editor.Options.AllowScrollBelowDocument = false;
        var margin = editor.TextArea.LeftMargins.OfType<DiffNumberMargin>().FirstOrDefault();
        if (margin == null) { margin = new DiffNumberMargin(); editor.TextArea.LeftMargins.Insert(0, margin); }
        margin.Numbers = lines.Select(l => l.Position).ToArray();
        margin.InvalidateVisual();
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
            var hex = changes[number] == ChangeType.Imaginary ? "#25272A" : changes[number] == ChangeType.Modified ? "#293A50" : oldSide ? "#492D35" : "#294436";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, visual.FirstDocumentLine))
                context.DrawRectangle(brush, null, new Rect(0, rect.Top, Math.Max(view.ActualWidth, 0), rect.Height));
        }
    }
}

public sealed class BraceIndentation(TextEditor editor) : DefaultIndentationStrategy
{
    public override void IndentLine(TextDocument document, DocumentLine line)
    {
        base.IndentLine(document, line);
        if (line.PreviousLine == null) return;
        var previous = document.GetText(line.PreviousLine).TrimEnd();
        if (!previous.EndsWith('{')) return;
        var indentation = !editor.Options.ConvertTabsToSpaces ? "\t" : new string(' ', editor.Options.IndentationSize);
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
