using Microsoft.UI.Xaml;

namespace DeltaHarmonica.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaHarmonica");
                Directory.CreateDirectory(path);
                File.AppendAllText(Path.Combine(path, "crash.log"), $"{DateTimeOffset.Now:O}\n{e.Exception}\n");
            }
            catch { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
