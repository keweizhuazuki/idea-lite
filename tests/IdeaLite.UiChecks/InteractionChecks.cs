using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using IdeaLite;
using IdeaLite.Core;

internal static class InteractionChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Action _pump = null!;
    public static void Run(Action<string, Action> check, Action pump)
    {
        _pump = pump;
        check("Scrollbar tracks stay eight pixels wide in editor and list templates", () =>
        {
            var editor = new TextEditor(); EditorSupport.Configure(editor); editor.Text = string.Join('\n', Enumerable.Repeat(new string('x', 300), 300));
            var list = new ListBox { ItemsSource = Enumerable.Range(1, 200).ToArray(), Width = 140 };
            var panel = new DockPanel(); DockPanel.SetDock(list, Dock.Right); panel.Children.Add(list); panel.Children.Add(editor);
            var owner = new Window { Width = 800, Height = 400, Content = panel }; owner.Show(); pump(); owner.UpdateLayout();
            try
            {
                var bars = Descendants(owner).OfType<ScrollBar>().Where(bar => bar.IsVisible).ToArray();
                Require(bars.Length >= 3, "Fixture did not show both orientations and a list scrollbar");
                foreach (var bar in bars) Require((bar.Orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight) <= 8.1, "A default template overrode the narrow scrollbar: " + bar.RenderSize);
            }
            finally { owner.Close(); }
        });
        check("Diff overview renders every change type and navigates both aligned panes", () =>
        {
            var before = Enumerable.Range(1, 400).Select(line => "line " + line).ToList(); var after = before.ToList();
            after[30] = "modified"; after.RemoveAt(160); after.Insert(310, "inserted");
            var model = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(string.Join('\n', before), string.Join('\n', after));
            var left = new TextEditor(); var right = new TextEditor(); EditorSupport.Configure(left); EditorSupport.Configure(right);
            var overview = new DiffOverview(); var sync = new DiffScrollSynchronizer(left, right); overview.Attach(right, sync.RevealLine);
            var panel = new Grid(); panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
            Grid.SetColumn(right, 1); Grid.SetColumn(overview, 2); panel.Children.Add(left); panel.Children.Add(right); panel.Children.Add(overview);
            EditorSupport.SetDiffText(left, model.OldText.Lines, "Example.java", true); EditorSupport.SetDiffText(right, model.NewText.Lines, "Example.java", false); overview.SetChanges(model.NewText.Lines, false);
            var owner = new Window { Content = panel, Width = 900, Height = 400 }; owner.Show(); pump();
            try
            {
                Require(overview.Changes.Select(change => change.Kind).Distinct().Count() == 3, "Overview omitted inserted, modified or deleted regions");
                typeof(DiffOverview).GetMethod("Navigate", Flags)!.Invoke(overview, [overview.ActualHeight * .79]); pump();
                Require(left.VerticalOffset > 3000 && Math.Abs(left.VerticalOffset - right.VerticalOffset) < 1, "Overview did not navigate and synchronize the panes");
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(overview), null, new Rect(0, 0, 12, 400));
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(12, 400, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var pixels = new byte[12 * 400 * 4]; image.CopyPixels(pixels, 48, 0);
                foreach (var color in new[] { ((SolidColorBrush)LineChanges.Added).Color, ((SolidColorBrush)LineChanges.Modified).Color, ((SolidColorBrush)LineChanges.Deleted).Color })
                    Require(Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4] == color.B && pixels[index * 4 + 1] == color.G && pixels[index * 4 + 2] == color.R), "A change color was not actually painted");
                overview.SetChanges(model.NewText.Lines.Select(line => new DiffPiece(line.Text, ChangeType.Unchanged, line.Position)).ToArray(), false);
                Require(overview.Changes.Count == 0, "Switching to an unchanged diff left stale markers");
            }
            finally { owner.Close(); }
        });
        check("Git diff paints only changed characters in Chinese comments and keeps emoji whole", () => WithRepository((owner, git, root) =>
        {
            const string before = "// 状态：已保存，图标🙂，次数1\n// 检查\nclass Example {}\n";
            const string after = "// 状态：已提交，图标🙃，次数2\n// 已检查\nclass Example {}\n";
            var path = Path.Combine(root, "src", "Example.java");
            File.WriteAllText(path, before);
            Wait(git.StageAsync(["src/Example.java"]));
            File.WriteAllText(path, after);
            Wait(InvokeTask(owner, "RefreshGitAsync"));
            var changes = (ListBox)owner.FindName("ChangesList");
            Until(() => changes.Items.Count == 1);
            changes.SelectedIndex = 0;
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            owner.UpdateLayout(); pump();

            void CheckSpans(string editorName, string expectedText, Color color, params string[] changedText)
            {
                var editor = (TextEditor)owner.FindName(editorName);
                Require(editor.Text == expectedText, "Inline painting altered the displayed source");
                var view = editor.TextArea.TextView;
                view.EnsureVisualLines();
                var renderer = view.BackgroundRenderers.OfType<DiffBackground>().Single();
                var drawing = new DrawingGroup();
                using (var context = drawing.Open()) renderer.Draw(view, context);
                var actual = drawing.Children.OfType<GeometryDrawing>()
                    .Where(item => item.Brush is SolidColorBrush brush && brush.Color == color)
                    .Select(item => item.Geometry.Bounds).ToArray();
                var expected = changedText.SelectMany(text => BackgroundGeometryBuilder.GetRectsForSegment(view,
                    new TextSegment { StartOffset = expectedText.LastIndexOf(text, StringComparison.Ordinal), Length = text.Length })).ToArray();
                Require(actual.SequenceEqual(expected), $"{editorName} highlights {actual.Length} rectangles instead of the exact {expected.Length} changed spans");
            }

            CheckSpans("OldEditor", before, Color.FromRgb(0x62, 0x34, 0x3A), "保存", "🙂", "1");
            CheckSpans("NewEditor", after, Color.FromRgb(0x30, 0x54, 0x40), "提交", "🙃", "2", "已");
            Require(EditorSupport.BuildDiff("  value", "value", true).NewText.Lines.Single().Type == ChangeType.Unchanged,
                "Character comparison changed the ignore whitespace option");
        }));
        check("Working-tree diff edits share the source buffer, save without padding, and leave staged diffs read-only", () => WithRepository((owner, git, root) =>
        {
            const string relative = "src/Example.java";
            var path = Path.Combine(root, relative);
            var baseline = File.ReadAllText(path);
            var working = baseline.Replace("    int value1 = 1;\n", "", StringComparison.Ordinal);
            File.WriteAllText(path, working);
            Wait(InvokeTask(owner, "OpenFileAsync", relative, null, true));
            var editor = (TextEditor)owner.FindName("Editor");
            var old = (TextEditor)owner.FindName("OldEditor");
            var right = (TextEditor)owner.FindName("NewEditor");
            var changes = (ListBox)owner.FindName("ChangesList");

            Wait(InvokeTask(owner, "RefreshGitAsync"));
            changes.SelectedIndex = 0;
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            Require(old.IsReadOnly && !right.IsReadOnly && ReferenceEquals(editor.Document, right.Document),
                "The working-tree preview is not editing the same source buffer");
            Require(right.Text == working && old.Text == baseline, "The editable pane contains alignment padding");
            bool Shortcut(TextEditor target, Key key, ModifierKeys mods) =>
                (bool)typeof(MainWindow).GetMethod("HandleCodeShortcut", Flags)!.Invoke(owner, [target, key, mods])!;
            Require(!Shortcut(old, Key.D, ModifierKeys.Control) && old.Text == baseline, "Duplicate line modified the read-only pane");
            right.CaretOffset = right.Document.GetLineByNumber(2).Offset;
            Require(Shortcut(right, Key.D, ModifierKeys.Control) && right.Document.LineCount == editor.Document.LineCount &&
                right.Text.Contains("    int value2 = 2;\n    int value2 = 2;"), "Duplicate line did not target the Diff editor");
            right.Undo();
            right.CaretOffset = right.Document.GetLineByNumber(2).Offset;
            Require(Shortcut(right, Key.OemQuestion, ModifierKeys.Control) && right.Text.Contains("// int value2 = 2;"),
                "Toggle comment did not target the Diff editor");
            right.Undo();
            Require(right.Text == working, "Undoing Diff shortcuts changed the source");

            const string addition = "    // 修改草稿\n";
            int offset = working.IndexOf("    int value2", StringComparison.Ordinal);
            right.Document.Insert(offset, addition);
            var draft = working.Insert(offset, addition);
            Require(editor.Text == draft && File.ReadAllText(path) == working, "Editing did not share an unsaved draft");
            Wait(InvokeTask(owner, "OpenFileAsync", relative, null, true));
            Require(editor.Text == draft, "Switching tabs lost the diff edit");
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            right.Undo();
            Require(right.Text == working, "Switching tabs discarded undo history");
            right.Redo();
            Require(right.Text == draft, "Redo did not restore the diff edit");
            right.CaretOffset = offset + 4;
            int caret = right.CaretOffset;
            right.Document.Insert(caret, " ");
            var saved = draft.Insert(caret, " ");
            Wait(InvokeTask(owner, "UpdateEditableDiffAsync"));
            Require(right.CaretOffset >= caret && right.Text == saved, "Live diff refresh replaced the editable buffer");
            Wait(InvokeTask(owner, "SaveAsync", false));
            Require(File.ReadAllText(path) == saved && !right.IsReadOnly, "Saving the diff did not write the exact worktree text");

            Wait(git.StageAsync([relative]));
            Wait(InvokeTask(owner, "RefreshGitAsync"));
            changes.SelectedIndex = 0;
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            Require(right.IsReadOnly && right.Document != editor.Document && editor.Text == saved,
                "Switching to an index snapshot left it writable or mutated the source buffer");
            Require(!Shortcut(right, Key.OemQuestion, ModifierKeys.Control) && right.Text == saved, "An edit shortcut modified the index snapshot");
        }));
        check("Editor gutter tracks unsaved and staged edits against HEAD and clears after commit", () => WithRepository((owner, git, root) =>
        {
            Wait(InvokeTask(owner, "OpenFileAsync", "src/Example.java", null, true));
            var editor = (TextEditor)owner.FindName("Editor");
            var lines = editor.Text.Split('\n').ToList(); lines[3] = "    int changed = 2;"; lines.RemoveAt(8); lines.Insert(16, "    int added = 3;"); editor.Text = string.Join('\n', lines);
            Wait(InvokeTask(owner, "UpdateLineChangesAsync"));
            var margin = editor.TextArea.LeftMargins.OfType<LineChangeMargin>().Single();
            Require(margin.Changes.Select(change => change.Kind).Distinct().Count() == 3, "Unsaved add/edit/delete colors are missing");
            Wait(InvokeTask(owner, "SaveAsync", false)); Wait(git.StageAsync(["src/Example.java"])); Wait(InvokeTask(owner, "RefreshGitAsync"));
            Require(margin.Changes.Count == 3, "Staging incorrectly reset the HEAD baseline");
            Wait(git.RunAsync(["commit", "-m", "fixture update"])); Wait(InvokeTask(owner, "RefreshGitAsync"));
            Require(margin.Changes.Count == 0, "A new HEAD left obsolete editor markers");
        }));
        check("Amend fills the complete prior message and restores an untouched draft when unchecked", () => WithRepository((owner, git, root) =>
        {
            var message = (TextBox)owner.FindName("CommitMessage"); var amend = (CheckBox)owner.FindName("AmendCheck");
            message.Text = "my next draft"; amend.IsChecked = true; Wait(InvokeTask(owner, "UpdateAmendMessageAsync"));
            Require(message.Text == "fixture initial\n\nA multiline commit body.", "Amend omitted the previous subject or body");
            amend.IsChecked = false; Wait(InvokeTask(owner, "UpdateAmendMessageAsync")); Require(message.Text == "my next draft", "Unchecking amend discarded the original draft");
            amend.IsChecked = true; Wait(InvokeTask(owner, "UpdateAmendMessageAsync")); message.Text = "edited amend message";
            amend.IsChecked = false; Wait(InvokeTask(owner, "UpdateAmendMessageAsync")); Require(message.Text == "edited amend message", "Unchecking amend overwrote user edits");
        }));
        check("Search discovers externally created files and outside clicks dismiss without disabling the owner", () => WithRepository((owner, git, root) =>
        {
            File.WriteAllText(Path.Combine(root, "src", "FreshLink.java"), "public class FreshLink {}\n");
            var task = InvokeTask(owner, "SearchAsync", "all", "FreshLink");
            Until(() => Application.Current.Windows.OfType<SearchWindow>().Any());
            var search = Application.Current.Windows.OfType<SearchWindow>().Single();
            var results = (ListBox)search.FindName("Results"); Until(() => results.Items.Count > 0);
            Require(results.Items.Cast<SearchHit>().Any(hit => hit.Path == "src/FreshLink.java"), "A new untracked file is absent from Search Everywhere");
            Require(owner.IsEnabled, "Search still disables the entire parent window");
            int closing = 0;
            search.Closing += (_, _) =>
            {
                closing++;
                typeof(Window).GetMethod("OnDeactivated", Flags)!.Invoke(search, [EventArgs.Empty]);
            };
            owner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Wait(task); pump();
            Require(!search.IsVisible && closing == 1, "Deactivation during outside dismissal closed search more than once");
        }));
        check("Project tree siblings keep a fixed indent after expanding a deep package", () => WithRepository((owner, git, root) =>
        {
            var tree = (TreeView)owner.FindName("FileTree"); var top = (TreeViewItem)tree.Items[0];
            TreeViewItem Child(TreeViewItem item, string path) => item.Items.OfType<TreeViewItem>().Single(child => ((StackPanel)child.Header).ToolTip as string == path);
            var first = Child(top, "app-admin"); var second = Child(top, "app-api"); second.IsExpanded = true;
            var source = Child(second, "app-api/src"); source.IsExpanded = true;
            var main = Child(source, "app-api/src/main"); main.IsExpanded = true;
            var java = Child(main, "app-api/src/main/java"); java.IsExpanded = true;
            pump(); owner.UpdateLayout();
            double X(TreeViewItem item) => ((FrameworkElement)item.Header).TransformToAncestor(tree).Transform(new Point()).X;
            Require(Math.Abs(X(first) - X(second)) < 1, "Sibling modules have different visual depths: " + X(first) + "/" + X(second));
            foreach (var pair in new[] { (top, first), (second, source), (source, main), (main, java) })
                Require(Math.Abs(X(pair.Item2) - X(pair.Item1) - 18) < 1, "A deeper subtree stretched the row indentation: " + X(pair.Item1) + "/" + X(pair.Item2));
        }));
        check("Open Project starts a separate executable with a literal Unicode project argument", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "idea-lite open & 项目 ' " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var info = (ProcessStartInfo)typeof(MainWindow).GetMethod("ProjectInstanceStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
                Require(info.WorkingDirectory == root && info.ArgumentList.Last() == root && !info.UseShellExecute, "Project path was passed through a shell or lost literal characters");
                Require(Path.GetFileName(info.FileName) is "IdeaLite.exe" or "dotnet", "Open Project did not target a new application instance");
            }
            finally { Directory.Delete(root); }
        });
        check("Ctrl hover only decorates resolvable symbols and restores the normal cursor on release", () => WithRepository((owner, git, root) =>
        {
            Wait(InvokeTask(owner, "OpenFileAsync", "src/Example.java", null, true));
            var editor = (TextEditor)owner.FindName("Editor"); var view = editor.TextArea.TextView; view.EnsureVisualLines();
            var position = view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(1, 9), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle);
            var point = view.TransformToAncestor(editor).Transform(new Point(position.X - view.HorizontalOffset, position.Y - view.VerticalOffset));
            string text = editor.Text; int caret = editor.CaretOffset;
            var hover = typeof(MainWindow).GetMethod("UpdateDefinitionHover", Flags)!;
            hover.Invoke(owner, [editor, point, true]);
            var links = (IDictionary)typeof(MainWindow).GetField("_definitionLinks", Flags)!.GetValue(owner)!;
            var link = links[editor]!;
            Require((int)link.GetType().GetProperty("Length")!.GetValue(link)! == "Example".Length, "A resolvable class was not underlined");
            var cursor = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.QueryCursorEvent }; editor.RaiseEvent(cursor);
            Require(cursor.Cursor == Cursors.Hand && cursor.Handled, "Ctrl hover did not show a hand cursor");
            view.EnsureVisualLines();
            for (int i = 0; i < 5; i++) hover.Invoke(owner, [editor, point, true]);
            Require(view.VisualLinesValid, "Moving within the same Ctrl-hovered symbol forced a redraw");
            hover.Invoke(owner, [editor, point, false]);
            Require((int)link.GetType().GetProperty("Length")!.GetValue(link)! == 0 && editor.Text == text && editor.CaretOffset == caret, "Releasing Ctrl retained the link or changed the document");
        }));
        check("Reload All from Disk preserves drafts and Diff usage hints jump to the selected reference", () => WithRepository((owner, git, root) =>
        {
            var source = Path.Combine(root, "src", "Example.java");
            var readme = Path.Combine(root, "README.md");
            var original = File.ReadAllText(source);
            File.AppendAllText(source, "// first external edit\n");
            Wait(InvokeTask(owner, "OpenFileAsync", "README.md", null, true));
            var editor = (TextEditor)owner.FindName("Editor");
            editor.Document.Insert(editor.Document.TextLength, "My unsaved draft\n");
            var draft = editor.Text;
            File.AppendAllText(readme, "External README edit\n");
            Wait(InvokeTask(owner, "RefreshGitAsync"));
            var changes = (ListBox)owner.FindName("ChangesList");
            changes.SelectedItem = changes.Items.OfType<ChangeItem>().Single(item => item.Change.Path == "src/Example.java");
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            var right = (TextEditor)owner.FindName("NewEditor");
            right.CaretOffset = 5;
            var external = original + "// second external edit\n";
            File.WriteAllText(source, external);
            const string client = "class Client { Example example = new Example(); }\n";
            File.WriteAllText(Path.Combine(root, "src", "Client.java"), client);
            Wait(InvokeTask(owner, "ReloadAllFromDiskAsync"));
            Require(right.Text == external && right.CaretOffset == 5 && !right.IsReadOnly && !right.CanUndo, "Reload did not safely refresh the Diff buffer");
            Require(editor.Text == draft && File.ReadAllText(readme).Contains("External README edit"), "Reload discarded or wrote a dirty draft");
            var view = right.TextArea.TextView; view.EnsureVisualLines();
            var hints = view.BackgroundRenderers.Single(renderer => renderer.GetType().Name == "TypeUsageHints");
            var drawings = new DrawingGroup();
            using (var drawing = drawings.Open()) hints.Draw(view, drawing);
            Require(drawings.Children.Count > 0, "The class usage hint was not drawn");
            var hit = (SearchHit?)hints.GetType().GetMethod("HitTest", Flags)!.Invoke(hints, [view,
                new Point(drawings.Bounds.X + drawings.Bounds.Width / 2, drawings.Bounds.Y + drawings.Bounds.Height / 2)]);
            Require(hit?.Name == "Example", "The painted usage hint was not clickable at its displayed location");
            var open = InvokeTask(owner, "ShowTypeUsagesAsync", hit);
            Until(() => owner.OwnedWindows.OfType<SearchWindow>().Any());
            var popup = owner.OwnedWindows.OfType<SearchWindow>().Single();
            var results = (ListBox)popup.FindName("Results");
            Until(() => results.Items.Count == 2);
            results.SelectedIndex = 1;
            popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Wait(open);
            Require(editor.Text == client && editor.CaretOffset == client.LastIndexOf("Example", StringComparison.Ordinal),
                "Selecting the second usage did not jump to its exact column");
        }));
        check("Ctrl navigation opens the imported Maven interface from the managed source JAR", () => WithRepository((owner, git, root) =>
        {
            File.WriteAllText(Path.Combine(root, "pom.xml"), """
                <project><groupId>demo</groupId><artifactId>parent</artifactId><version>1</version>
                <properties><library.version>2</library.version></properties>
                <dependencyManagement><dependencies><dependency>
                <groupId>external.lib</groupId><artifactId>responses</artifactId><version>${library.version}</version>
                </dependency></dependencies></dependencyManagement></project>
                """);
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, "app", "pom.xml"), """
                <project><parent><groupId>demo</groupId><artifactId>parent</artifactId><version>1</version></parent><artifactId>app</artifactId>
                <dependencies><dependency><groupId>external.lib</groupId><artifactId>responses</artifactId></dependency></dependencies></project>
                """);
            const string path = "app/ApiResponse.java";
            const string client = "import external.lib.Response;\nclass ApiResponse implements Response {}\n";
            File.WriteAllText(Path.Combine(root, path), client);
            File.WriteAllText(Path.Combine(root, "src", "Response.java"), "package unrelated;\ninterface Response {}\n");
            var repository = Path.Combine(root, "target", "local-repository");
            var version = Path.Combine(repository, "external", "lib", "responses", "2");
            Directory.CreateDirectory(version);
            const string dependency = "package external.lib;\n\npublic interface Response {\n    int getCode();\n}\n";
            using (var jar = System.IO.Compression.ZipFile.Open(Path.Combine(version, "responses-2-sources.jar"), System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(jar.CreateEntry("external/lib/Response.java").Open()))
                writer.Write(dependency);
            var layout = (WorkbenchLayout)typeof(MainWindow).GetField("_layout", Flags)!.GetValue(owner)!;
            var previous = layout.MavenRepository;
            layout.MavenRepository = repository;
            try
            {
                Wait(InvokeTask(owner, "ReloadAllFromDiskAsync"));
                Wait(InvokeTask(owner, "OpenFileAsync", path, null, true));
                var editor = (TextEditor)owner.FindName("Editor");
                var view = editor.TextArea.TextView;
                view.EnsureVisualLines();
                var offset = client.LastIndexOf("Response", StringComparison.Ordinal);
                var position = view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(editor.Document.GetLocation(offset)),
                    ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle);
                var point = view.TransformToAncestor(editor).Transform(new Point(position.X - view.HorizontalOffset, position.Y - view.VerticalOffset));
                typeof(MainWindow).GetMethod("UpdateDefinitionHover", Flags)!.Invoke(owner, [editor, point, true]);
                var links = (IDictionary)typeof(MainWindow).GetField("_definitionLinks", Flags)!.GetValue(owner)!;
                var link = links[editor]!;
                Require((int)link.GetType().GetProperty("Length")!.GetValue(link)! == "Response".Length,
                    "An imported Maven interface did not acquire the Ctrl-click link");
                Wait(InvokeTask(owner, "NavigateDefinitionAsync", editor, offset));
                var source = owner.OwnedWindows.Cast<Window>().Single(window => window.Title.StartsWith("external.lib.Response", StringComparison.Ordinal));
                try
                {
                    var viewer = (TextEditor)source.Content;
                    Require(viewer.IsReadOnly && viewer.Text == dependency && viewer.TextArea.Caret.Line == 3 && editor.Text == client,
                        "Navigation did not show the imported interface at its declaration in a read-only viewer");
                }
                finally { source.Close(); }
            }
            finally { layout.MavenRepository = previous; }
        }));
        check("Document tabs scroll and bulk close protects drafts, including an editable Diff", () => WithRepository((owner, git, root) =>
        {
            var scroll = (ScrollViewer)owner.FindName("TabScroll");
            var tabs = (StackPanel)owner.FindName("DocumentTabs");
            var editor = (TextEditor)owner.FindName("Editor");
            MenuItem ActionAt(int tab, int action) => (MenuItem)((Border)tabs.Children[tab]).ContextMenu.Items[action];
            void Click(MenuItem action) => action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            void AnswerNextConfirmation(bool answer) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                owner.OwnedWindows.Cast<Window>().Single(window => window.IsVisible).DialogResult = answer);
            for (int index = 0; index < 8; index++)
            {
                string name = $"src/LongNameForEditorOverflowDocument{index}.java";
                File.WriteAllText(Path.Combine(root, name), "class Fixture {}\n");
                Wait(InvokeTask(owner, "OpenFileAsync", name, null, true));
            }
            Until(() => scroll.ScrollableWidth > 150 && scroll.HorizontalOffset > 120);
            double previous = scroll.HorizontalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Until(() => scroll.HorizontalOffset < previous);
            previous = scroll.HorizontalOffset;
            ((Button)owner.FindName("TabsRight")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => scroll.HorizontalOffset > previous);
            Require(((Button)owner.FindName("TabActions")).IsEnabled, "The right-side tab actions button was unavailable");
            editor.AppendText("// draft");
            var others = ActionAt(0, 1);
            AnswerNextConfirmation(false);
            Click(others);
            Require(tabs.Children.Count == 9 && editor.Text.Contains("// draft"), "Canceling Close Other Tabs discarded a draft or removed tabs");
            AnswerNextConfirmation(true);
            Click(ActionAt(0, 1));
            Until(() => tabs.Children.Count == 1 && editor.Text.StartsWith("# Interaction fixture", StringComparison.Ordinal));
            Click(ActionAt(0, 2));
            Until(() => tabs.Children.Count == 0);

            string path = Path.Combine(root, "src", "Example.java");
            File.AppendAllText(path, "// external update\n");
            Wait(InvokeTask(owner, "RefreshGitAsync"));
            var changes = (ListBox)owner.FindName("ChangesList");
            changes.SelectedItem = changes.Items.OfType<ChangeItem>().Single(item => item.Change.Path == "src/Example.java");
            Wait(InvokeTask(owner, "OpenSelectedChangeAsync"));
            var right = (TextEditor)owner.FindName("NewEditor");
            right.AppendText("// Diff draft");
            Click(ActionAt(tabs.Children.Count - 1, 1));
            Require(tabs.Children.Count == 1 && !right.IsReadOnly && right.Text.EndsWith("// Diff draft", StringComparison.Ordinal),
                "Closing other tabs detached the retained Diff's draft");
            Click(ActionAt(0, 0));
            Until(() => editor.IsVisible && editor.Text.EndsWith("// Diff draft", StringComparison.Ordinal));
            Require(tabs.Children.Count == 1 && !File.ReadAllText(path).Contains("// Diff draft"),
                "Closing the only Diff tab failed to preserve its unsaved source buffer");
        }));
        check("Single clicks open source and diffs while commit checkboxes keep their selection behavior", () => WithRepository((owner, git, root) =>
        {
            var tree = (TreeView)owner.FindName("FileTree"); var top = (TreeViewItem)tree.Items[0];
            var directory = top.Items.OfType<TreeViewItem>().Single(item => ((StackPanel)item.Header).ToolTip as string == "src"); directory.IsExpanded = true; pump();
            var file = directory.Items.OfType<TreeViewItem>().Single();
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = file.Header });
            var editor = (TextEditor)owner.FindName("Editor"); Until(() => editor.Text.Contains("class Example"));
            File.AppendAllText(Path.Combine(root, "src", "Example.java"), "// external update\n");
            Wait(InvokeTask(owner, "RefreshGitAsync"));
            ((Button)owner.FindName("CommitRail")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump();
            var changes = (ListBox)owner.FindName("ChangesList"); Until(() => changes.Items.Count == 1); changes.SelectedIndex = 0; changes.UpdateLayout(); pump();
            var row = (ListBoxItem)changes.ItemContainerGenerator.ContainerFromIndex(0);
            var checkbox = Descendants(row).OfType<CheckBox>().Single(); checkbox.IsChecked = true;
            changes.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = checkbox });
            Require(((Grid)owner.FindName("DiffPane")).Visibility != Visibility.Visible, "Clicking a commit checkbox opened a diff");
            var label = Descendants(row).OfType<TextBlock>().First(block => block.Text == "Example.java");
            changes.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = label });
            Until(() => ((Grid)owner.FindName("DiffPane")).Visibility == Visibility.Visible);
            Require(((TextEditor)owner.FindName("NewEditor")).Text.Contains("external update"), "A single row click did not open the selected file diff");
        }));
    }
    private static void WithRepository(Action<MainWindow, GitService, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "idea-lite-interactions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var git = new GitService(root); Wait(git.RunAsync(["init"])); Wait(git.RunAsync(["config", "user.name", "Fixture"])); Wait(git.RunAsync(["config", "user.email", "fixture@example.invalid"]));
        Directory.CreateDirectory(Path.Combine(root, "src")); File.WriteAllText(Path.Combine(root, "src", "Example.java"), "class Example {\n" + string.Join('\n', Enumerable.Range(1, 20).Select(number => "    int value" + number + " = " + number + ";")) + "\n}\n");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Interaction fixture\n");
        foreach (string module in new[] { "app-admin", "app-api" })
        {
            var directory = Path.Combine(root, module, "src/main/java/dev/idealite/shop"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Module.java"), "package dev.idealite.shop; class Module {}\n");
            File.WriteAllText(Path.Combine(root, module, "pom.xml"), "<project><modelVersion>4.0.0</modelVersion><groupId>dev.idealite</groupId><artifactId>" + module + "</artifactId><version>1</version></project>");
        }
        Wait(git.StageAsync(["."])); Wait(git.RunAsync(["commit", "-m", "fixture initial", "-m", "A multiline commit body."]));
        var owner = new MainWindow(null); owner.Show();
        try { Wait(InvokeTask(owner, "OpenWorkspaceAsync", root)); action(owner, git, root); }
        finally
        {
            foreach (var search in owner.OwnedWindows.Cast<Window>().ToArray()) search.Close();
            var documents = (IEnumerable)typeof(MainWindow).GetField("_documents", Flags)!.GetValue(owner)!;
            foreach (var document in documents) document.GetType().GetProperty("Dirty")!.SetValue(document, false);
            owner.Close();
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
    private static Task InvokeTask(object target, string method, params object?[] arguments) => (Task)target.GetType().GetMethod(method, Flags)!.Invoke(target, arguments)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew(); while (!done() && timer.Elapsed.TotalSeconds < 25) { _pump(); Thread.Sleep(10); }
        Require(done(), "Interaction fixture timed out"); _pump();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
