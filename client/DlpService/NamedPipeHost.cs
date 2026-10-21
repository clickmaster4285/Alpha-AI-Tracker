using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Minimal named-pipe IPC host (\\.\pipe\AlphaAITracker.Dlp).
/// P0: GetPolicyVersion + Health. ProtectedStop/Uninstall arrive in P1.
/// </summary>
public sealed class NamedPipeHost : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly ILogger<NamedPipeHost> _logger;

    public NamedPipeHost(DlpConfig cfg, PolicyStore store, ILogger<NamedPipeHost> logger)
    {
        _cfg = cfg;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("NamedPipeHost parked (ALPHA_DLP_ENABLED=false)");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("NamedPipeHost parked (Windows-only)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation("Named pipe listening on {Pipe}", _cfg.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _cfg.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Named pipe accept/handle error");
                try { await Task.Delay(500, stoppingToken); } catch { /* ignore */ }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return;

        string response;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var op = doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() : "";
            response = op switch
            {
                "GetPolicyVersion" => await GetPolicyVersionJson(ct),
                "Health" => await HealthJson(ct),
                _ => JsonSerializer.Serialize(new { ok = false, error = "unknown_op" }),
            };
        }
        catch (Exception ex)
        {
            response = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        await writer.WriteLineAsync(response.AsMemory(), ct);
    }

    private async Task<string> GetPolicyVersionJson(CancellationToken ct)
    {
        var (version, hash, _) = await _store.GetAppliedAsync(ct);
        return JsonSerializer.Serialize(new { ok = true, policyVersion = version, hash });
    }

    private async Task<string> HealthJson(CancellationToken ct)
    {
        var (version, _, _) = await _store.GetAppliedAsync(ct);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            enabled = _cfg.Enabled,
            policyVersion = version,
            usbBlock = _cfg.UsbBlock,
            webBlock = _cfg.WebBlock,
        });
    }
}
