using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using IdeaLite;
using IdeaLite.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        if (args.Length == 3 && args[0] == "--render-preview")
        {
            RenderPreview(args[1], args[2]); app.Shutdown(); return 0;
        }
        int failed = 0;
        void Check(string name, Action check)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        Check("Dark context menus and nested popup surfaces", () =>
        {
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "文件", InputGestureText = "Ctrl+O" };
            item.Items.Add(new MenuItem { Header = "打开项目" });
            menu.Items.Add(item);
            menu.Style = (Style)app.FindResource(typeof(ContextMenu));
            item.Style = (Style)app.FindResource(typeof(MenuItem));
            menu.ApplyTemplate(); item.ApplyTemplate();
            var popup = item.Template.FindName("PART_Popup", item) as Popup ?? throw new Exception("Missing submenu popup");
            var border = popup.Child as Border ?? throw new Exception("Missing submenu surface");
            Require((border.Background as SolidColorBrush)?.Color == Color.FromRgb(43, 45, 48), "Submenu background must follow the dark palette");
            Require((item.Foreground as SolidColorBrush)?.Color == Color.FromRgb(223, 225, 229), "Menu foreground must stay readable");
        });
        Check("File checkboxes update the commit selection in both directions", () =>
        {
            var workbench = new MainWindow(null);
            var list = (ListBox)workbench.FindName("ChangesList");
            var choice = new CommitChoice();
            var item = new ChangeItem(new("Example.java", null, ' ', 'M'), "更改", false, choice);
            var row = (FrameworkElement)list.ItemTemplate.LoadContent();
            row.DataContext = item;
            row.Measure(new Size(350, 30)); row.Arrange(new Rect(0, 0, 350, 30)); Pump();
            var checkbox = ((Grid)row).Children.OfType<CheckBox>().Single();
            checkbox.IsChecked = true; Pump();
            Require(choice.Included, "Checked file was not included");
            choice.Included = false; Pump();
            Require(checkbox.IsChecked == false, "Selection model did not update the checkbox");
        });
        Check("Deferred diff scrolling follows the input pane without feedback", () =>
        {
            var left = new TextEditor(); var right = new TextEditor();
            EditorSupport.Configure(left); EditorSupport.Configure(right);
            left.Options.AllowScrollBelowDocument = right.Options.AllowScrollBelowDocument = false;
            left.Text = right.Text = string.Join('\n', Enumerable.Range(0, 400).Select(i => "line " + i));
            var host = new Grid { Width = 1000, Height = 300 };
            host.ColumnDefinitions.Add(new()); host.ColumnDefinitions.Add(new());
            host.Children.Add(left); host.Children.Add(right); Grid.SetColumn(right, 1);
            void Layout() { host.Measure(new Size(1000, 300)); host.Arrange(new Rect(0, 0, 1000, 300)); host.UpdateLayout(); Pump(); }
            Layout();
            _ = new DiffScrollSynchronizer(left, right);
            void Drive(TextEditor editor, double offset)
            {
                editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                editor.ScrollToVerticalOffset(offset);
                for (int i = 0; i < 4; i++) Layout();
            }
            Drive(left, 230);
            Require(left.VerticalOffset > 100, "Scroll fixture did not move");
            Require(Math.Abs(left.VerticalOffset - right.VerticalOffset) < 1, "Right pane failed to follow");
            double stable = left.VerticalOffset;
            for (int i = 0; i < 8; i++) Layout();
            Require(Math.Abs(left.VerticalOffset - stable) < 1, "Deferred follower events moved the driver");
            Drive(right, 460);
            Require(Math.Abs(left.VerticalOffset - right.VerticalOffset) < 1, "Changing the input pane broke synchronization");
        });
        Console.WriteLine($"UI regression checks: {3 - failed} passed, {failed} failed.");
        app.Shutdown();
        return failed == 0 ? 0 : 1;
    }
    private static void RenderPreview(string project, string destination)
    {
        var workbench = new MainWindow(project);
        var host = (FrameworkElement)workbench.Content;
        void Layout()
        {
            host.Measure(new Size(1380, 860));
            host.Arrange(new Rect(0, 0, 1380, 860));
            host.UpdateLayout(); Pump();
        }
        void Until(Func<bool> done)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                Layout();
                if (timer.Elapsed.TotalSeconds > 25) throw new Exception("Preview did not finish loading");
                Thread.Sleep(10);
            }
            Layout();
        }
        workbench.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var editor = (TextEditor)workbench.FindName("Editor");
        Until(() => !editor.IsReadOnly);
        ((Button)workbench.FindName("CommitRail")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var changes = (ListBox)workbench.FindName("ChangesList");
        Until(() => changes.Items.Count > 0);
        var item = changes.Items.Cast<ChangeItem>().First(c => c.Change.Name == "OrderService.java");
        item.Choice.Included = true; changes.SelectedItem = item;
        Button FindButton(DependencyObject root, string tooltip)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button && (button.ToolTip as string) == tooltip) return button;
                try { return FindButton(child, tooltip); } catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException("Button not found: " + tooltip);
        }
        FindButton(host, "显示差异 · Ctrl+D").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((Grid)workbench.FindName("DiffPane")).Visibility == Visibility.Visible);
        ((Button)workbench.FindName("GitRail")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((ListBox)workbench.FindName("LogList")).Items.Count > 0 &&
            ((TreeView)workbench.FindName("HistoryFiles")).Items.Count > 0);
        for (int i = 0; i < 4; i++) Layout();
        ((TextBlock)workbench.FindName("MemoryLabel")).Text = "";
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1380, 860, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        using var output = System.IO.File.Create(destination);
        png.Save(output);
        Console.WriteLine("Rendered native workbench preview: " + destination);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
