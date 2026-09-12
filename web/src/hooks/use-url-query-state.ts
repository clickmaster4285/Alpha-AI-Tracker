'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';

/**
 * Sync a page's filter state with the browser URL so that:
 *   1. applying a filter in the UI writes the same key/value to the URL via
 *      `router.replace` (no history entries, no scroll jump), and
 *   2. a manual URL edit (typing in the address bar, a deep link, browser
 *      back/forward) propagates back into the page state.
 *
 * Why this hook exists
 * --------------------
 * The 15+ filter-heavy pages in this dashboard (employees, shifts, timesheets,
 * attendance, the journey/device-specs shells, configuration/apps,
 * configuration/websites, logs/comprehensive, user-management) all need the
 * same dual-direction wiring. Without a shared hook, every page would
 * re-implement `useSearchParams` + `useRouter` + `router.replace` glue, and the
 * patterns would drift (some pages would push history entries on every
 * keystroke, some would forget to read the URL back on mount, some would
 * race the React render). Centralising it here also keeps the call sites readable.
 *
 * Semantics
 * ---------
 * - **Read** — on mount and on every `searchParams` change, `value` reflects
 *   the URL. Unknown keys are ignored; missing keys take `initial`.
 * - **Write** — `setValue(next)` accepts a partial patch (shallow merge) and
 *   `router.replace`s the URL with the merged keys. Writes are
 *   **debounced by `debounceMs`** so a 300 ms search box does not push 12
 *   history entries while the user types (default 0 — caller-controlled).
 * - **Empty keys are stripped** — a key whose value is `''`, `null`, or
 *   `undefined` is removed from the URL so the address bar stays clean.
 * - **Stable identity** — `value` is a stable reference when the underlying
 *   stringified keys haven't changed, so it's safe to use in `useEffect`
 *   dependency arrays.
 *
 * Caveats
 * -------
 * - Pages wrapped in a `<Suspense>` boundary should still call this hook
 *   inside the inner client component (this is a 'use client' hook).
 * - `router.replace` does not scroll, so this is safe to call from form
 *   changes. For push-history semantics (so back/forward cycles filters)
 *   pass `{ history: 'push' }`.
 */
export interface UrlQueryStateOptions {
  /**
   * Debounce window for writes (ms). Use a positive value for search inputs
   * that fire on every keystroke; `0` for discrete changes (selects, dates).
   */
  debounceMs?: number;
  /** Replace (default) or push history entries. */
  history?: 'replace' | 'push';
}

export type UrlQueryStatePatch<T extends Record<string, unknown>> = Partial<T> | ((prev: T) => T);

/**
 * Module-level "latest URL search string any hook on this page wrote" slot.
 *
 * Sibling `useUrlQueryState` hooks (e.g. one for the employee picker, one
 * for the activity filter) call `flush` from the same React batch when the
 * user interacts with both in one tick. Each `flush` needs to read the
 * URL *including* the previous sibling's just-written keys — not the
 * stale `useSearchParams()` value, which only updates after the router
 * commits. Sharing a single mutable slot (cleared on every page load so
 * cross-page links never leak state) lets the chain stay consistent.
 */
const latestWrittenSearch: { current: string } = { current: '' };

if (typeof window !== 'undefined') {
  // Reset the chain when the user navigates to a new page so a stale slot
  // from the previous route can't leak into this page's first flush.
  window.addEventListener('popstate', () => { latestWrittenSearch.current = ''; }, { passive: true });
}

const stripEmpty = (record: Record<string, unknown>): Record<string, string> => {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(record)) {
    if (v === '' || v === null || v === undefined) continue;
    out[k] = String(v);
  }
  return out;
};

/**
 * Read a typed record of query-string keys. Unknown keys present in the URL
 * are left in place — only the keys known to the schema are extracted.
 */
function readFromUrl<T extends Record<string, string>>(
  searchParams: URLSearchParams,
  schema: Record<keyof T, { parse?: (raw: string) => T[keyof T] }>,
): T {
  const result = {} as T;
  for (const key of Object.keys(schema) as (keyof T)[]) {
    const raw = searchParams.get(String(key));
    if (raw === null) continue;
    const def = schema[key];
    if (def.parse) {
      result[key] = def.parse(raw);
    } else {
      result[key] = raw as T[keyof T];
    }
  }
  return result;
}

export function useUrlQueryState<T extends Record<string, string>>(
  schema: Record<keyof T, { parse?: (raw: string) => T[keyof T] }>,
  initial: T,
  options: UrlQueryStateOptions = {},
): [T, (patch: UrlQueryStatePatch<T>) => void] {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const { debounceMs = 0, history = 'replace' } = options;

  // The current parsed record. Initialized from the URL on first render so
  // the page hydrates with the URL the user navigated to (deep link).
  const [value, setValueState] = useState<T>(() => ({
    ...initial,
    ...readFromUrl(searchParams, schema),
  }));

  // When the URL changes from outside the page (back/forward, manual edit,
  // a programmatic push from a sibling component OR from a sibling
  // `useUrlQueryState` / `useUrlActivityFilter` instance managing a
  // disjoint key set) propagate the change back into `value`.
  //
  // We diff ONLY against the keys this hook owns — a sibling hook writing
  // its own key must not be treated as a "reset to defaults" signal for
  // this hook, otherwise two hooks on the same page ping-pong each other
  // into their initial values on every write. See AGENTS.md → URL-Synced
  // Filters Rule.
  const lastSerialized = useRef<string>('');
  useEffect(() => {
    const carry = {} as T;
    for (const key of Object.keys(schema) as (keyof T)[]) {
      const fromUrl = searchParams.get(String(key));
      carry[key] = fromUrl === null ? initial[key] : (fromUrl as T[keyof T]);
    }
    const serialized = JSON.stringify(carry);
    if (serialized !== lastSerialized.current) {
      lastSerialized.current = serialized;
      setValueState(carry);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  const writeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingPatch = useRef<UrlQueryStatePatch<T> | null>(null);

  const flush = useCallback(() => {
    writeTimer.current = null;
    const patch = pendingPatch.current;
    pendingPatch.current = null;
    if (!patch) return;

    setValueState(prev => {
      const nextResolved = typeof patch === 'function' ? patch(prev) : { ...prev, ...patch };
      // Merge with initial defaults so the URL only carries keys we manage.
      //
      // Read the FRESHEST search string, NOT the `searchParams` captured
      // in this `flush` closure. When two URL hooks write in the same
      // React batch (e.g. a row click sets `employeeId` AND the user
      // changes a filter chip in the same tick), both `setValue`s fire
      // before either `router.replace` resolves; the closure's
      // `searchParams` still reflects the URL *before* the first write,
      // so the second flush would erase the first hook's key. We chain
      // writes through a module-level "last URL we wrote" slot: each
      // flush reads the previous flush's result, merges its own keys,
      // and updates the slot. `window.location.search` is also updated
      // by Next's router (synchronously via history.replaceState) — fall
      // back to it when the chain slot is empty (first flush ever).
      const baseSearch =
        (typeof window !== 'undefined' && latestWrittenSearch.current)
          || (typeof window !== 'undefined' ? window.location.search : '')
          || searchParams.toString();
      const urlParams = new URLSearchParams(baseSearch);
      // Remove all keys managed by this hook (we'll re-add the current ones).
      for (const key of Object.keys(schema) as (keyof T)[]) {
        urlParams.delete(String(key));
      }
      const merged: Record<string, string> = {
        ...stripEmpty(urlParams as unknown as Record<string, unknown>),
        ...stripEmpty(nextResolved as unknown as Record<string, unknown>),
      };
      const qs = new URLSearchParams(merged).toString();
      // Record what we're about to write so the next sibling hook's flush
      // (in the same React batch) sees this hook's keys instead of the
      // pre-write URL.
      latestWrittenSearch.current = qs ? `?${qs}` : '';
      lastSerialized.current = JSON.stringify(nextResolved);
      const url = qs ? `${pathname}?${qs}` : pathname;
      if (history === 'push') router.push(url); else router.replace(url);
      return nextResolved;
    });
  }, [pathname, router, searchParams, schema, history]);

  const setValue = useCallback(
    (patch: UrlQueryStatePatch<T>) => {
      pendingPatch.current = patch;
      if (writeTimer.current) clearTimeout(writeTimer.current);
      if (debounceMs > 0) {
        writeTimer.current = setTimeout(flush, debounceMs);
      } else {
        flush();
      }
    },
    [debounceMs, flush],
  );

  // Cancel any pending flush on unmount so an unmounted component doesn't
  // call `router.replace`.
  useEffect(() => () => {
    if (writeTimer.current) clearTimeout(writeTimer.current);
  }, []);

  return [value, setValue];
}