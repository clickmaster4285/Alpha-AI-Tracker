using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services;

/// <summary>
/// The single implementation of the account-removal routine (R4/R5/R6, 2026-10-19).
///
/// Before this service existed the "stop tracking → drain → wipe → kill siblings"
/// sequence was duplicated in four places in <c>Program.cs</c> / <c>MainViewModel.cs</c>
/// and none of them actually waited for the drain or told the server. This class owns
/// the canonical order:
///
///   1. <b>Stop collection</b> so nothing new is written while we drain / wipe
///      (R-WipeRace mitigation in the plan).
///   2. <b>Drain</b> every unsynced row to the server, bounded by
///      <see cref="AppConfig.RemoveAccountSyncTimeoutSec"/> so a huge backlog or an
///      offline machine can never block removal forever.
///   3. <b>Self-remove</b> — <c>POST /api/v1/devices/self-remove</c> under DeviceAuth,
///      which makes the server mark the employee <c>untracked</c>, this machine
///      <c>old</c>, and advance <c>last_track_at</c> (R5).
///   4. <b>Wipe local state</b> — empty the SQLite DB and delete <c>.machine-id</c> /
///      device id files, then reset the in-memory client id so the next login mints a
///      brand-new machine id (R7).
///
/// The same routine serves the server-initiated path: when a sync response carries
/// <c>{"action":"wipe_local"}</c> (an admin removed this device — R6), SyncService raises
/// <see cref="SyncService.OnServerRequestedWipe"/> and the listener calls
/// <see cref="RunAsync"/>. Idempotent: a second call after a completed wipe is a no-op.
/// </summary>
public class AccountRemovalService
{
    private readonly ILogStore _store;
    private readonly SyncService _sync;
    private readonly AppConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<AccountRemovalService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _wiped;

    /// <summary>True once the server acknowledged the self-remove call in this process.</summary>
    public bool ServerNotified { get; private set; }

    /// <summary>
    /// Raised after the local DB + identifiers have been wiped. Hosts listen to switch
    /// the UI back to the sign-in page (GUI) or to surface the sign-in window
    /// (background/headless mode).
    /// </summary>
    public event Action? WipeCompleted;

    public AccountRemovalService(
        ILogStore store,
        SyncService sync,
        AppConfig config,
        IServiceProvider services,
        ILogger<AccountRemovalService> logger)
    {
        _store = store;
        _sync = sync;
        _config = config;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full removal routine. Returns true when the server acknowledged the
    /// self-remove; the local wipe happens regardless (the operator asked for it).
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_wiped)
                return ServerNotified;

            StopCollection();

            // Bounded drain — the local wipe is what the operator asked for, so a slow
            // or unreachable server must never prevent it. Timeout ⇒ proceed.
            var timeout = TimeSpan.FromSeconds(Math.Max(5, _config.RemoveAccountSyncTimeoutSec));
            var drained = await _sync.WaitForDrainAsync(timeout, ct);
            _logger.LogInformation(
                "Account removal: final sync drain {State} (timeout {Timeout}s)",
                drained ? "completed" : "timed out — proceeding with local wipe",
                timeout.TotalSeconds);

            // R5: tell the server the machine is going away (idempotent).
            ServerNotified = await _sync.SelfRemoveAsync(ct);

            await WipeLocalAsync();
            _wiped = true;

            _logger.LogInformation("Account removal complete (serverNotified={Notified})", ServerNotified);

            try
            {
                WipeCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WipeCompleted handler threw");
            }

            return ServerNotified;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the collectors so no new rows are written during the wipe. Resolved lazily:
    /// the terms-only host (agent mode) does not register <see cref="LogCollectorService"/>.
    /// </summary>
    private void StopCollection()
    {
        try
        {
            _services.GetService<LogCollectorService>()?.StopTracking();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StopTracking during account removal failed");
        }
    }

    /// <summary>
    /// Empties the local database (every table, read from sqlite_master so future
    /// tables are covered automatically) and deletes the machine / device identifiers
    /// so the next login generates a fresh machine id (R7).
    ///
    /// Awaited (never blocked on): the GUI path runs on the Avalonia UI thread, and a
    /// blocking `.GetAwaiter().GetResult()` around the store's async command would
    /// deadlock if any continuation needed that thread.
    /// </summary>
    private async Task WipeLocalAsync()
    {
        try
        {
            await _store.WipeLocalAccountDataAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local database wipe failed");
        }

        try
        {
            AppConfig.RemoveMachineAndDeviceIdentifiers();
            _config.ResetMachineId();
            EncryptedConfigService.ResetCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing machine / device identifiers failed");
        }
    }
}
