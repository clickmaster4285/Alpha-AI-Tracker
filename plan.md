# Plan — Security & DLP (Windows Production)

> **Status:** P0 complete · P1 USB + service install + protected removal (code / ACL / uninstall gate) shipped  
> **Scope:** Windows-only endpoint DLP + Shadow IT + hardened agent lifecycle  
> **Out of scope (v1–v3):** Linux/macOS enforcement, kernel minifilter (deferred to P6), TLS inspection / ChatGPT surgical upload block (deferred to P7)  
> **Related UI mocks:** `/dlp-rules`, `/dlp-alerts`, `/shadow-it` (static today)  
> **Last updated:** 2026-10-21

---

## 0. One-sentence goal

Ship a **production-grade Windows Security & DLP stack**: admin configures rules on the web → a **SYSTEM Windows Service** enforces USB / site / install controls → a **DLP Alert Generator** queues incidents → the web **DLP Alerts** inbox and **Shadow IT** catalog stay live — and the agent can only be **stopped or uninstalled with a server-issued removal code**.

---

## 1. Product modules (what the three pages become)

| Web route | Module | Job |
|-----------|--------|-----|
| `/dlp-rules` | **DLP Rules** | Policy authoring: trigger, match pattern, action (Alert / Block / Alert+Block), severity, scope (dept / all) |
| `/dlp-alerts` | **DLP Alerts** | Incident inbox: triage Open → Investigating → Resolved / False Positive |
| `/shadow-it` | **Shadow IT** | App/SaaS inventory: Approved / Unauthorized / Under Review → Approve / Block / Flag |

**Relationship:**

```
DLP Rules  ──policy bundle──►  Blocker (enforce) + Alert Generator (match)
                                      │
                                      ▼
                               DLP Alerts (sync)

Installed apps / sites  ──►  Shadow IT catalog
Unauthorized + Block    ──►  can publish install/web deny rules into next policy bundle
```

---

## 2. Non-negotiable architecture decisions

### 2.1 Windows only (for enforcement)

- Detection/telemetry on Linux may continue as today; **block / protected service / WFP / USBSTOR / AppLocker** are **Windows-only**.
- Feature flags: `ALPHA_DLP_ENABLED` (client), server ignores enforce endpoints for non-Windows devices.

### 2.2 Do **not** build a “second firewall”

| Channel | Correct mechanism | Windows Firewall role |
|---------|-------------------|------------------------|
| USB file copy / mass storage | USBSTOR policy / later minifilter | **None** |
| Software install/uninstall | Inventory + AppLocker/WDAC | **None** |
| Browse / cloud domains | WFP API and/or DNS sink (named `AlphaAI-DLP-*` rules) | Optional blunt tool only — not file-aware |

Windows Firewall / WFP may be **configured by our service**; we do **not** invent a separate firewall product.

### 2.3 Process model (production)

**Do not** put hard blocking inside the Avalonia user process alone.

```
┌─────────────────────────────────────────────────────────────────┐
│  AlphaAITracker.Service.exe     Windows Service (LocalSystem)   │
│  ├── EnforceWorker   (Blocker)                                  │
│  ├── AlertWorker     (DLP Alert Generator)                      │
│  ├── PolicySync      (pull versioned bundles)                   │
│  ├── AlertSync       (batch POST alerts)                        │
│  └── Watchdog / Health heartbeat                                │
└────────────────────────────┬────────────────────────────────────┘
                             │ named pipe + policy files
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│  AlphaAITracker.exe             User-session helper             │
│  ├── Existing collectors (a11y, sessions, Explorer, stream…)    │
│  ├── Emits desktop-only signals to service (optional)           │
│  └── GUI / login / terms (Session 0 cannot host this)            │
└─────────────────────────────────────────────────────────────────┘
```

| Process | Privilege | Mutex |
|---------|-----------|-------|
| Service | `LocalSystem` | `Global\AlphaAITracker.Service` |
| User helper | Logged-on user | Existing `Global\<APP_MUTEX>` |

**Why SYSTEM service for the core:**

- Enforce USB/WFP without UAC prompts mid-flight  
- Survive user logoff  
- ACL-harden stop/uninstall  
- Crash-isolate DLP from live-stream / GUI  

**Why keep a user helper:** Session 0 cannot do interactive desktop a11y, foreground window, or Avalonia UI correctly.

### 2.4 Protected stop / uninstall (authorized removal)

Marketing-accurate language: **protected uninstall for standard users** — not “impossible for Administrators offline.”

| Actor | Can stop/uninstall? |
|-------|---------------------|
| Standard employee | **No** (service DACL + no ARP / gated uninstaller) |
| Local Admin without company code | Discouraged (ACLs + no easy UI); offline/Safe Mode still possible |
| Company-authorized operator | **Yes** — one-time **removal code** from server |

**Removal flow (extends existing `AccountRemovalService` / `POST /devices/self-remove`):**

```
Admin issues removal code on web (machine-scoped)
  → server stores SHA-256 hash only, returns raw code once (+ expiry)
Operator on PC: --protected-uninstall --code <raw>
  → Service validates online (or short-lived signed offline voucher)
  → drain sync → POST /devices/self-remove
  → release service ACLs → stop service → allow/file uninstall
  → mark code used (replay fails)
```

Local SQLite holds `machine_id` + identity; **do not** store a forever plaintext uninstall password.

Also keep server-initiated path: admin remove device → sync answers `wipe_local` → same protected teardown after drain.

---

## 3. Current state (baseline)

| Area | Today | Gap |
|------|-------|-----|
| Web `/dlp-*` / `/shadow-it` | Static mocks, local `useState` | No API |
| Client USB | `HardwareDeviceWatcherService` plug/unplug; `FileSystemEventWatcher` on Removable | Detect only — no block |
| Client apps | Installed software inventory + sync | No allow/deny enforce |
| Client web | Browser tab / domain in journeys | No domain block |
| Agent lifecycle | User `--background` + autostart; `--remove-account` / self-remove | Not SYSTEM service; not ACL-locked |
| Policy pull | `ScheduleCacheService` pattern exists | No DLP policy bundle |

---

## 4. Target data model

### 4.1 Server (new migrations — append-only)

**`dlp_rules`** — editable current rules (admin CRUD).

| Column | Notes |
|--------|-------|
| `id` UUID PK | |
| `name`, `enabled` | |
| `trigger` | `usb_storage` \| `usb_file` \| `web_navigate` \| `software_install` \| `software_uninstall` \| `cloud_sync_folder` |
| `match_json` | extensions, domains, publishers, keywords |
| `action` | `alert` \| `block` \| `alert_block` |
| `severity` | `critical` \| `high` \| `medium` \| `low` |
| `scope_json` | `{ "all": true }` or department / employee ids |
| `created_at` / `updated_at` / `deleted_at` | soft delete |

**`dlp_policy_versions`** — immutable published bundles (fleet scale).

| Column | Notes |
|--------|-------|
| `version` BIGSERIAL | monotonic |
| `bundle_json` | full compiled rules for a scope snapshot **or** global + client filters |
| `bundle_hash` | sha256 |
| `published_at`, `published_by` | audit |

Clients pull **by version**, not chatty per-rule polls.

**`dlp_alerts`**

| Column | Notes |
|--------|-------|
| `id` UUID (client-generated) | idempotent upsert |
| `rule_id`, `policy_version` | |
| `employee_id`, `machine_id` | |
| `type`, `severity`, `action_taken` | `alerted` \| `blocked` \| `blocked_failed` |
| `occurred_at`, `evidence_json` | path, process, deviceId, domain, app name |
| `status` | `open` \| `investigating` \| `resolved` \| `false_positive` |
| `assigned_to`, `notes` | web triage |
| `synced_at` | server arrival |

**`shadow_it_apps`** (+ optional `shadow_it_app_employees` junction)

| Column | Notes |
|--------|-------|
| catalog key | normalized app/domain name |
| `auth_status` | `approved` \| `unauthorized` \| `under_review` |
| `risk` | `high` \| `medium` \| `low` |
| `first_detected_at`, `user_count` | maintained by rollup job or on sync |
| `action` | `approve` \| `block` \| `flag` — block feeds next policy publish |

**`removal_challenges`**

| Column | Notes |
|--------|-------|
| `id` UUID | |
| `machine_id`, `employee_id` | |
| `token_hash` | sha256(raw); raw never stored |
| `expires_at`, `used_at` | single use |
| `issued_by`, `purpose` | `stop` \| `uninstall` \| `both` |

### 4.2 Client (service-local store)

Prefer **separate** SQLite under `%ProgramData%\AlphaAITracker\dlp\` (SYSTEM ACL), single-writer = service:

- `policy_store` (version, hash, bundle blob, applied_at)  
- `dlp_alerts_local` (`is_synced`, same shape as server)  
- `enforcement_audit` (what we applied: USBSTOR on/off, WFP rule ids)  
- `machine_identity` mirror / device token reference for DeviceAuth  

Do **not** multi-write the user tracker’s main SQLite from the SYSTEM service.

### 4.3 Policy bundle (wire format)

```json
{
  "policyVersion": 1842,
  "publishedAt": "2026-10-20T12:00:00Z",
  "hash": "sha256:…",
  "rules": [
    {
      "id": "…",
      "enabled": true,
      "trigger": "usb_storage",
      "match": { "extensions": [".xlsx", ".csv"] },
      "action": "block",
      "severity": "critical",
      "alert": true
    }
  ],
  "shadowDeny": {
    "domains": ["wetransfer.com"],
    "publishers": [],
    "appNames": ["Telegram"]
  }
}
```

Offline: **last good bundle stays enforced** until a newer version applies.

---

## 5. API surface (server)

All device endpoints: **DeviceAuth**. Admin endpoints: **JWT** + RBAC keys matching sidebar (`dlp-rules`, `dlp-alerts`, `shadow-it`) — seed catalog per **RBAC Nav → Catalog Parity Rule**.

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/api/v1/dlp/policy?sinceVersion=` | Device | Pull bundle (304 / empty if current) |
| POST | `/api/v1/dlp/alerts/sync` | Device | Batch upsert alerts; return accepted ids |
| POST | `/api/v1/dlp/health` | Device | Heartbeat: policyVersion, enforceOK, lastError |
| GET/POST/PATCH/DELETE | `/api/v1/dlp/rules` | JWT | Admin CRUD |
| POST | `/api/v1/dlp/rules/publish` | JWT | Compile → new `policy_versions` row |
| GET | `/api/v1/dlp/alerts` | JWT | Infinite-scroll list + filters |
| PATCH | `/api/v1/dlp/alerts/:id` | JWT | status / assignee / notes |
| GET/PATCH | `/api/v1/shadow-it/apps` | JWT | catalog + Approve/Block/Flag |
| POST | `/api/v1/devices/:machineId/removal-code` | JWT | Issue one-time code |
| POST | `/api/v1/devices/removal-verify` | Device | Validate code before teardown |

Reuse existing `POST /devices/self-remove` **after** removal-verify succeeds.

---

## 6. Client workers (the “two subprocesses”)

### 6.1 Blocker (`EnforceWorker`)

**Fast path only** — allow/deny in milliseconds; no heavy content scan.

| Capability | Phase | Mechanism |
|------------|-------|-----------|
| USB mass storage off | P1 | Registry / policy USBSTOR (coarse) |
| USB device allowlist | P5 | DeviceID regex allow |
| USB file extension block | P6 | Minifilter (signed `.sys`) |
| Domain navigate block | P2 | WFP filters + optional hosts/DNS helper; rules named `AlphaAI-DLP-*` |
| Install/uninstall detect | P3 | ETW / inventory diff (alert-heavy) |
| Install hard block | P4 | AppLocker or WDAC policy push |
| Cloud sync folder write | P6 | Minifilter on known sync roots |
| Tamper / self-protect | P1 | ACL service + install dir; deny stop without code |

On each enforce action → emit `EnforcementEvent` to AlertWorker (IPC).

### 6.2 DLP Alert Generator (`AlertWorker`)

**Slow path** — match, enrich, dedup, queue, sync.

```
Signals (USB plug, file on Removable, WFP block, install event, Shadow IT)
  → normalize Fact (schema versioned)
  → match compiled rule index
  → dedup window (e.g. 5 min: same rule + path/domain + machine)
  → INSERT dlp_alerts_local is_synced=0
  → batch sync (cap backoff like SyncService; guaranteed cadence ≤ 60s)
```

Alert JSON (stable contract) must include `policyVersion`, `ruleId`, `actionTaken`, `evidence`.

---

## 7. IPC & files

| Channel | Use |
|---------|-----|
| Named pipe `\\.\pipe\AlphaAITracker.Dlp` | Health, EnforcementEvent, ProtectedStop/Uninstall commands, GetPolicyVersion |
| `%ProgramData%\AlphaAITracker\dlp\policy.vN.json` | Applied bundle (ACL: SYSTEM) |
| Length-prefixed JSON (or protobuf later) | Version field on every message |

Authenticate pipe clients with a machine-local secret written at install (ACL’d).

---

## 8. Web implementation notes

Replace mocks with live APIs; keep UX intent of the three pages.

| Page | Must follow |
|------|-------------|
| All lists | **Web Infinite-Scroll Rule** (no Next/Previous) |
| Filters | **URL-Synced Filters Rule** (`useUrlQueryState`) |
| RBAC | Seed `dlp-rules`, `dlp-alerts`, `shadow-it` in `rbacCatalog` **and** sidebar `module` keys (already present) |
| Device Specs / employees | Unrelated; do not break machine lifecycle |

Optional: “Issue removal code” on employee device menu (next to Remove Current Device) — gated by admin permission.

---

## 9. Installer & packaging (Installer-Parity Rule)

Windows Inno / existing `client/publish` pipeline must:

1. Install `AlphaAITracker.Service.exe` as Automatic **LocalSystem** service  
2. SCM recovery: restart on failure  
3. ACL service + `%ProgramFiles%\…` + `%ProgramData%\AlphaAITracker`  
4. Start user helper at logon (Task Scheduler or service-triggered) with `--background`  
5. Gate uninstall: `InitializeUninstall` → require `--removal-code` validation via service  
6. Re-bake `config.enc` for new knobs (`ALPHA_DLP_*`, pipe names, intervals)  
7. Code-sign service + helper EXEs  

**Done means:** works from **installed** build, not only `dotnet run`.

---

## 10. Feature flags & config

| Key | Default | Meaning |
|-----|---------|---------|
| `ALPHA_DLP_ENABLED` | `false` until ship | Master switch for service workers |
| `ALPHA_DLP_POLICY_POLL_SEC` | `300` | Policy pull interval |
| `ALPHA_DLP_ALERT_SYNC_SEC` | `60` | Alert drain cadence |
| `ALPHA_DLP_USB_BLOCK` | `true` when enabled | Allow coarse USBSTOR path |
| `ALPHA_DLP_WEB_BLOCK` | `true` | Allow WFP domain path |
| `ALPHA_DLP_MAX_LOCAL_ALERTS` | `50000` | Queue ceiling |
| `ALPHA_DLP_DEDUP_SEC` | `300` | Alert dedup window |

Server: no special env beyond normal DB; optional `DLP_REMOVAL_CODE_TTL_MIN=60`.

---

## 11. Phased roadmap

### P0 — Spine (no user-visible blocks yet)

- [x] Migrations: rules, policy_versions, alerts, shadow_it, removal_challenges (`051_dlp_security.sql`)  
- [x] RBAC catalog seed for three modules (already in `rbacCatalog` → `security-dlp`)  
- [x] Device `GET /dlp/policy`, `POST /dlp/alerts/sync`, `POST /dlp/health` (+ admin CRUD/publish/alerts/shadow-it + removal-code)  
- [x] Windows Service project skeleton + named pipe + PolicyStore (`client/DlpService`)  
- [x] AlertWorker queue schema + sync drain  
- [x] Installer registers service (Inno + `install-service.ps1`; `service.env` enables workers)  
- [x] `go build` / `dotnet build` (DlpService) / `tsc` clean  
- [x] Web: live DLP Rules / Alerts / Shadow IT pages (API-backed; enforce still P1+)  

### P1 — USB coarse + protected lifecycle

- [x] EnforceWorker: USBSTOR disable when any active `usb_*` block rule in applied bundle (`UsbStorEnforcer`)  
- [x] Alerts on USB plug + blocked storage (`RemovableDriveMonitor` + enforce apply alerts)  
- [x] Lab proof: elevated `AlphaAITracker.Service.exe` pulls policy + blocks USB (token from `aat_verify.db`)  
- [x] `sc create` / installer registers service as LocalSystem + failure recovery; tracker writes `device.auth` for SYSTEM  
- [x] Service ACL locked (SY full / BA query-only) + Inno hard uninstall gate on `teardown.ok` (no break-glass; Alpha AI Tracker only)  
- [x] Removal code: web Issue + `--protected-uninstall --code` + service `AuthorizeTeardown` pipe  
- [x] Web: live DLP Alerts (read) + Rules publish MVP  

### P2 — Web domain block

- [ ] WFP (or equivalent) apply/remove `AlphaAI-DLP-*` filters from `web_navigate` / Shadow IT deny domains  
- [ ] Alerts on block events  
- [ ] Web Rules UI: domain patterns  

### P3 — Shadow IT live

- [ ] Derive catalog from installed apps + observed domains  
- [ ] Web Shadow IT Approve/Block/Flag  
- [ ] Block → include in next policy publish (`shadowDeny`)  
- [ ] Install/uninstall → alerts (detect-only)  

### P4 — Hard install control

- [ ] AppLocker or WDAC allowlist managed by service  
- [ ] Align with Shadow IT Approved publishers  

### P5 — USB device allowlist

- [ ] DeviceID regex allow; deny others while USB storage enabled for allowlisted only  

### P6 — True file DLP (optional major)

- [ ] Signed minifilter: block writes of matched extensions to Removable / sync folders  
- [ ] Separate driver package + WHQL/attestation plan  

### P7 — Advanced egress (optional)

- [ ] Browser upload / content classifiers / optional proxy  
- [ ] Honest limits documented for ChatGPT (site block ≠ upload block)  

---

## 12. Security, compliance, support

1. **Tokens:** hash at rest; single-use; expiry; audit who issued/used.  
2. **Break-glass:** IT runbook for Safe Mode / offline disk when PC is dead.  
3. **Signing:** all EXEs (and driver if P6).  
4. **AV:** document as enterprise agent; avoid rootkit-like tricks beyond standard service ACL.  
5. **Privacy:** evidence paths/domains only as needed; retention aligned with `RETENTION_DAYS`.  
6. **Employment policy:** agent lock-down assumes company-owned endpoints.  
7. **Honesty:** never claim “Admin cannot uninstall offline.”  

---

## 13. Testing plan

| Layer | Proof |
|-------|-------|
| Unit | Policy compile, match engine, dedup, token hash verify |
| Integration | DeviceAuth policy pull → apply → alert sync → JWT list |
| Installed Windows | Service starts at boot; standard user cannot stop; removal code uninstalls; USBSTOR toggles; WFP blocks test domain |
| Regression | Existing tracker sync, live stream, self-remove without DLP still works when `ALPHA_DLP_ENABLED=false` |
| RBAC | Role without `dlp-*` cannot see nav; `company_admin` can |

---

## 14. Explicit non-goals (until later phases)

- Linux/macOS Blocker service  
- Custom userspace “firewall” product  
- Relying on `FileSystemWatcher` alone for **Block** (too late — alert-only is OK)  
- Guaranteed ChatGPT **file upload** block via domain Firewall  
- Storing raw removal codes in SQLite long-term  

---

## 15. Suggested implementation order (first sprint)

1. Server migrations + policy publish + device pull  
2. Service skeleton + PolicyStore + health  
3. Alert queue + sync + web Alerts page (read-only live)  
4. P1 USBSTOR + removal code + installer ACL  
5. Web Rules CRUD + publish button  
6. Then P2 domains → P3 Shadow IT  

---

## 16. Success criteria (MVP = end of P1 + Alerts/Rules live)

- [ ] Admin creates rule → Publish → Windows lab PC pulls new `policyVersion` within poll interval  
- [ ] USB mass storage blocked when rule says Block; alert appears on `/dlp-alerts`  
- [ ] Standard user cannot stop service or uninstall via Apps  
- [ ] Admin issues removal code → `--protected-uninstall` drains, self-removes, uninstalls cleanly  
- [ ] `ALPHA_DLP_ENABLED=false` → behavior identical to today’s tracker  
- [ ] `go build` / `go vet` / `dotnet build` 0/0 / `npx tsc --noEmit` / installed Windows proof  

---

## 17. Doc updates when shipping

- [ ] `AGENTS.md` changelog + conventions (Device Lifecycle stays; add **DLP Policy Version Rule**, **Protected Removal Rule**)  
- [ ] `client/ARCHITECTURE.md` — service topology, IPC, ProgramData store  
- [ ] `server/ARCHITECTURE.md` — DLP routes + migrations  
- [ ] `web/ARCHITECTURE.md` — three live pages  
- [ ] `WORKFLOW.md` — installer steps for service + `config.enc` knobs  
- [ ] `TESTING.md` — lab playbook for USB / domain / removal code  

---

*End of plan. Implement phase-by-phase; do not start P6/P7 until P1–P3 are proven on installed Windows builds.*
