using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Fast-path enforcer. P1: USBSTOR Start=4 when any usb_* block rule is published.
/// </summary>
public sealed class EnforceWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly RemovableDriveMonitor _drives;
    private readonly ILogger<EnforceWorker> _logger;
    private long _lastVersion = -1;
    private bool? _lastUsbBlock;

    public EnforceWorker(
        DlpConfig cfg,
        PolicyStore store,
        RemovableDriveMonitor drives,
        ILogger<EnforceWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _drives = drives;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("EnforceWorker parked (ALPHA_DLP_ENABLED=false)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation(
            "EnforceWorker running (usbBlock={Usb}, webBlock={Web})",
            _cfg.UsbBlock, _cfg.WebBlock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EnforceWorker tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        var (version, hash, bundleJson) = await _store.GetAppliedAsync(ct);
        var bundle = PolicyBundleParser.Parse(bundleJson);
        var wantUsbBlock = _cfg.UsbBlock && PolicyBundleParser.WantsUsbStorageBlock(bundle);

        if (version != _lastVersion)
        {
            _lastVersion = version;
            await _store.AuditAsync("enforce_reload", $"version={version} hash={hash}", ct);
            _logger.LogInformation("EnforceWorker loaded policy version {Version}", version);
        }

        if (_lastUsbBlock == wantUsbBlock)
        {
            _drives.SetUsbBlocked(wantUsbBlock);
            return;
        }

        var result = UsbStorEnforcer.Apply(wantUsbBlock);
        _drives.SetUsbBlocked(wantUsbBlock);
        await _store.AuditAsync(
            "usbstor_apply",
            $"block={wantUsbBlock} changed={result.Changed} detail={result.Detail} prev={result.PreviousStart}",
            ct);

        if (result.Detail == "access_denied_need_admin")
        {
            _logger.LogWarning(
                "USBSTOR enforce needs Administrator / LocalSystem (detail={Detail}). " +
                "Run elevated or install as Windows Service.", result.Detail);
            var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "usb_storage",
                rule?.Severity ?? "critical",
                "blocked_failed",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new { reason = result.Detail, desiredBlock = wantUsbBlock }),
                ct);
            // Do not flip _lastUsbBlock so we retry after elevation.
            return;
        }

        _lastUsbBlock = wantUsbBlock;
        _logger.LogInformation(
            "USBSTOR {State} (changed={Changed}, detail={Detail})",
            wantUsbBlock ? "BLOCK" : "ALLOW", result.Changed, result.Detail);

        if (result.Changed && wantUsbBlock)
        {
            var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "usb_storage",
                rule?.Severity ?? "critical",
                "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new
                {
                    action = "usbstor_disabled",
                    previousStart = result.PreviousStart,
                    desiredStart = result.DesiredStart,
                }),
                ct);
        }
    }
}
