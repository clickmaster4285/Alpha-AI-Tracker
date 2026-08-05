using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace client.Core.Browser;

/// <summary>
/// Deterministic UUID derivation for runtime/profile/tab/window/journey identity.
/// All identity is generated client-side and stable across reboots (never titles/PIDs).
/// </summary>
public static class BrowserIdMapper
{
    private const string Namespace = "alpha-ai-tracker/browser";

    public static Guid For(string kind, string fingerprint) =>
        CreateDeterministic($"{Namespace}/{kind}/{fingerprint}");

    public static Guid ForRuntime(string binaryPathFingerprint) => For("runtime", binaryPathFingerprint);

    public static Guid ForProfile(Guid runtimeId, string? profileDirOrName) =>
        For("profile", $"{runtimeId:N}/{profileDirOrName ?? "default"}");

    public static Guid ForTab(Guid runtimeId, string targetId) =>
        For("tab", $"{runtimeId:N}/{targetId}");

    public static Guid ForWindow(Guid runtimeId, string windowId) =>
        For("window", $"{runtimeId:N}/{windowId}");

    public static Guid ForJourney(Guid runtimeId, string targetId) =>
        For("journey", $"{runtimeId:N}/{targetId}");

    /// <summary>RFC 4122 version-3 (MD5) style deterministic Guid from a name.</summary>
    private static Guid CreateDeterministic(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }
}

/// <summary>
/// Debug port allocation. ALWAYS assigns the FIRST FREE port; the search always starts at
/// 30000 (BROWSER_DEBUG_PORT_START). Never a fixed per-runtime port — a stale process can
/// otherwise block the sequence. Leases are persisted so recovery can validate before reuse.
/// </summary>
public sealed class DebugPortManager
{
    private readonly int _startPort;
    private readonly Abstractions.IBrowserRuntimeStore? _store;

    public DebugPortManager(int startPort, Abstractions.IBrowserRuntimeStore? store)
    {
        _startPort = startPort <= 0 ? 30000 : startPort;
        _store = store;
    }

    public async Task<int> AllocateAsync(Guid runtimeId, CancellationToken ct)
    {
        if (_store != null)
        {
            var persisted = await _store.GetPortLeaseAsync(runtimeId, ct);
            if (persisted != null && await IsFreeAsync(persisted.Value, ct))
            {
                await _store.SetPortLeaseAsync(runtimeId, persisted.Value, ct);
                return persisted.Value;
            }
        }

        for (var port = _startPort; port < _startPort + 2000; port++)
        {
            if (await IsFreeAsync(port, ct))
            {
                if (_store != null)
                    await _store.SetPortLeaseAsync(runtimeId, port, ct);
                return port;
            }
        }
        throw new InvalidOperationException($"No free debug port found in range {_startPort}+");
    }

    public async Task ReleaseAsync(Guid runtimeId, CancellationToken ct)
    {
        if (_store != null)
            await _store.ClearPortLeaseAsync(runtimeId, ct);
    }

    private static Task<bool> IsFreeAsync(int port, CancellationToken ct)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return Task.FromResult(true);
        }
        catch (SocketException)
        {
            return Task.FromResult(false);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }
}
