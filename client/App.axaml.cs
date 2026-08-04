using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using client.Core;
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

            var hideItem = new Avalonia.Controls.NativeMenuItem("Hide");
            hideItem.Click += (s, e) =>
            {
                mainWindow.Hide();
            };

            var menu = new Avalonia.Controls.NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(hideItem);

            trayIcon.Menu = menu;
            trayIcon.IsVisible = true;
            
            var trayIcons = new Avalonia.Controls.TrayIcons { trayIcon };
            Avalonia.Controls.TrayIcon.SetIcons(this, trayIcons);

            // ─── Single-instance activation ───
            // This process is the primary (mutex-owning) instance — it may have
            // been launched hidden (--background / --minimized by auto-start or
            // systemd) and is the one that keeps tracking alive. When a second
            // user launch (e.g. the user clicks the desktop entry) sends a SHOW
            // signal via the named pipe, bring this window to the front. This is
            // what makes "open the GUI any number of times" work without ever
            // stopping the background tracker.
            SingleInstanceService.OnShowRequested = () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                            mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                        mainWindow.Show();
                        mainWindow.Activate();
                        mainWindow.Topmost = true;
                        mainWindow.Topmost = false; // bring to front without pinning
                    }
                    catch
                    {
                        // Window may be in an odd state during shutdown — ignore.
                    }
                });
            };

            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--background") && !args.Contains("--minimized"))
            {
                mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}