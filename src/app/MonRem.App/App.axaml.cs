using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MonRem.App.Views;

namespace MonRem.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "Monitor Remoto",
                    Width = 1280,
                    Height = 800,
                    Content = new Shell(),
                };
                break;
            case IActivityApplicationLifetime activity:
                activity.MainViewFactory = () => new Shell();
                break;
            case ISingleViewApplicationLifetime single:
                single.MainView = new Shell();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
