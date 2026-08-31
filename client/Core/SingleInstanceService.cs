using System.IO.Pipes;

namespace client.Core;

/// <summary>
/// Cross-platform single-instance activation for Alpha AI Tracker.
///
/// The app is designed as ONE long-lived process (the "background" instance
/// launched by auto-start / systemd with --minimized / --background). That
/// process owns the global mutex, runs all collection/sync services, and keeps
/// a hidden window + tray icon alive so closing the window does not stop
/// tracking.
///
/// When the user launches the app a SECOND time (e.g. clicks the desktop
/// entry while the background instance is already running), the mutex is
/// already held. Instead of exiting silently — which made the GUI "open and
/// instantly close" — the second launch sends a SHOW signal to the running
/// instance via a named pipe. The running instance then brings its window to
/// the front. This lets the user open/close the GUI any number of times
/// without ever stopping the background tracking.
/// </summary>
public static class SingleInstanceService
{
    /// <summary>
    /// Machine-wide mutex name used by every launch path.
    ///
    /// On Unix, an unqualified .NET named mutex is scoped to the process session
    /// (<c>/tmp/.dotnet/shm/session&lt;sid&gt;</c>). A desktop launch and a systemd
    /// user service have different session IDs, so both could acquire the old
    /// mutex and run collectors against the same SQLite database. The
    /// <c>Global\</c> namespace maps them to the same machine-wide mutex backing
    /// object. Windows uses the same prefix to cross Terminal Services sessions.
    /// </summary>
    public static string MutexName => $@"Global\{AppInfo.AppMutex}";

    /// <summary>
    /// Pipe name used for cross-instance signalling. The same name must be
    /// used by the server (running instance) and the client (second launch).
    /// Lower-case, no path separators — safe on Windows named pipes and on
    /// Linux/macOS (where .NET maps it to a Unix domain socket path).
    /// </summary>
    public const string PipeName = "alpha-ai-tracker-activation";

    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Invoked on the pipe-listener thread when a second launch requests that
    /// the window be shown. Set by <see cref="App"/> once the main window is
    /// created. Implementations must marshal onto the UI thread (e.g. via
    /// <c>Dispatcher.UIThread.Post</c>).
    /// </summary>
    public static Action? OnShowRequested;

    /// <summary>
    /// Start the named-pipe server on the primary (mutex-owning) instance.
    /// Loops forever, accepting one connection at a time, until
    /// <see cref="StopServer"/> is called.
    /// </summary>
    public static void StartServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    private static async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // maxNumberOfServerInstances = 1: only the primary instance
                // should ever own this pipe.
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct);
                if (string.Equals(line, "SHOW", StringComparison.Ordinal))
                {
                    try { OnShowRequested?.Invoke(); }
                    catch { /* never let a UI callback error kill the listener */ }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error (e.g. a client connected then
                // disappeared). Recreate the server and keep listening.
                // Yield briefly to avoid a tight loop on persistent failures.
                try { await Task.Delay(250, ct); } catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Stop the pipe server. Called on application shutdown.
    /// </summary>
    public static void StopServer()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>
    /// Attempt to signal an already-running instance to show its window.
    /// Called by a second launch that failed to acquire the global mutex.
    /// Returns true if the signal was delivered.
    /// </summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);

            // 2s timeout: the running instance should already be listening.
            client.Connect(2000);

            using var writer = new StreamWriter(client);
            writer.WriteLine("SHOW");
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
