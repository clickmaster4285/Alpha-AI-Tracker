#!/usr/bin/env bash
# T5 — event vocabulary drift detector (finalplan §10).
# Asserts client, server, and web expose the same session_events event_type strings.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

extract_csharp() {
  grep -oE 'public const string [A-Za-z0-9_]+[[:space:]]*=[[:space:]]*"[^"]+"' \
    "$ROOT/client/Core/Models/SessionEvent.cs" \
    | sed -E 's/.*"([^"]+)".*/\1/' \
    | sort -u
}

extract_go() {
  grep -oE '= "[^"]+"' \
    "$ROOT/server/internal/models/session_event_types.go" \
    | sed -E 's/.*"([^"]+)".*/\1/' \
    | sort -u
}

extract_ts() {
  grep -oE ": '[^']+'" \
    "$ROOT/web/src/lib/eventTypes.ts" \
    | sed -E "s/.*'([^']+)'.*/\1/" \
    | sort -u
}

CS=$(extract_csharp)
GO=$(extract_go)
TS=$(extract_ts)

fail=0
if [[ "$CS" != "$GO" ]]; then
  echo "contract-event-types: C# vs Go mismatch" >&2
  diff <(printf '%s\n' "$CS") <(printf '%s\n' "$GO") >&2 || true
  fail=1
fi
if [[ "$CS" != "$TS" ]]; then
  echo "contract-event-types: C# vs web mismatch" >&2
  diff <(printf '%s\n' "$CS") <(printf '%s\n' "$TS") >&2 || true
  fail=1
fi

if [[ $fail -ne 0 ]]; then
  exit 1
fi

echo "contract-event-types: OK ($(printf '%s\n' "$CS" | wc -l) event types)"
