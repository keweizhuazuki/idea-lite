using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private readonly LineChangeMargin _lineChanges = new();
    private readonly DispatcherTimer _lineChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _baselineLoad;
    private DocumentState? _baselineOwner;
    private string? _baselineText;
    private int _lineChangeGeneration;
    private readonly Dictionary<TextEditor, DefinitionLink> _definitionLinks = [];
    private readonly TypeUsageHints _editorUsages = new(), _diffUsages = new();

    private void InitializeEditorChanges()
    {
        Editor.TextArea.LeftMargins.Add(_lineChanges);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_editorUsages);
        NewEditor.TextArea.TextView.BackgroundRenderers.Add(_diffUsages);
        foreach (var editor in new[] { Editor, OldEditor, NewEditor })
        {
            var link = new DefinitionLink();
            var hints = editor == Editor ? _editorUsages : editor == NewEditor ? _diffUsages : null;
            var view = editor.TextArea.TextView;
            SearchHit? pressedUsage = null;
            _definitionLinks.Add(editor, link);
            editor.TextArea.TextView.LineTransformers.Add(link);
            editor.MouseMove += (_, e) => UpdateDefinitionHover(editor, e.GetPosition(editor), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            editor.MouseLeave += (_, _) =>
            {
                pressedUsage = null;
                SetDefinitionHover(editor, 0, 0, false);
            };
            editor.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
            {
                if (link.Length > 0 || hints?.HitTest(view, Mouse.GetPosition(view)) != null)
                { e.Cursor = Cursors.Hand; e.Handled = true; }
            }), true);
            editor.PreviewMouseLeftButtonDown += async (_, e) =>
            {
                pressedUsage = Keyboard.Modifiers == ModifierKeys.None ? hints?.HitTest(view, e.GetPosition(view)) : null;
                if (pressedUsage != null)
                {
                    e.Handled = true;
                    return;
                }
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
                UpdateDefinitionHover(editor, e.GetPosition(editor), true);
                if (link.Length == 0) return;
                e.Handled = true;
                await Guard(() => NavigateDefinitionAsync(editor, link.Offset));
            };
            editor.PreviewMouseLeftButtonUp += async (_, e) =>
            {
                var type = pressedUsage;
                pressedUsage = null;
                // Open after the click finishes so the new window cannot receive the hint's mouse release.
                if (type == null || Keyboard.Modifiers != ModifierKeys.None || hints?.HitTest(view, e.GetPosition(view)) != type) return;
                e.Handled = true;
                await Guard(() => ShowTypeUsagesAsync(type));
            };
        }
        _lineChangeTimer.Tick += async (_, _) => { _lineChangeTimer.Stop(); await Guard(UpdateLineChangesAsync); };
        OldOverview.Attach(OldEditor, _diffScroll.RevealLine);
        NewOverview.Attach(NewEditor, line => _diffScroll.RevealLine(_diffScroll.AlignedLine(line)));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
            foreach (var editor in _definitionLinks.Keys.Where(editor => editor.IsMouseOver))
                UpdateDefinitionHover(editor, Mouse.GetPosition(editor), true);
        };
        PreviewKeyUp += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl) ClearDefinitionHover(); };
        Deactivated += (_, _) => ClearDefinitionHover();
    }
    private void QueueLineChanges()
    {
        if (_loadingDocument) return;
        _lineChangeGeneration++; _lineChangeTimer.Stop(); _lineChangeTimer.Start();
        ClearDefinitionHover();
    }
    private void ResetLineChanges()
    {
        _baselineLoad?.Cancel(); _baselineOwner = null; _baselineText = null; _lineChangeGeneration++;
        _lineChangeTimer.Stop(); _lineChanges.Changes = []; _lineChanges.InvalidateVisual(); ClearDefinitionHover();
    }
    private async Task RefreshEditorBaselineAsync()
    {
        _baselineLoad?.Cancel(); _baselineLoad?.Dispose(); _baselineLoad = new();
        var token = _baselineLoad.Token; var document = _active; var git = _git;
        if (document == null) { ResetLineChanges(); return; }
        string relative = Relative(document.File.Path);
        var original = _gitStatus?.Changes.FirstOrDefault(change => change.Path == relative)?.OriginalPath ?? relative;
        try
        {
            string text = git == null ? document.File.Text : await git.HeadTextAsync(original, token);
            token.ThrowIfCancellationRequested();
            if (_active != document || _git != git) return;
            _baselineOwner = document; _baselineText = text;
            await UpdateLineChangesAsync();
        }
        catch (OperationCanceledException) { }
    }
    private async Task UpdateLineChangesAsync()
    {
        var document = _active;
        if (document == null) return;
        if (_baselineOwner != document || _baselineText == null) { await RefreshEditorBaselineAsync(); return; }
        string baseline = _baselineText, current = document.Document.Text;
        int generation = ++_lineChangeGeneration;
        // 与差异视图保持相同的有界计算，避免大文件编辑时持续占用 UI 线程。
        var changes = baseline.Length + current.Length > 1_000_000 || baseline.Count(c => c == '\n') + current.Count(c => c == '\n') > 40_000
            ? [] : await Task.Run(() => LineChanges.Compare(baseline, current));
        if (generation != _lineChangeGeneration || _active != document || document.Document.Text != current) return;
        _lineChanges.Changes = changes; _lineChanges.InvalidateVisual();
    }

    internal void UpdateDefinitionHover(TextEditor editor, Point point, bool control)
    {
        var link = _definitionLinks[editor];
        if (!control || _workspace == null || CodePath(editor) is not { } path || editor.GetPositionFromPoint(point) is not { } position)
        {
            SetDefinitionHover(editor, 0, 0, false);
            return;
        }
        var (start, length) = IdentifierAt(editor.Document, editor.Document.GetOffset(position.Location), atCaret: false);
        if (link.Candidate == (start, length)) return;
        bool found = false;
        if (length > 0)
        {
            var definitions = SourceDefinitions(_workspace, editor.Document.GetText(start, length), path, editor.Text, out var importedType);
            found = definitions.Count > 0 || importedType != null && MavenSources.ModuleFor(_workspace, path)?.Dependencies.Count > 0;
        }
        SetDefinitionHover(editor, start, length, found);
    }
    private static (int Start, int Length) IdentifierAt(TextDocument document, int offset, bool atCaret)
    {
        static bool Identifier(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';
        int start = offset, end = offset;
        if (!atCaret && (offset >= document.TextLength || !Identifier(document.GetCharAt(offset)))) return (0, 0);
        while (start > 0 && Identifier(document.GetCharAt(start - 1))) start--;
        while (end < document.TextLength && Identifier(document.GetCharAt(end))) end++;
        return (start, end - start);
    }
    private void SetDefinitionHover(TextEditor editor, int start, int length, bool found)
    {
        var link = _definitionLinks[editor];
        link.Candidate = (start, length);
        int oldStart = link.Offset, oldLength = link.Length;
        if (found && oldStart == start && oldLength == length || !found && oldLength == 0) return;
        link.Offset = start; link.Length = found ? length : 0;
        var view = editor.TextArea.TextView;
        if (oldLength > 0) view.Redraw(oldStart, oldLength);
        if (found) view.Redraw(start, length);
        Mouse.UpdateCursor();
    }
    private void ClearDefinitionHover()
    {
        foreach (var editor in _definitionLinks.Keys) SetDefinitionHover(editor, 0, 0, false);
    }

    private void UpdateTypeUsages()
    {
        _editorUsages.Set(Editor, _workspace, _active == null ? null : Relative(_active.File.Path));
        _diffUsages.Set(NewEditor, _workspace, _diffDocument == null ? null : Relative(_diffDocument.File.Path));
    }

    private async Task ShowTypeUsagesAsync(SearchHit type)
    {
        var workspace = _workspace;
        if (workspace == null || !workspace.JavaTypeUsages.TryGetValue(type, out var references)) return;
        var choices = references.Select(reference => new SearchHit(Path.GetFileName(reference.Path),
            reference.Path, reference.Line, "用法", reference.Path) { Column = reference.Column }).ToArray();
        var dialog = new SearchWindow(this, workspace, "usages", choices: choices,
            choiceTitle: UiText.Current.Format($"{type.Name} 的用法"));
        if (await dialog.ShowSearchAsync() && dialog.Result is { } hit && _workspace?.Root == workspace.Root)
            await OpenLocationAsync(hit.Path, hit.Line, hit.Column);
    }

    private static IReadOnlyList<SearchHit> SourceDefinitions(WorkspaceSnapshot workspace, string name, string path, string text, out string? importedType)
    {
        var definitions = WorkspaceService.FindDefinitions(workspace, name, path, text);
        var qualified = Path.GetExtension(path) == ".java" ? JavaAnalyzer.ExplicitTypeImport(text, name) : null;
        importedType = qualified;
        return qualified == null ? definitions : definitions.Where(hit => hit.Kind == "类型" && hit.Detail == qualified).ToArray();
    }

    private async Task OpenMavenSourceAsync(WorkspaceSnapshot workspace, MavenModule module, string qualifiedType, string name)
    {
        var configured = _layout.MavenRepository.Trim();
        if (configured.StartsWith("/mnt/", StringComparison.Ordinal) && configured.Length >= 7 &&
            char.IsAsciiLetter(configured[5]) && configured[6] == '/')
            configured = configured[5] + ":" + configured[6..].Replace('/', '\\');
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".m2", "repository");
        var repositories = Path.IsPathFullyQualified(configured) ? new[] { configured, standard } : [standard];
        var source = await MavenSources.FindAsync(module, qualifiedType, repositories.Distinct(StringComparer.OrdinalIgnoreCase),
            _workspaceLoad?.Token ?? default);
        if (_workspace?.Root != workspace.Root) return;
        if (source == null)
        {
            SetStatus($"本地 Maven 仓库中没有找到 {qualifiedType} 的依赖源码。");
            return;
        }
        var viewer = new TextEditor { Text = source.Text };
        EditorSupport.Configure(viewer);
        EditorSupport.Highlight(viewer, source.Entry);
        var window = new Window
        {
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Width = 850, Height = 550, Title = $"{qualifiedType} — {source.Dependency.Artifact}:{source.Dependency.Version}", Content = viewer
        };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        window.Show();
        var declaration = JavaAnalyzer.DeclaredTypes(source.Text).FirstOrDefault(type => type.Name == name);
        viewer.CaretOffset = declaration.Offset;
        viewer.ScrollToLine(viewer.TextArea.Caret.Line);
        viewer.TextArea.Focus();
    }

    internal static ProcessStartInfo ProjectInstanceStartInfo(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string assembly = typeof(MainWindow).Assembly.Location, executable = Path.ChangeExtension(assembly, ".exe");
        var info = new ProcessStartInfo(File.Exists(executable) ? executable : "dotnet") { WorkingDirectory = root, UseShellExecute = false };
        if (!File.Exists(executable)) info.ArgumentList.Add(assembly);
        info.ArgumentList.Add(root); return info;
    }
    private Task OpenProjectInstanceAsync(string root)
    {
        using var process = Process.Start(ProjectInstanceStartInfo(root));
        return Task.CompletedTask;
    }
}

internal sealed class DefinitionLink : DocumentColorizingTransformer
{
    public (int Start, int Length) Candidate { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; }
    protected override void ColorizeLine(DocumentLine line)
    {
        int first = Math.Max(Offset, line.Offset), last = Math.Min(Offset + Length, line.EndOffset);
        if (Length == 0 || first >= last) return;
        ChangeLinePart(first, last, element =>
        {
            element.TextRunProperties.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(112, 161, 255)));
            element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
        });
    }
}

internal sealed class TypeUsageHints : IBackgroundRenderer
{
    private static readonly Typeface HintFont = new("Segoe UI");
    private static readonly Brush HintColor = new SolidColorBrush(Color.FromRgb(124, 135, 150));
    private (TextAnchor Anchor, SearchHit Type, int Count)[] _hints = [];
    public KnownLayer Layer => KnownLayer.Background;

    public void Set(TextEditor editor, WorkspaceSnapshot? workspace, string? path)
    {
        _hints = [];
        if (workspace != null && Path.GetExtension(path) == ".java" && editor.Document.TextLength <= 512 * 1024)
        {
            var types = workspace.Symbols.Where(symbol => symbol.Path == path && symbol.Kind == "类型")
                .GroupBy(symbol => symbol.Name).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
            _hints = JavaAnalyzer.DeclaredTypes(editor.Text).Where(type => types.ContainsKey(type.Name))
                .Select(type =>
                {
                    var anchor = editor.Document.CreateAnchor(type.Offset);
                    anchor.MovementType = AnchorMovementType.AfterInsertion;
                    var symbol = types[type.Name];
                    return (anchor, symbol, workspace.JavaTypeUsages.GetValueOrDefault(symbol)?.Count ?? 0);
                }).ToArray();
        }
        editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView view, DrawingContext drawing)
    {
        foreach (var (_, _, bounds, text) in Layout(view)) drawing.DrawText(text, bounds.TopLeft);
    }

    internal SearchHit? HitTest(TextView view, Point point)
    {
        if (!new Rect(view.RenderSize).Contains(point)) return null;
        return Layout(view).FirstOrDefault(item => item.Count > 0 && item.Bounds.Contains(point)).Type;
    }

    private IEnumerable<(SearchHit Type, int Count, Rect Bounds, FormattedText Text)> Layout(TextView view)
    {
        if (_hints.Length == 0 || !view.VisualLinesValid || view.VisualLines.Count == 0) yield break;
        int first = view.VisualLines[0].FirstDocumentLine.Offset, last = view.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var (anchor, type, count) in _hints)
        {
            var name = type.Name;
            if (anchor.IsDeleted || anchor.Offset < first || anchor.Offset + name.Length > last ||
                view.Document.GetText(anchor.Offset, name.Length) != name) continue;
            var line = view.Document.GetLineByOffset(anchor.Offset);
            var position = view.GetVisualPosition(new TextViewPosition(line.LineNumber, line.Length + 1), VisualYPosition.LineMiddle);
            double x = position.X - view.HorizontalOffset + 13;
            if (x < 0 || x >= view.ActualWidth) continue;
            string label = count == 1 ? UiText.Current.Translate("1 处用法") : UiText.Current.Format($"{count} 处用法");
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HintFont, 11,
                HintColor, VisualTreeHelper.GetDpi(view).PixelsPerDip);
            yield return (type, count, new Rect(x, position.Y - view.VerticalOffset - text.Height / 2,
                text.WidthIncludingTrailingWhitespace, text.Height), text);
        }
    }
}
