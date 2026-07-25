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
    public static bool AllowShutdown { get; set; }

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

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            // Intercept close to hide instead (only block normal window-close, not explicit shutdown)
            mainWindow.Closing += (s, e) =>
            {
                if (!AllowShutdown)
                {
                    e.Cancel = true;
                    mainWindow.Hide();
                }
            };

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var trayIcon = new Avalonia.Controls.TrayIcon
            {
                Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://client/Assets/avalonia-logo.ico"))),
                ToolTipText = "Alpha AI Tracker"
            };

            var showItem = new Avalonia.Controls.NativeMenuItem("Show Alpha AI Tracker");
            showItem.Click += (s, e) =>
            {
                mainWindow.Show();
                mainWindow.Activate();
            };

            var exitItem = new Avalonia.Controls.NativeMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                AllowShutdown = true;
                desktop.Shutdown();
            };

            var menu = new Avalonia.Controls.NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(exitItem);

            trayIcon.Menu = menu;
            trayIcon.IsVisible = true;
            
            var trayIcons = new Avalonia.Controls.TrayIcons { trayIcon };
            Avalonia.Controls.TrayIcon.SetIcons(this, trayIcons);

            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--background") && !args.Contains("--minimized"))
            {
                mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}