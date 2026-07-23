using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using client.ViewModels;
using client.Views;
using Microsoft.Extensions.DependencyInjection;

namespace client;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; internal set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (ServiceProvider == null)
            {
                throw new InvalidOperationException("ServiceProvider must be set before app starts");
            }

            var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();

            // Initialize async (check existing login state from SQLite)
            Task.Run(async () =>
            {
                await viewModel.InitializeAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}