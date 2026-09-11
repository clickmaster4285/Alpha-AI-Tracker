Let me read all the key files first before making any changes.

Viewed 035_fix_stranded_sessions.sql:1-8
Viewed session_lifecycle_sweep.go:1-221
Viewed new_schema_repo.go:1-80
Searched for "AggregateAppSessionsUsage"
Viewed page.tsx:1-342
Searched for "AppSessionUsageRow\|AppUsageRow\|has_open_session\|lastActiveAt\|lastClosedAt\|firstOpenedAt"
Viewed page.tsx:1-198
Searched for "AppUsageRow|has_open_session|lastActiveAt|lastClosedAt|firstOpenedAt|AppSessionUsage"
Searched for "AppUsageRow|hasOpenSession|lastActiveAt|lastClosedAt|firstOpenedAt"
Viewed new_schema_repo.go:791-950
Viewed new_schema_dto.go:270-295
Viewed new_schema_service.go:515-560
Viewed page.tsx:1-80

Good. I now have a full picture of what's already done vs what still needs to be done. Let me check the client shutdown path:

Searched for "CloseSessionsAndAppItemsAsync"
Searched for "ReconcileStaleSessionsOnBootAsync"
Viewed LogCollectorService.cs:1917-2000
Searched for "SessionEnding\|CloseSessionsAndAppItems\|ShutdownSentinel\|SessionEndReason"
Searched for "SessionEnding\|CloseSessionsAndAppItems\|ShutdownSentinel"
Searched for "SessionEnding"
Searched for "is_synced = 0\|isSynced = false\|IsSynced = false"
Viewed SystemEventWatcher.Windows.cs:1-141

Now I have a complete picture. Let me check what's already done vs what still needs doing:

**Already done (no changes needed):**
- ✅ Phase 0 migration `035_fix_stranded_sessions.sql` exists
- ✅ Phase 1.1 `BOOL_OR(status = 'ACTIVE' AND ended_at IS NULL)` — already in repo line 907
- ✅ Phase 1.2/1.3 `LastActiveAt` in struct + DTO + scan — already done
- ✅ Phase 1.4 per-row stagnation sweep — already in `session_lifecycle_sweep.go` lines 199-214
- ✅ Phase 2.1 expanded row Closed cell (STALE/OFFLINE → lastSyncAt) — already fixed at apps/page.tsx lines 268-272
- ✅ Phase 2.2 column redesign — already `Application | Sessions | Duration | Last Active | Status`
- ✅ Phase 2.3 tiles — already "Total session time" / "With open sessions"
- ✅ Phase 3.1 `py-px` → `py-0.5` — already done (line 151 shows `py-0.5`)
- ✅ Phase 3.2 timeline Closed col STALE/OFFLINE → lastSyncAt — already done (line 164-166)
- ✅ Phase 4.1 SessionEnding Windows handler — already exists

**Still needs to be done:**
- ❌ Phase 0: Migration not yet **run** against the live DB
- ❌ Phase 2.4 Title→Context — apps page still has `'Context'` header but need to verify
- ❌ Phase 2.5 Edge grouping (msedge/msedgewebview2 display normalisation)
- ❌ Phase 3.3 Web page open-tab duration (`visitDurationSeconds` returns 0 for open tabs)
- ❌ Phase 4.2/4.3 Reconcile re-queues `is_synced=0` — need to verify CloseSessionsAndAppItems resets is_synced
- ❌ Phase 4.1 gap: `SessionEnding` writes `power_off` event but does NOT call `CloseSessionsAndAppItemsAsync` to close open sessions

Let me check what `CloseSessionsAndAppItemsAsync` does to `is_synced`:

Viewed SqliteLogStore.cs:2198-2250
Searched for "UpdateAppSessionEndedSql"
Viewed DatabaseSchema.cs:623-645

Now I have the complete picture. `UpdateAppSessionEndedSql` already sets `is_synced = 0` on close. The issue is that the `SessionEnding` handler on Windows fires `power_off` event via `SafeRecordAsync` but does **not** call `CloseSessionsAndAppItemsAsync`. Let me now execute all the remaining work:

Created task.md

Good. Most phases are already implemented. Now let me execute the **3 remaining items**:

1. **Phase 3.3** — `visitDurationSeconds` open-tab duration fix (web page)
2. **Phase 4.1 gap** — `SessionEnding` must call `CloseSessionsAndAppItemsAsync`
3. **Phase 0** — Run migration on live DB

Let's start:

Edited page.tsx
Viewed page.tsx:67-72
Edited page.tsx

Phase 3.3 done. Now fix the **critical client shutdown gap** — `SessionEnding` only writes `power_off` but never closes open sessions:

Viewed SystemEventWatcher.Windows.cs:1-30

Now I need to find what the `SystemEventWatcher` base class looks like — specifically what injected services it has access to:

Searched for "class SystemEventWatcher"
Viewed SystemEventWatcher.cs:1-214

`_store` (which is `ILogStore`) is already available in `SystemEventWatcher`. Now I'll add the `CloseSessionsAndAppItemsAsync` call inside the `SessionEnding` handler:

Edited SystemEventWatcher.Windows.cs

Now I need to add the `CloseAllOpenSessionsOnShutdownAsync` helper method to the base `SystemEventWatcher.cs` (since `_store` lives there):

Edited SystemEventWatcher.cs

The edit added an extra closing brace (`}}`). Let me fix that:

Viewed SystemEventWatcher.cs:207-249

I see three issues: (1) the extra `}}` on line 249, (2) the `SafeRecordAsync` method is broken — the `}` closing it is missing (the new comment/method got inserted inside `SafeRecordAsync`). Let me view the full current state of lines 190-214 to understand the structure:

Viewed SystemEventWatcher.cs:188-215

The `SafeRecordAsync` method's closing `}` is missing — the new content was inserted inside it. Let me fix all the structural issues in one clean edit:

Edited SystemEventWatcher.cs

The diff shows a stray `}` was inserted inside the `try` block again. Let me view the exact state and do a clean targeted fix:

Viewed SystemEventWatcher.cs:200-225

Line 208-209 has an extra `}` stray. Fix it:

Edited SystemEventWatcher.cs

Now let me verify the full file is structurally sound:

Viewed SystemEventWatcher.cs:155-251