using System.IO;
using System.Windows;

namespace MBDInspector;

public partial class App : Application
{
    private void App_Startup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        window.Show();

        if (e.Args.Length > 0)
        {
            string path = Path.GetFullPath(e.Args[0]);
            window.OpenFile(path);
        }
    }
}
