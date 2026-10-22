# Protected Uninstall — Root Cause & Fix Plan

> **Status:** Implemented on `feature_dlp` (ProtectedUninstallService + pipe AuthorizeTeardown + teardown.ok). Product roadmap: root [plan.md](../plan.md).  
> **Date:** 2026-10-22  
> **Machine evidence:** live PC after failed `--protected-uninstall` (progdata + `dotnetrunlog.txt`)

---

## 1. Observed failure (evidence)

Command:

```cmd
"C:\Program Files (x86)\Alpha AI Tracker\client.exe" --protected-uninstall --code "<code>" --no-pause
```

Console / progress stuck at:

```text
Removing DLP / unlock uninstall...
```

`%ProgramData%\AlphaAITracker\dlp\removal.progress` frozen at:

```text
phase=teardown
label=Removing DLP / unlock uninstall
at=2026-10-22T06:27:12.6826816Z
```

Same second: `teardown.ok` was written (`authorizedAt=2026-10-22T06:27:12.6856668Z`).

`dotnetrunlog.txt` shows:

1. Full DI host started for the CLI (BackgroundGuard, SyncService, HardwareDeviceWatcher, …).
2. Account wipe completed (`serverNotified=False`).
3. ~2s later hosted services still ran (PnP “New Volume” events) — teardown never logged completion / `phase=done`.
4. HKCU Run still had `AlphaAITracker` → `client.exe --background` after Apps & features removal of files.

`serverNotified=False` is **independent** of DLP teardown (self-remove HTTP); it does not block local wipe.

---

## 2. Execution path (CLI → hang)

```
Program.cs --protected-uninstall
  ├─ (mutex held) ApproveAndHandoff → kill other client.exe → TakeOver
  ├─ ApproveAsync (or skip if coldCodeAlreadyApproved)
  ├─ Host.StartAsync  ← starts BackgroundGuard + collectors
  └─ ProtectedUninstallService.RunPipelineAsync
       ├─ AccountRemovalService.RunAsync   (stop → sync → self-remove → wipe)  ✓
       └─ FinalizeTeardownAsync            ← HANG / false success here
            ├─ WriteTeardownOkLocal        ✓ (progress still "teardown")
            ├─ DlpPipeClient.ApplyTeardownGateAsync  (result IGNORED)
            └─ WindowsTeardownCleanup.RestoreUsbAndRemoveDlpService
                 (schtasks /RU SYSTEM; errors swallowed; WaitForExit 20s×3)
```

Spinner in `CliConsole.RunWithSpinnerAsync` only stops when `RunPipelineAsync` returns. While teardown blocks, UI stays on “Removing DLP / unlock uninstall…”.

---

## 3. Root cause (evidence-based)

### RC1 — Success criterion is wrong (primary product bug)

`FinalizeTeardownAsync` returns success **only** if `teardown.ok` exists:

```csharp
_ = await DlpPipeClient.ApplyTeardownGateAsync(...);  // ignored
WindowsTeardownCleanup.RestoreUsbAndRemoveDlpService(); // failures swallowed
return File.Exists(TeardownOkPath);
```

Writing the gate file is **authorization**, not proof that:

- DLP unlocked ACL  
- USBSTOR Start=3  
- `AlphaAITracker.Service` was stopped and **deleted**  
- Auto-start / maint tasks were cleared  

So account wipe can succeed while services / USB block remain, or the CLI can report success after a no-op cleanup.

### RC2 — SYSTEM cleanup from a non-elevated CLI is unreliable and can hang

`WindowsTeardownCleanup` creates `schtasks /RU SYSTEM`. This agent shell is **not elevated** (`net session` → error 5; Administrators SID is deny-only). Typical outcomes:

- Access denied (quick fail) → catch → best-effort `reg.exe` HKLM also fails → **USB stays blocked, service stays**  
- Or `schtasks` waits on credentials with redirected stdin → **`WaitForExit(20_000)` loops / appears stuck**; process is **not killed** on timeout  

`/TR` quoting (`cmd.exe /c ""path""`) is fragile vs the working Inno `StopDeleteDlpServiceAsSystem` pattern.

### RC3 — Named-pipe unlock is not hard-bounded against hung Connect/Read

`DlpPipeClient` uses `CancelAfter` on `ConnectAsync` / `ReadLineAsync`. If cancellation is slow or ignored while the service is ACL-stuck / dying, the CLI blocks past the intended 2s+5s budget with no process-level kill and no fallback verification.

### RC4 — Dependency order is correct but incomplete

Correct order (keep):

1. Verify removal code  
2. Stop collection + drain + self-remove + wipe  
3. Write `teardown.ok`  
4. **Pipe `ApplyTeardownGate`** (SYSTEM: restore USBSTOR, open service DACL, stop service)  
5. **Then** delete service registration (needs open DACL + admin/SYSTEM)  
6. Clear auto-start / maint tasks  
7. **Verify** postconditions  

Missing today: (5) reliable elevated delete, (6) auto-start removal, (7) verification, and honest exit codes.

### RC5 — CLI host keeps BackgroundGuard alive during teardown

Cold-start protected-uninstall calls `host.StartAsync()` with `BackgroundGuardService` registered. Log: “Auto-start already configured — will watchdog”. Guard can **reinstall** the Run key if teardown removes it, fighting uninstall. Collectors keep running after wipe (noise + restart risk).

### RC6 — `serverNotified=False` (separate)

Self-remove did not confirm; local wipe still ran. Report separately; do not treat as teardown failure unless product policy changes.

---

## 4. Affected files

| File | Role |
|------|------|
| `client/Services/ProtectedUninstallService.cs` | Pipeline; false success; progress labels |
| `client/Services/WindowsTeardownCleanup.cs` | SYSTEM schtasks cleanup; silent fail / hang |
| `client/Services/DlpPipeClient.cs` | Pipe timeouts |
| `client/Services/AccountRemovalService.cs` | Account wipe (keep contract) |
| `client/Program.cs` | CLI host starts full service set |
| `client/Services/BackgroundGuardService.cs` / `AutoStartService.cs` | Watchdog vs teardown |
| `client/DlpService/NamedPipeHost.cs` | ApplyTeardownGate (mostly OK: reply then stop) |
| `client/publish/installer-windows.iss` | Reference SYSTEM delete pattern |
| `client/Services/CliConsole.cs` | Spinner stuck until work returns |

---

## 5. Implementation strategy

1. **`TeardownReport`** — structured result: gateWritten, pipeUnlock, usbRestored, dlpServiceGone, autoStartCleared, pendingElevatedCleanup, errors[].  
2. **Hard-bounded pipe** — `Task.WhenAny` + dispose client on deadline; never ignore result.  
3. **Rewrite Windows teardown** — mirror Inno: restore USBSTOR, clear failure actions, SYSTEM (or elevated) stop/delete/taskkill; kill helper processes on timeout; log exit codes; **verify** `sc query` → 1060 and USBSTOR Start=3.  
4. **Elevation** — if not admin after pipe unlock, launch one-shot elevated cleanup (`runas` / scheduled task only when creatable) or report **partial failure / cleanup pending** — never full success.  
5. **Disable auto-start** early in protected-uninstall; **do not register BackgroundGuard** (and park non-essential hosted services) for `--protected-uninstall` / `--remove-account`.  
6. **Stage logging** — `[uninstall] …` with op, duration, service state before/after.  
7. **Exit codes** — 0 = verified full local uninstall; 2 = partial (account wiped, DLP/service pending); 1 = auth/hard failure.  
8. **Tests** — unit tests for report/timeouts/mocks; Windows integration tests for verify helpers where possible.  
9. **Build → install → E2E** on this PC per phases 6–13.

---

## 6. Risks

| Risk | Mitigation |
|------|------------|
| UAC prompt required for delete | Detect; ask operator; report pending if declined |
| Service delete pending reboot | Detect STOP_PENDING / marked for delete; report honestly |
| Pipe service already dead | Skip unlock; still run elevated cleanup + verify |
| Breaking remove-account contract | Keep services installed for `--remove-account`; only protected-uninstall deletes DLP |

---

## 7. Test plan & acceptance

- Invalid code → exit 1, no wipe  
- Valid protected-uninstall → exit 0 only if `AlphaAITracker.Service` absent **and** USBSTOR Start=3 **and** auto-start cleared  
- Partial (no elevation) → exit 2, clear remaining steps  
- Pipe timeout → cleanup path still attempted; no infinite spinner  
- Idempotent retry after partial  
- `--remove-account` does not delete DLP service  
- Installer build contains new client + Service binaries  

---

## 8. Acceptance criteria (final)

- [ ] Root cause documented (this file)  
- [ ] Implementation matches strategy  
- [ ] Automated tests green for changed logic  
- [ ] Installer built and installed on this PC  
- [ ] Login + tracking smoke  
- [ ] `remove-account` verified  
- [ ] `protected-uninstall` verified with independent `sc query` / USBSTOR / Run key checks  
- [ ] Final status PASS / PARTIAL / BLOCKED / FAIL with evidence  

---

## 9. Implementation log

### 2026-10-22 — Fix landed (Phases 2–6)

**Code**
- `TeardownReport` + exit codes 0 / 2 / 1 (`TeardownReport.cs`)
- `FinalizeTeardownDetailedAsync` verifies USBSTOR Start=3 + service absent; no longer succeeds on `teardown.ok` alone
- `DlpPipeClient`: hard `Task.WhenAny` deadlines + dispose on timeout
- `WindowsTeardownCleanup`: kill hung helpers, close stdin (no password hang), log steps, verify
- CLI uninstall skips BackgroundGuard / stream / browser / hardware watchers; `SuppressWatchdog` for in-process path
- Auto-start disabled early in protected-uninstall
- Stage logs prefixed `[uninstall]`

**Tests:** `client/client.Tests` — 11 passed (Release)

**Build:** `client/VERSION` → **2.0.3**  
Installer: `client/installers/AlphaAITracker-Setup-2.0.3.exe` (ISCC success)

**Install on this PC:** Completed (silent). Verified:
- `DisplayVersion=2.0.3`, `client.exe` ProductVersion `2.0.3+…`
- Service registry `AlphaAITracker.Service` Start=2, process running
- Installed `client.dll` contains `[uninstall]`, `connect_timeout`, `SuppressWatchdog`
- USBSTOR Start=**4** (DLP enforcing — expected after install)
- **No `device.auth`** — login required before remove-account / protected-uninstall E2E

### 2026-10-22 — Hang at "Unlocking DLP service…" (2.0.4)

**Evidence (operator):** CLI froze after gate write; Apps & features → UAC → Error 32 on `unins000.dat` (client still locking files) → retry → confirm → uninstall eventually succeeded.

**Cause:** DLP `CompleteTeardown` ran USBSTOR + `sc sdset` *before* writing the pipe reply. Client read-timeout + `DisposeAsync` waited on the service while the service held the pipe → deadlock. Stuck `client.exe` caused Error 32.

**Fix (2.0.4):**
- Service: reply `ok=true` first; USB/ACL/stop on background thread
- Client: never await hung pipe Dispose; hard 8s overall ApplyTeardownGate budget; abandon and continue SYSTEM cleanup
- Quiet Warning-level logs on CLI removal; bounded `host.StopAsync` (8s)
- Inno: kill tracker/service before opening uninstall data; longer settle to avoid Error 32

### Remaining E2E

Reinstall **2.0.4**, login, fresh removal code, re-test `--protected-uninstall` (must exit; no permanent spinner). One Apps & features confirm is normal; Error 32 / double-stuck must not recur.
