# Testing — live playbooks

Procedures that need a running server (and for DLP, an installed Windows agent). Unit/CI tests live next to the code; this file is for **lab proof**.

Companion: [plan.md](./plan.md) (DLP roadmap), [WORKFLOW.md](./WORKFLOW.md), [client/build.md](./client/build.md).

*Last audited: 2026-10-23*

---

## Security & DLP (Windows lab — P0+P1+P2 domain)

**Prereqs:** server with migrations 051/052 applied; web admin with `dlp-rules` / `dlp-alerts` / `web-blocker` grants; Windows PC with tracker + DLP service installed (Inno or `client/DlpService/install-service.ps1` elevated).

### A. Policy publish → service pull

1. Web `/dlp-rules` → create a rule: trigger `usb_storage`, action `block` (or `alert_block`), enabled.
2. Click **Publish** → note `policyVersion` in the response toast / network tab.
3. On the PC (as SYSTEM service): wait ≤ `ALPHA_DLP_POLICY_POLL_SEC` (installer lab often uses 60s), or restart the Windows service.
4. Confirm `%ProgramData%\<APP_MUTEX>\dlp\` has a policy blob / store row with that version (`GetPolicyVersion` via pipe or service logs).

### B. USBSTOR coarse block + alert

1. With a published `usb_storage` **block** rule and `ALPHA_DLP_USB_BLOCK=true`, plug a USB mass-storage stick.
2. Expect: Removable volumes unavailable / USBSTOR disabled (coarse — whole class, not per-file).
3. Web `/dlp-alerts` within ≤ `ALPHA_DLP_ALERT_SYNC_SEC`: new row (`actionTaken` `blocked` or `alerted`), `policyVersion` matches.
4. Open the row → detail dialog → change status/notes → **Save** → History shows `dlp_alert_events` entries. Status must **not** update on dropdown change alone.

### C. Protected uninstall (removal code)

1. Employees → device menu → **Issue Removal Code** (machine scoped). Copy the raw code (shown once).
2. Elevated or user session:
   ```powershell
   & "C:\Program Files\…\client.exe" --protected-uninstall --code '<paste>'
   ```
3. Expect: service unlocks ACL → stops → sync drain → `self-remove` → local wipe → `%ProgramData%\<APP_MUTEX>\dlp\teardown.ok`.
4. Apps & features uninstall succeeds. Replay the same code → verify fails. Without `teardown.ok`, Inno uninstall exits with an error.
5. Before unlock: Services.msc **Stop** on the DLP service fails for Administrators (query-only DACL).

### D. Master switch off

1. Set `ALPHA_DLP_ENABLED=false` in `service.env` (and/or bake `config.enc` for the helper), restart service.
2. Expect: workers parked; tracker sync / live stream / self-remove without DLP behave as pre-DLP; no USBSTOR enforcement from this product.

### E. RBAC

1. Role without `dlp-*` / `web-blocker` / `shadow-it` → sidebar entries hidden; direct URL → `/unauthorized`.
2. `company_admin` → Security & DLP pages (Alerts / Rules / Web Blocker / Shadow IT) reachable.

### F. Web Blocker (hosts + firewall + DNS pin — P2)

1. Web `/web-blocker` → Add rule: `youtube.com` with **Include related domains** (chips, not commas), action **Block** / **Alert + Block**, Status Active.
2. Save auto-publishes → wait for policy pull (~30–60s). Active/Inactive also auto-publishes.
3. Expect hosts `# AlphaAI-DLP-BEGIN` with `127.0.0.1` / `::1` per domain; `netsh` rules `AlphaAI-DLP-*` (apex) and soon `AlphaAI-DLP-PIN-*` (live CDN IPs).
4. Apply path: browser policies + firewall + TCP-kill + hosts + flushdns only — **never** winsock / `ipconfig /release` / NIC reset.
5. **Mid-session lab (no employee restart):** start YouTube playback → Publish/Activate → keep the same tab playing. `DnsBlockPinWorker` should see `rr*---sn-*.googlevideo.com` in DNS cache, pin those IPs, TCP-kill; playback should stall within a few seconds to ~1–2 minutes without closing Chrome or rebooting. Second browser should fail on navigate.
6. Confirm: `netsh advfirewall firewall show rule name=all | findstr AlphaAI-DLP-PIN` shows pinned rules after video DNS runs; service log / `enforcement_audit` may show `web_dns_pin`.
7. Active→Inactive clears hosts + owned FW + pins; Active again re-applies.
8. Teardown / clear policy removes owned hosts block + all `AlphaAI-DLP-*` rules.
9. Edit a rule → History dialog shows prior revisions (migration 054).

---

## Orphan `app-items` FK 23503 (server preflight)

Historical: items batch arriving before parent `app_sessions` used to 500 the whole batch. Server now preflights and returns 200 with a reduced `accepted` count; client keeps refused rows `is_synced=0` when `rejectedIds` is present.

**Steps (abbrev):**

1. With a DeviceAuth token, POST an `app-items/sync` batch whose `appSessionId` does not exist → 200, survivors only / rejected ids.
2. POST the parent `app-sessions/sync`, then re-POST the items → accepted.
3. Confirm no silent `is_synced=1` on refused rows (client ≥ 2026-09-09).

---

## App Usage aggregate (`GET /app-sessions/usage`)

See server smoke via curl: one row per `(appDisplayName, processName)`; web Duration = `lastClosedAt - firstOpenedAt`, not Σ row durations. Chevron expand uses `GET /app-sessions/usage/sessions`.
