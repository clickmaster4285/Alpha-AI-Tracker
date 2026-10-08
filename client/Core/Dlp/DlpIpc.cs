using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Core.Dlp;

/// <summary>
/// Local IPC between the main tracker (publisher) and the --dlp agent (listener).
/// Windows: named pipe. Linux/macOS: Unix domain socket under the user data dir.
/// </summary>
public static class DlpIpcPaths
{
    public const string DefaultPipeName = "AlphaAITracker-dlp";

    public static string ResolveUserDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "alpha-ai-tracker");
    }

    public static string ResolveSocketPath(string? configuredName)
    {
        var name = string.IsNullOrWhiteSpace(configuredName) ? "dlp-agent.sock" : configuredName.Trim();
        if (Path.IsPathRooted(name)) return name;
        return Path.Combine(ResolveUserDataDir(), name);
    }

    public static string ResolvePipeName(string? configuredName)
    {
        if (string.IsNullOrWhiteSpace(configuredName)) return DefaultPipeName;
        var n = configuredName.Trim();
        // If the config looks like a socket filename, fall back to the default pipe.
        if (n.Contains('/') || n.Contains('\\') || n.EndsWith(".sock", StringComparison.OrdinalIgnoreCase))
            return DefaultPipeName;
        return n;
    }
}

public sealed class DlpIpcPublisher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _pipeName;
    private readonly string _socketPath;
    private readonly ILogger<DlpIpcPublisher>? _logger;
    private readonly Channel<DlpIpcEvent> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drain;

    public DlpIpcPublisher(string? ipcName, ILogger<DlpIpcPublisher>? logger = null)
    {
        _pipeName = DlpIpcPaths.ResolvePipeName(ipcName);
        _socketPath = DlpIpcPaths.ResolveSocketPath(ipcName);
        _logger = logger;
        // Single consumer — named pipe allows only 1 server instance; concurrent
        // ConnectAsync calls (e.g. 3 PnP rows for one USB stick) race and time out.
        _queue = Channel.CreateUnbounded<DlpIpcEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _drain = Task.Run(() => DrainAsync(_cts.Token));
    }

    public void PublishFireAndForget(DlpIpcEvent evt)
    {
        if (!_queue.Writer.TryWrite(evt))
            _logger?.LogWarning("DlpIpcPublisher: queue closed; dropped {Type}", evt.Type);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await PublishAsync(evt, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "DlpIpcPublisher: publish failed for {Type} (agent may be down)", evt.Type);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task PublishAsync(DlpIpcEvent evt, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(evt, JsonOpts) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        if (OperatingSystem.IsWindows())
        {
            await using var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, ct);
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
            return;
        }

        if (!File.Exists(_socketPath)) return;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
        await socket.SendAsync(bytes, SocketFlags.None, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { await _drain; } catch { }
        _cts.Dispose();
    }
}

public sealed class DlpIpcListener : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _pipeName;
    private readonly string _socketPath;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Socket? _listenSocket;

    public event Action<DlpIpcEvent>? EventReceived;

    public DlpIpcListener(string? ipcName, ILogger logger)
    {
        _pipeName = DlpIpcPaths.ResolvePipeName(ipcName);
        _socketPath = DlpIpcPaths.ResolveSocketPath(ipcName);
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            await RunWindowsAsync(ct);
        else
            await RunUnixAsync(ct);
    }

    private async Task RunWindowsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                    Dispatch(line);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DlpIpcListener: pipe accept loop error");
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private async Task RunUnixAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
            if (File.Exists(_socketPath))
            {
                try { File.Delete(_socketPath); } catch { /* stale */ }
            }

            _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listenSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listenSocket.Listen(8);

            while (!ct.IsCancellationRequested)
            {
                var client = await _listenSocket.AcceptAsync(ct);
                _ = HandleUnixClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DlpIpcListener: unix socket failed");
        }
    }

    private async Task HandleUnixClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
                Dispatch(line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DlpIpcListener: client read failed");
        }
    }

    private void Dispatch(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            var evt = JsonSerializer.Deserialize<DlpIpcEvent>(line, JsonOpts);
            if (evt != null && !string.IsNullOrWhiteSpace(evt.Type))
                EventReceived?.Invoke(evt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DlpIpcListener: bad JSON line");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listenSocket?.Dispose(); } catch { }
        if (_loop != null)
        {
            try { await _loop; } catch { }
        }
        if (!OperatingSystem.IsWindows() && File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); } catch { }
        }
        _cts?.Dispose();
    }
}
