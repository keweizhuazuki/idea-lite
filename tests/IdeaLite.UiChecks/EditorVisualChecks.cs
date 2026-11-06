using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using IdeaLite;
using IdeaLite.Core;

internal static class EditorVisualChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Source = """
        package dev.idealite.shop.common.utils;

        import java.util.List;

        /**
         * Query helpers for the current project.
         * @param <T> document type
         */
        @Service
        public abstract class AbstractRepository<T> {
            private static final int QUERY_LIMIT = 20_000;

            public T findById(String id) {
                Query query = new Query();
                query.addCriteria(Criteria.where(ID_FIELD).is(id));
                return mongoTemplate.findOne(query, entityType);
            }

            public boolean fieldExists(String field, Object value) {
                // Keep field names and values separate from the query text.
                String message = "return /* this is a string */";
                if (StringUtils.isBlank(field)) {
                    throw new IllegalArgumentException("Field name is required");
                }

                Query query = new Query();
                query.addCriteria(Criteria.where(field).is(value));
                return mongoTemplate.exists(query, entityType);
            }

            public List<T> findByQuery(Query query) {
                return mongoTemplate.find(query.limit(QUERY_LIMIT), entityType);
            }
        }
        """;

    public static void Run(Action<string, Action> check, Action pump)
    {
        check("Java palette separates keywords, annotations, declarations, constants, comments and text blocks", () =>
        {
            var editor = new TextEditor(); EditorSupport.Configure(editor); EditorSupport.Highlight(editor, "Example.java"); editor.Text = Source;
            using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
            string ColorAt(string token, int occurrence = 0)
            {
                int offset = -1;
                for (int index = 0; index <= occurrence; index++) offset = editor.Text.IndexOf(token, offset + 1, StringComparison.Ordinal);
                Require(offset >= 0, "Missing fixture token " + token);
                var line = editor.Document.GetLineByOffset(offset);
                var section = highlighter.HighlightLine(line.LineNumber).Sections.LastOrDefault(section => section.Offset <= offset && section.Offset + section.Length > offset);
                return (section?.Color.Foreground?.GetColor(null) ?? ((SolidColorBrush)editor.Foreground).Color).ToString();
            }
            Require(ColorAt("public") == "#FFCF8E6D", "Keywords lost their warm palette");
            Require(ColorAt("@Service") == "#FFB3AE60", "Annotations were not distinguished");
            Require(ColorAt("findById") == "#FFFFC66D", "Method declarations were not distinguished");
            Require(ColorAt("QUERY_LIMIT") == "#FF9876AA", "Constants were not distinguished");
            Require(ColorAt("20_000") == "#FF2AACB8", "Numbers were not distinguished");
            Require(ColorAt("Keep field names") == "#FF7A7E85", "Comment text changed");
            Require(ColorAt("return /* this") == "#FF6AAB73", "Keywords or comments leaked into a string");
            Require(ColorAt("mongoTemplate") == "#FFBCBEC4", "Ordinary identifiers were all tinted blue");
            editor.Text = "String text = \"\"\"\nreturn /* literal text */\n\"\"\";\nreturn text;";
            using var blocks = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
            Require(blocks.HighlightLine(2).Sections.Any(section => section.Color.Foreground?.GetColor(null)?.ToString() == "#FF6AAB73"), "Java text blocks lost multiline string state");
        });
        check("Editor spacing renders the bundled font with unchanged document coordinates", () =>
        {
            var editor = new TextEditor(); EditorSupport.Configure(editor, false); EditorSupport.Highlight(editor, "Example.java"); editor.Text = Source;
            var owner = new Window { Content = editor, Width = 900, Height = 500 }; owner.Show(); pump(); editor.UpdateLayout();
            try
            {
                var view = editor.TextArea.TextView; view.EnsureVisualLines();
                Require(view.DefaultLineHeight >= editor.FontSize * 1.5, "The code line spacing is still compressed");
                var glyphs = view.VisualLines.First().TextLines.SelectMany(line => line.GetIndexedGlyphRuns()).ToArray();
                var fontDirectory = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts") + Path.DirectorySeparatorChar).AbsoluteUri;
                Require(glyphs.Any(run => run.GlyphRun.GlyphTypeface.FontUri.ToString().StartsWith(fontDirectory, StringComparison.OrdinalIgnoreCase) && run.GlyphRun.GlyphTypeface.FamilyNames.Values.Contains("JetBrains Mono")), "Displayed code fell back from the bundled font: " + string.Join(", ", glyphs.Select(run => run.GlyphRun.GlyphTypeface.FontUri.ToString())));
                int offset = editor.Text.IndexOf("return mongoTemplate", StringComparison.Ordinal);
                editor.CaretOffset = offset; Require(editor.Document.GetOffset(editor.TextArea.Caret.Location) == offset, "Spacing changed caret offsets");
                Require(editor.Text == Source && view.BackgroundRenderers.OfType<IndentGuides>().Single().Enabled, "Indent guides changed source or were not enabled");
            }
            finally { owner.Close(); }
        });
        check("Project hierarchy compacts package chains, marks source roots and preserves selection on refresh", () =>
        {
            var fixture = CreateFixture(); var owner = new MainWindow(null); owner.Show();
            try
            {
                Wait((Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", Flags)!.Invoke(owner, [fixture.Project])!, pump);
                var tree = (TreeView)owner.FindName("FileTree"); var root = (TreeViewItem)tree.Items[0];
                Require(Header(root).Contains("[spring-workspace-parent]"), "Root Maven artifact label missing");
                Require(PathOf((TreeViewItem)root.Items[0]) == "common.module", "Directories were not sorted before files");
                var module = Child(root, "common.module"); module.IsExpanded = true;
                pump();
                var arrow = (FrameworkElement)module.Template.FindName("Expander", module);
                double gap = ((FrameworkElement)module.Header).TransformToVisual(arrow).Transform(new Point()).X;
                Require(gap >= 16 && gap <= 28, "Project icon is detached from its disclosure arrow: " + gap);
                Require(((StackPanel)module.Header).Children.OfType<IdeIcon>().Single().Kind == "Module", "Maven module icon missing");
                var src = Child(module, "common.module/src"); src.IsExpanded = true;
                var main = Child(src, "common.module/src/main"); main.IsExpanded = true;
                var java = Child(main, "common.module/src/main/java"); java.IsExpanded = true;
                Require(((StackPanel)java.Header).Children.OfType<IdeIcon>().Single().Kind == "SourceRoot", "Java source root looked like an ordinary folder");
                var package = Child(java, "common.module/src/main/java/dev/idealite/shop/common"); package.IsExpanded = true;
                Require(Header(package) == "dev.idealite.shop.common", "Package chain was not compacted");
                Wait((Task)typeof(MainWindow).GetMethod("OpenFileAsync", Flags)!.Invoke(owner, [fixture.SourcePath, null, true])!, pump);
                typeof(MainWindow).GetMethod("SelectCurrentFile", Flags)!.Invoke(owner, null); pump();
                Require(PathOf((TreeViewItem)tree.SelectedItem) == fixture.SourcePath, "Navigate to source selected the wrong node");
                typeof(MainWindow).GetMethod("RenderProjectTree", Flags)!.Invoke(owner, null); pump();
                Require(tree.SelectedItem is TreeViewItem selected && PathOf(selected) == fixture.SourcePath, "Refresh lost tree selection");
            }
            finally { owner.Close(); RemoveFixture(fixture.Root); }
        });
        check("Tooltips use a dark surface and project terminals target Git Bash with a literal directory", () =>
        {
            var owner = new Window { Width = 240, Height = 140 }; var button = new Button { Content = "Terminal" }; owner.Content = button; owner.Show(); pump();
            var tip = new ToolTip { Content = "Open Project Terminal · Alt+F12", PlacementTarget = button };
            try
            {
                tip.IsOpen = true; pump();
                Require(((SolidColorBrush)tip.Background).Color.ToString() == "#FF2B2D30" && ((SolidColorBrush)tip.Foreground).Color.ToString() == "#FFDFE1E5", "Tooltip foreground/background contrast is wrong");
                const string root = @"D:\dev\code\space & quote' 项目";
                var info = (System.Diagnostics.ProcessStartInfo)typeof(MainWindow).GetMethod("ProjectTerminalStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
                Require(Path.GetFileName(info.FileName).Equals("git-bash.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(info.FileName), "Terminal is not Git Bash");
                Require(info.WorkingDirectory == root && info.ArgumentList.SequenceEqual(new[] { "--cd=" + root }), "Terminal directory was converted into a shell script");
            }
            finally { tip.IsOpen = false; owner.Close(); }
        });
    }

    public static void Render(string destination, Action pump)
    {
        var fixture = CreateFixture(); var owner = new MainWindow(null); owner.Show();
        try
        {
            Wait((Task)typeof(MainWindow).GetMethod("OpenWorkspaceAsync", Flags)!.Invoke(owner, [fixture.Project])!, pump);
            Wait((Task)typeof(MainWindow).GetMethod("OpenFileAsync", Flags)!.Invoke(owner, [fixture.SourcePath, null, true])!, pump);
            typeof(MainWindow).GetMethod("SelectCurrentFile", Flags)!.Invoke(owner, null); pump();
            var layout = (WorkbenchLayout)typeof(MainWindow).GetField("_layout", Flags)!.GetValue(owner)!; layout.LeftWidth = 360;
            typeof(MainWindow).GetMethod("ApplyLayout", Flags)!.Invoke(owner, null);
            var host = (FrameworkElement)owner.Content;
            host.Measure(new Size(1380, 860)); host.Arrange(new Rect(0, 0, 1380, 860)); host.UpdateLayout(); pump();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1380, 860, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(destination); encoder.Save(output); Console.WriteLine("Rendered code and project hierarchy: " + destination);
        }
        finally { owner.Close(); RemoveFixture(fixture.Root); }
    }

    private static (string Root, string Project, string SourcePath) CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "idea-lite-editor-" + Guid.NewGuid().ToString("N")); string project = Path.Combine(root, "spring-workspace"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "pom.xml"), "<project><modelVersion>4.0.0</modelVersion><groupId>dev.idea</groupId><artifactId>spring-workspace-parent</artifactId><version>1</version><packaging>pom</packaging><modules><module>common.module</module></modules></project>");
        const string source = "common.module/src/main/java/dev/idealite/shop/common/utils/AbstractRepository.java";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(project, source))!); File.WriteAllText(Path.Combine(project, source), Source);
        var config = Path.Combine(project, "common.module/src/main/java/dev/idealite/shop/common/config/QueryConfig.java"); Directory.CreateDirectory(Path.GetDirectoryName(config)!); File.WriteAllText(config, "class QueryConfig {}\n");
        File.WriteAllText(Path.Combine(project, "common.module/pom.xml"), "<project><modelVersion>4.0.0</modelVersion><groupId>dev.idea</groupId><artifactId>shop-common</artifactId><version>1</version></project>");
        File.WriteAllText(Path.Combine(project, "README.md"), "# Editor preview\n"); return (root, project, source);
    }
    private static void RemoveFixture(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static string Header(TreeViewItem item) => ((StackPanel)item.Header).Children.OfType<TextBlock>().Single().Text;
    private static string PathOf(TreeViewItem item) => ((StackPanel)item.Header).ToolTip as string ?? "";
    private static TreeViewItem Child(TreeViewItem parent, string path) => parent.Items.OfType<TreeViewItem>().Single(child => PathOf(child) == path);
    private static void Wait(Task task, Action pump)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 25) { pump(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Editor fixture timed out"); task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
