using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BlueStar.Linux.ViewModels;
using BlueStar.Linux.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BlueStar.Linux;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = Program.Services.GetRequiredService<MainViewModel>() };
        base.OnFrameworkInitializationCompleted();
    }
}
