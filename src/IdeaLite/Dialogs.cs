using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdeaLite.Core;
using Microsoft.Win32;

namespace IdeaLite;

public static class Dialogs
{
    public static bool BuildSettings(Window owner, WorkbenchLayout settings)
    {
        var window = new Window { Owner = owner, Title = "构建工具与环境", Width = 660, Height = 480, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }.Localize("Content"));
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)owner.FindResource("Primary") }.Localize("Content");
        save.Click += (_, _) => window.DialogResult = true; buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var form = new StackPanel(); panel.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "Git：" + GitService.ExecutablePath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }.Localize("Text"));
        var wsl = new CheckBox { Content = "使用 WSL 运行 Maven", IsChecked = settings.MavenInWsl, Margin = new Thickness(0, 0, 0, 10) }.Localize("Content"); form.Children.Add(wsl);
        form.Children.Add(new TextBlock { Text = "WSL 模式填写 Linux 路径；Windows 模式填写 Windows 路径。留空时使用项目 Wrapper 或 PATH。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }.Localize("Text"));
        bool browsing = false;
        string? distributionRoot = null;
        TextBox Input(string label, string value, string? filter = null, bool folder = false)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var title = new TextBlock { Text = label, Width = 130 }.Localize("Text"); DockPanel.SetDock(title, Dock.Left); row.Children.Add(title);
            var field = new TextBox { Text = value };
            if (filter != null || folder)
            {
                var browse = new Button { Content = "浏览…", Margin = new Thickness(7, 0, 0, 0), MinWidth = 68 }.Localize("Content");
                System.Windows.Automation.AutomationProperties.SetName(browse, UiText.Current.Format($"浏览 {UiText.Current.Translate(label)}"));
                DockPanel.SetDock(browse, Dock.Right);
                row.Children.Add(browse);
                browse.Click += async (_, _) =>
                {
                    if (browsing) return;
                    browsing = true;
                    browse.IsEnabled = false;
                    try
                    {
                        bool useWsl = wsl.IsChecked == true;
                        if (useWsl && distributionRoot == null)
                        {
                            var result = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                                ["--exec", "wslpath", "-w", "/"], Environment.SystemDirectory, timeoutSeconds: 10);
                            if (result.ExitCode != 0 || !result.Output.Trim().StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("无法打开默认 WSL 发行版的文件系统。");
                            distributionRoot = result.Output.Trim();
                        }
                        if (!window.IsVisible || useWsl != (wsl.IsChecked == true)) return;
                        var current = field.Text.Trim();
                        if (useWsl) current = WslProcess.WindowsBrowsePath(current, distributionRoot!);
                        var initial = folder && Directory.Exists(current) ? current : Path.GetDirectoryName(current);
                        if (!Directory.Exists(initial)) initial = useWsl ? distributionRoot : null;
                        string? selected;
                        if (folder)
                        {
                            var picker = new OpenFolderDialog { Title = UiText.Current.Translate(label), InitialDirectory = initial ?? "" };
                            selected = picker.ShowDialog(window) == true ? picker.FolderName : null;
                        }
                        else
                        {
                            var picker = new OpenFileDialog
                            {
                                Title = UiText.Current.Translate(label), InitialDirectory = initial ?? "",
                                Filter = UiText.Current.Translate(useWsl && label == "Maven 可执行文件" ? "所有文件|*.*" : filter),
                                FileName = File.Exists(current) ? Path.GetFileName(current) : ""
                            };
                            selected = picker.ShowDialog(window) == true ? picker.FileName : null;
                        }
                        if (selected != null) field.Text = useWsl ? WslProcess.LinuxBrowsePath(selected, distributionRoot!) : selected;
                    }
                    catch (Exception ex)
                    {
                        if (window.IsVisible) Error(window, ex);
                    }
                    finally { browsing = false; browse.IsEnabled = true; }
                };
            }
            row.Children.Add(field); form.Children.Add(row); return field;
        }
        var executable = Input("Maven 可执行文件", settings.MavenExecutable, "可执行文件|*.exe;*.cmd;*.bat|所有文件|*.*");
        var configuration = Input("settings.xml", settings.MavenSettings, "XML 文件|*.xml|所有文件|*.*");
        var repository = Input("本地仓库", settings.MavenRepository, folder: true);
        var java = Input("JAVA_HOME", settings.JavaHome, folder: true);
        var goals = Input("默认任务与参数", settings.MavenGoals);
        window.Content = panel;
        if (window.ShowDialog() != true) return false;
        settings.MavenInWsl = wsl.IsChecked == true;
        settings.MavenExecutable = executable.Text.Trim(); settings.MavenSettings = configuration.Text.Trim();
        settings.MavenRepository = repository.Text.Trim(); settings.JavaHome = java.Text.Trim();
        settings.MavenGoals = string.IsNullOrWhiteSpace(goals.Text) ? "test" : goals.Text.Trim();
        settings.Save(); return true;
    }

    public static void Error(Window? owner, Exception error)
    {
        var detail = error.GetType().FullName + Environment.NewLine + error.StackTrace;
        try
        {
            var directory = Environment.GetEnvironmentVariable("IDEALITE_CONFIG_DIR") ??
                (System.IO.Directory.Exists(@"D:\") ? @"D:\Codex\IdeaLite" : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdeaLite"));
            System.IO.Directory.CreateDirectory(directory);
            // Only persist the type and stack; command output and exception messages can contain credentials.
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "last-error.log"), detail);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        var window = new Window { Owner = owner, Title = "Idea Lite · 操作失败", Width = 640, Height = 340, WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(16) };
        var message = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }.Localize("Text");
        DockPanel.SetDock(message, Dock.Top); panel.Children.Add(message);
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right }.Localize("Content");
        close.Click += (_, _) => window.Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = detail, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Content = panel; window.ShowDialog();
    }

    public static string? Prompt(Window owner, string title, string description, string value = "", bool multiline = false)
    {
        var window = new Window { Owner = owner, Title = title, Width = 620, Height = multiline ? 360 : 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var label = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) }.Localize("Text");
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75 }.Localize("Content");
        var ok = new Button { Content = "确定", IsDefault = !multiline, MinWidth = 75 }.Localize("Content");
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var input = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top };
        panel.Children.Add(input); window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }

    public static bool PushPreview(Window owner, string destination, string commits)
    {
        var dialog = new Window { Title = "推送提交", Owner = owner, Width = 590, Height = 325, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(12) };
        var route = new TextBlock { Text = destination, FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 5, 5, 10) };
        DockPanel.SetDock(route, Dock.Top); panel.Children.Add(route);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var push = new Button { Content = "推送", IsDefault = true, Style = (Style)owner.FindResource("Primary") }.Localize("Content");
        push.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }.Localize("Content")); buttons.Children.Add(push);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var preview = new TextBox { IsReadOnly = true, Text = commits.TrimEnd(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (string.IsNullOrWhiteSpace(commits)) UiText.Set(preview, "Text", "没有待推送提交");
        panel.Children.Add(preview);
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    public static bool Confirm(Window owner, string message)
    {
        var window = new Window { Owner = owner, Title = "Idea Lite · 确认操作", Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = 520, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var no = new Button { Content = "否", IsCancel = true, IsDefault = true, MinWidth = 75 }.Localize("Content");
        var yes = new Button { Content = "是", MinWidth = 75 }.Localize("Content");
        no.Click += (_, _) => window.DialogResult = false;
        yes.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(no); buttons.Children.Add(yes); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }.Localize("Text"));
        window.Content = panel; return window.ShowDialog() == true;
    }

    public static void Information(Window owner, string title, string message, bool localizeMessage = true)
    {
        var window = new Window { Owner = owner, Title = title, Width = 720, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Localize("Title");
        var panel = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right }.Localize("Content");
        close.Click += (_, _) => window.Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var content = new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(localizeMessage ? content.Localize("Text") : content);
        window.Content = panel; window.ShowDialog();
    }
}
