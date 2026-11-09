using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IdeaLite;

public partial class MainWindow
{
    private void InitializeTerminalInput()
    {
        // HwndHost 的按键不一定进入 WPF 路由；只拦截当前终端子窗口的快捷键。
        ComponentDispatcher.ThreadFilterMessage += TerminalMessage;
        Closed += (_, _) => ComponentDispatcher.ThreadFilterMessage -= TerminalMessage;
    }
    private void TerminalMessage(ref MSG message, ref bool handled)
    {
        if (handled || _activeTerminal == null || !TerminalPane.IsVisible || message.message is not (0x100 or 0x104 or 0x205)) return;
        static HwndHost? Host(DependencyObject parent)
        {
            if (parent is HwndHost host) return host;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (Host(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
            return null;
        }
        var host = Host(_activeTerminal.View);
        if (host == null || (message.hwnd != host.Handle && !IsChild(host.Handle, message.hwnd))) return;
        if (message.message == 0x205)
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, PlacementTarget = _activeTerminal.View };
            void Item(string title, Action action) { var item = new MenuItem { Header = title }.Localize("Header"); item.Click += (_, _) => action(); menu.Items.Add(item); }
            Item("复制", () => CopyTerminal()); Item("粘贴", PasteTerminal); menu.Items.Add(new Separator());
            Item("新建终端", AddTerminal); Item("关闭终端", () => { if (_activeTerminal != null) CloseTerminal(_activeTerminal); });
            menu.IsOpen = true; handled = true; return;
        }
        var modifiers = ModifierKeys.None;
        if (GetKeyState(0x11) < 0) modifiers |= ModifierKeys.Control;
        if (GetKeyState(0x10) < 0) modifiers |= ModifierKeys.Shift;
        if (GetKeyState(0x12) < 0) modifiers |= ModifierKeys.Alt;
        handled = HandleTerminalShortcut(KeyInterop.KeyFromVirtualKey(message.wParam.ToInt32()), modifiers);
    }
    private bool HandleTerminalShortcut(Key key, ModifierKeys modifiers)
    {
        if (_activeTerminal == null) return false;
        var controlShift = ModifierKeys.Control | ModifierKeys.Shift;
        if (modifiers == controlShift && key == Key.T) { AddTerminal(); return true; }
        if (modifiers == controlShift && key == Key.W) { CloseTerminal(_activeTerminal); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.F12) { HideBottomClick(this, new RoutedEventArgs()); return true; }
        if ((modifiers == controlShift && key == Key.V) || (modifiers == ModifierKeys.Control && key == Key.V) || (modifiers == ModifierKeys.Shift && key == Key.Insert))
        { PasteTerminal(); return true; }
        if ((modifiers == controlShift || modifiers == ModifierKeys.Control) && key == Key.C)
            return CopyTerminal() || modifiers == controlShift;
        return false;
    }
    private bool CopyTerminal()
    {
        try
        {
            string text = _activeTerminal?.View.GetSelectedText() ?? "";
            if (text.Length == 0) return false;
            Clipboard.SetText(text); return true;
        }
        catch (ExternalException) { SetStatus("剪贴板暂时不可用，请重试。"); return true; }
    }
    private void PasteTerminal()
    {
        try { if (_activeTerminal != null && Clipboard.ContainsText()) _activeTerminal.Session.WriteInput(_activeTerminal.Session.PreparePaste(Clipboard.GetText())); }
        catch (ExternalException) { SetStatus("剪贴板暂时不可用，请重试。"); }
    }
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);
}
