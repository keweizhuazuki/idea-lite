using System.IO;
using System.Windows;

namespace IdeaLite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Idea Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var args = e.Args;
        var root = args.FirstOrDefault(a => Directory.Exists(a));
        var window = new MainWindow(root);
        MainWindow = window;
        window.Show();
    }
}
