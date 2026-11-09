using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using IdeaLite.Core;

namespace IdeaLite;

public partial class MainWindow
{
    private sealed record TerminalTab(string Name, string Root, TerminalControl View, NativeTerminalSession Session);
    private readonly List<TerminalTab> _terminals = [];
    private TerminalTab? _activeTerminal;
    private int _terminalNumber;

    internal static (string CommandLine, string Directory) EmbeddedTerminalCommand(string root)
    {
        var gitBash = GitService.GitBashExecutablePath ?? throw new InvalidOperationException("未找到 Git Bash，请安装 Git for Windows 后重试。");
        var executable = Path.Combine(Path.GetDirectoryName(gitBash)!, "bin", "bash.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Git Bash executable was not found.", executable);
        // 使用 Git for Windows 的原生启动器，项目路径通过 CreateProcess 的工作目录传递。
        return ($"\"{executable}\" --login -i", Path.GetFullPath(root));
    }
    private void AddTerminal()
    {
        if (_workspace == null) return;
        var command = EmbeddedTerminalCommand(_workspace.Root);
        var view = new TerminalControl { AutoResize = true, Background = (Brush)FindResource("Bg") };
        var session = new NativeTerminalSession(command.CommandLine, command.Directory);
        var tab = new TerminalTab(++_terminalNumber == 1 ? "本地" : $"本地 {_terminalNumber}", _workspace.Root, view, session);
        session.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_terminals.Contains(tab)) return;
            view.IsEnabled = false; RenderTerminalTabs(tab);
        });
        view.Loaded += (_, _) =>
        {
            uint ColorRef(string hex) { var color = (Color)ColorConverter.ConvertFromString(hex); return color.R | (uint)color.G << 8 | (uint)color.B << 16; }
            var theme = new TerminalTheme
            {
                DefaultBackground = ColorRef("#191A1C"), DefaultForeground = ColorRef("#BCBEC4"), DefaultSelectionBackground = ColorRef("#2E4368"), CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = new[] { "#191A1C", "#C75450", "#6AAB73", "#C4A44C", "#6C95EB", "#C77DBB", "#2AACB8", "#BCBEC4", "#7A7E85", "#F77669", "#A8C893", "#E5C07B", "#8DB5F4", "#DCA6D3", "#80CBC4", "#DFE1E5" }.Select(ColorRef).ToArray()
            };
            view.SetTheme(theme, "Consolas", 14, (Color)ColorConverter.ConvertFromString("#191A1C"));
        };
        view.Connection = session;
        _terminals.Add(tab); TerminalPane.Children.Add(view); SelectTerminal(tab);
    }
    private void SelectTerminal(TerminalTab tab)
    {
        _activeTerminal = tab;
        foreach (var terminal in _terminals) terminal.View.Visibility = Visible(terminal == tab);
        RenderTerminalTabs();
        Dispatcher.BeginInvoke(() => tab.View.Focus());
    }
    private void RenderTerminalTabs(TerminalTab? exited = null)
    {
        TerminalTabs.Children.Clear();
        foreach (var tab in _terminals)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = tab.Root };
            var select = new Button { Style = (Style)FindResource("DockTab"), Tag = tab == _activeTerminal ? "active" : null };
            UiText.Set(select, "Content", () => UiText.Current.Translate(tab.Name) + (!tab.View.IsEnabled || tab == exited ? " · " + UiText.Current.Translate("已退出") : ""));
            select.Click += (_, _) => SelectTerminal(tab);
            var close = new Button { Content = new IdeIcon { Kind = "Close", Width = 10, Height = 10 }, Style = (Style)FindResource("FlatButton"), Padding = new Thickness(3), ToolTip = "关闭终端" }.Localize("ToolTip");
            close.Click += (_, _) => CloseTerminal(tab);
            row.Children.Add(select); row.Children.Add(close); TerminalTabs.Children.Add(row);
        }
    }
    private void CloseTerminal(TerminalTab tab)
    {
        _terminals.Remove(tab); TerminalPane.Children.Remove(tab.View); tab.Session.Dispose();
        if (_activeTerminal == tab) _activeTerminal = _terminals.LastOrDefault();
        if (_activeTerminal != null) SelectTerminal(_activeTerminal); else RenderTerminalTabs();
    }
    private void CloseTerminals()
    {
        var tabs = _terminals.ToArray(); _terminals.Clear(); _activeTerminal = null;
        TerminalPane.Children.Clear(); TerminalTabs.Children.Clear();
        foreach (var tab in tabs) tab.Session.Dispose();
    }
    private void AddTerminalClick(object sender, RoutedEventArgs e)
    {
        try { AddTerminal(); }
        catch (Exception ex) { Dialogs.Error(this, ex); }
    }
}
