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

### F. Web Blocker (domain DNS sink — P2)

1. Web `/web-blocker` → Add rule: domains e.g. `example.com`, action **Block** or **Alert + Block**, optional schedule window.
2. **Publish** → wait for policy pull on the PC.
3. Expect hosts `# AlphaAI-DLP-BEGIN` block with both `0.0.0.0` **and** `::` for the domain (+ www), and `netsh advfirewall` rules named `AlphaAI-DLP-*` for real A/AAAA IPs (DoH bypass).
4. Flush DNS happens on apply. Browser should fail; if Chrome Secure DNS is on, firewall IP rules are the effective block.
5. `/dlp-alerts` shows a `web_navigate` row when the sink was applied.
6. Delete/Publish clear → hosts + firewall owned rules removed; teardown clears too.
7. **Honesty:** URL **paths** are alert-match only — not hard-blocked at the network layer.

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
