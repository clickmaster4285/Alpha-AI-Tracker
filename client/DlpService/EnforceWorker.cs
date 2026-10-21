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
    private DateTimeOffset _lastAccessDeniedLog = DateTimeOffset.MinValue;
    private bool _accessDeniedAlertQueued;

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
            _accessDeniedAlertQueued = false;
            await _store.AuditAsync("enforce_reload", $"version={version} hash={hash}", ct);
            _logger.LogInformation("EnforceWorker loaded policy version {Version}", version);
            if (wantUsbBlock)
                _logger.LogInformation("Policy requests USB mass-storage BLOCK");
            else if (version == 0)
                _logger.LogInformation(
                    "No published DLP policy yet (version 0). Create a USB rule, Publish on web, and ensure DeviceAuth token is valid.");
        }

        _drives.SetUsbBlocked(wantUsbBlock);

        // Do not touch the registry just to "confirm allow" on cold start with no block rules.
        if (!wantUsbBlock && _lastUsbBlock != true)
        {
            _lastUsbBlock = false;
            return;
        }

        if (_lastUsbBlock == wantUsbBlock)
            return;

        var result = UsbStorEnforcer.Apply(wantUsbBlock);
        await _store.AuditAsync(
            "usbstor_apply",
            $"block={wantUsbBlock} changed={result.Changed} detail={result.Detail} prev={result.PreviousStart}",
            ct);

        if (result.Detail == "access_denied_need_admin")
        {
            // Throttle spam — still retry so elevation mid-run can succeed.
            if (DateTimeOffset.UtcNow - _lastAccessDeniedLog > TimeSpan.FromMinutes(2))
            {
                _lastAccessDeniedLog = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "USBSTOR enforce needs Administrator / LocalSystem. " +
                    "Right-click AlphaAITracker.Service.exe → Run as administrator, or install as a Windows Service.");
            }

            if (!_accessDeniedAlertQueued && wantUsbBlock)
            {
                _accessDeniedAlertQueued = true;
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
            }
            return;
        }

        _lastUsbBlock = wantUsbBlock;
        _accessDeniedAlertQueued = false;
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
