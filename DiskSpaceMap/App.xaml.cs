using System.Windows;

namespace DiskSpaceMap;

public partial class App : Application
{
    private readonly bool _openMainWindow;

    public App() : this(openMainWindow: true) { }

    internal App(bool openMainWindow) => _openMainWindow = openMainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_openMainWindow) new MainWindow().Show();
    }
}
