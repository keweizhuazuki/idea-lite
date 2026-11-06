using System.IO;
using System.Windows;

namespace IdeaLite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiText.Current.Language = WorkbenchLayout.Load().Language;
        DispatcherUnhandledException += (_, args) =>
        {
            Dialogs.Error(MainWindow, args.Exception);
            args.Handled = true;
        };
        var args = e.Args;
        var root = args.FirstOrDefault(a => Directory.Exists(a));
        var window = new MainWindow(root);
        MainWindow = window;
        window.Show();
    }
}
