using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using client.Core;
using client.Services;
using client.ViewModels;
using client.Views;
using Microsoft.Extensions.DependencyInjection;

namespace client;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; internal set; }
    public static bool AllowShutdown { get; set; }

    /// <summary>True in the standalone terms-agent process (client.exe --terms):
    /// OnFrameworkInitializationCompleted then creates ONLY the TermsWindow with the
    /// agent's own DI services — no MainViewModel, no shell, no tray.</summary>
    public static bool TermsAgentMode { get; set; }

    /// <summary>
    /// True when the UI is being created HIDDEN (auto-start / systemd boot instance
    /// launched with --background/--minimized — no window at boot). A manual user
    /// launch sends a SHOW signal and the lazy GUI path clears this before starting
    /// Avalonia, so the window appears only when the user actually opens the app.
    /// </summary>
    public static bool LaunchedHidden { get; set; }

    /// <summary>
    /// True while an account removal is waiting for a GUI to exist.
    ///
    /// <c>client --remove-account</c> on a machine whose tracker runs headless
    /// (--background under systemd / auto-start) asks the running instance to remove
    /// the account. Program.cs sets this flag and starts the lazy GUI; the GUI's boot
    /// consumes it before the window is created, so the tool opens directly on the
    /// sign-in page instead of briefly rendering the removed employee's dashboard.
    /// </summary>
    public static bool PendingRemoveAccount { get; set; }

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

            // ─── Terms agent mode: ONLY the standalone terms window ───
            // A completely separate app surface: neutral-styled TermsWindow bound to
            // TermsViewModel from the agent's own DI container. No MainViewModel, no
            // shell, no tray, no collector. ShutdownMode=OnMainWindowClose so the
            // process exits when the terms window closes.
            if (TermsAgentMode)
            {
                var termsVm = ServiceProvider.GetRequiredService<client.ViewModels.TermsViewModel>();
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

                var agentWindow = new TermsWindow { DataContext = termsVm };
                desktop.MainWindow = agentWindow;

                // Refuse dismissal while terms are still pending.
                agentWindow.Closing += (s, e) =>
                {
                    if (termsVm.TotalCount > 0 && termsVm.CurrentTerm != null && !AllowShutdown)
                    {
                        e.Cancel = true;
                    }
                };

                // Flow complete (last pending term accepted → queue empty): close the
                // window. (2026-09-16 bug: this subscription was missing, so the modal
                // stayed open forever after the final acceptance.)
                termsVm.Done += () => Dispatcher.UIThread.Post(() =>
                {
                    try { agentWindow.Close(); }
                    catch { /* already closing/closed */ }
                });

                // Decisive exit — the agent's job ends with its window. The agent holds
                // NO unsaved state (consents are flushed synchronously at accept time),
                // so once the window is gone the process has nothing left to do.
                // Environment.Exit(0) is deliberate: ShutdownMode.OnMainWindowClose
                // proved unreliable here — the window closed but
                // StartWithClassicDesktopLifetime never returned, leaving a zombie
                // windowless agent process (2026-09-16). Exit also covers the
                // empty-queue race where the window closes during initialization.
                void ExitAgent()
                {
                    Environment.Exit(0);
                }
                agentWindow.Closed += (s, e) => ExitAgent();

                agentWindow.Show();
                _ = termsVm.LoadAsync();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();

            // Initialize async (check existing login state from SQLite)
            Task.Run(async () =>
            {
                await viewModel.InitializeAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();

            // ─── Pending account removal (client --remove-account) ───
            // Wipe this machine's local account and forget the employee (see
            // MainViewModel.RemoveAccountAsync). Called from here — before the window
            // exists, so a removal request that STARTED this GUI (headless tracker →
            // lazy GUI) opens straight on the sign-in page with no flash of the removed
            // employee's dashboard — and from the single-instance handler below, for a
            // GUI that is already live. InitializeAsync has restored the session by this
            // point; the removal undoes it.
            async Task RemoveLocalAccountAsync()
            {
                try
                {
                    await viewModel.RemoveAccountAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[client] Account removal failed: {ex.Message}");
                }
            }

            if (PendingRemoveAccount)
            {
                PendingRemoveAccount = false;
                Task.Run(RemoveLocalAccountAsync).GetAwaiter().GetResult();
            }

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
                    // Time and Attendance (Phase 1, finalplan section 2.6 / BUG-3 fix):
                    // emit a ui_hidden event BEFORE the window goes to the tray. The
                    // dashboard can now distinguish "in tray" from "actively using
                    // the app" without waiting for a heartbeat gap. The IEventRecorder
                    // is DI-registered as a singleton, so we resolve it from the
                    // ServiceProvider that Program.cs set in App.ServiceProvider.
                    try
                    {
                        var recorder = ServiceProvider?.GetService<client.Core.Abstractions.IEventRecorder>();
                        if (recorder != null)
                        {
                            _ = recorder.RecordAsync(client.Core.Models.SessionEventTypes.UiHidden);
                        }
                    }
                    catch
                    {
                        // Best-effort: telemetry must never break a window close.
                    }
                    mainWindow.Hide();
                }
            };

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var trayIcon = new Avalonia.Controls.TrayIcon
            {
                Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://client/Assets/avalonia-logo.ico"))),
                ToolTipText = AppInfo.DisplayName
            };

            var showItem = new Avalonia.Controls.NativeMenuItem($"Show {AppInfo.DisplayName}");
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
            //
            // Brings the window to the front. Shared by the SHOW signal and the
            // account-removal signal below.
            void ShowWindow()
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
            }

            SingleInstanceService.OnShowRequested = () => Dispatcher.UIThread.Post(ShowWindow);

            // ─── Account removal (client --remove-account) ───
            // This process owns both the SQLite connection and the window, so it
            // performs the removal in place: wipe every local row, stop collecting, and
            // land the UI on the sign-in page. The requesting CLI process then exits —
            // it must NOT start a second tracker. Removing the account here (rather than
            // in the CLI, after stopping the tracker) is also what keeps the Linux
            // systemd unit's Restart=always from racing the mutex with a restarted,
            // still-logged-in tracker.
            SingleInstanceService.OnRemoveAccountRequested = () => Dispatcher.UIThread.Post(async () =>
            {
                await RemoveLocalAccountAsync();
                ShowWindow();
            });

            SingleInstanceService.OnProtectedUninstallRequested = code => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var svc = ServiceProvider?.GetService<ProtectedUninstallService>();
                    if (svc is null)
                    {
                        Console.Error.WriteLine("[client] ProtectedUninstallService not registered");
                        return;
                    }
                    var ok = await svc.RunAsync(code, "both", CancellationToken.None);
                    Console.WriteLine(ok
                        ? "Protected uninstall complete."
                        : "Protected uninstall failed — check the removal code.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[client] Protected uninstall failed: {ex.Message}");
                }
                ShowWindow();
            });

            // A removal request that arrived between the boot-time check above and the
            // handler installation just now would otherwise be dropped (the flag had not
            // been consumed yet and no handler existed) — re-check and run it.
            if (PendingRemoveAccount)
            {
                PendingRemoveAccount = false;
                Dispatcher.UIThread.Post(async () =>
                {
                    await RemoveLocalAccountAsync();
                    ShowWindow();
                });
            }

            // Show the window only for a real user launch. Auto-start / systemd boot
            // instances (--background / --minimized) start hidden (or fully headless),
            // and the lazy-GUI path in Program.cs clears LaunchedHidden before creating
            // the window so a manual launch always brings the GUI up.
            if (!LaunchedHidden)
            {
                mainWindow.Show();
            }

            // ─── Terms & Conditions (2026-09-16) ───
            // The tracker GUI never shows terms itself: the standalone terms-agent
            // process (client.exe --terms, instance 3) owns the acceptance flow. The
            // MainViewModel spawns the agent when pending terms exist — its DI reaches
            // TermsService, whose ReadyToSpawn logic is wired through TermsViewModel.
            // No tracker-side window, no tracker-side close guard.
        }

        base.OnFrameworkInitializationCompleted();
    }
}