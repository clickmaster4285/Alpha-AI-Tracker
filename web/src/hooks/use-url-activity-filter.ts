'use client';

import { startOfDay, endOfDay } from 'date-fns';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import type { ActivityFilter, DatePreset } from '@/components/journey/ActivityFilters';
import { presetRange } from '@/components/journey/ActivityFilters';

/**
 * Wire the ActivityFilter value used by `ActivityFilters` (search + date
 * preset + dateFrom/dateTo ISO) to the browser URL.
 *
 * URL schema (compact, readable):
 *   ?q=<search>                (only when search is non-empty)
 *   &preset=<today|yesterday|7d|30d|all|custom>
 *   &from=YYYY-MM-DD&to=YYYY-MM-DD   (only when preset=custom)
 *
 * - Selecting a preset writes its short key (`today`, `7d`, …) so the URL
 *   stays human-readable. The `dateFrom`/`dateTo` ISO strings are then
 *   computed client-side from the preset, never sent over the wire.
 * - Custom ranges persist `from`/`to` as `YYYY-MM-DD` so they survive a
 *   page reload without round-tripping through `new Date()`.
 * - Empty `q` is stripped from the URL — `?q=&preset=today` is not stored.
 *
 * Returns the same `[filter, setFilter]` shape `ActivityFilters` expects.
 */
export function useUrlActivityFilter(): [ActivityFilter, (next: ActivityFilter) => void] {
  const [url, setUrl] = useUrlQueryState(
    {
      q: {},
      preset: {},
      from: {},
      to: {},
    },
    {
      q: '',
      preset: 'today',
      from: '',
      to: '',
    },
  );

  // Decode URL → ActivityFilter. `from`/`to` are stored as YYYY-MM-DD for
  // `custom` ranges; otherwise they are derived from the preset.
  const preset = (url.preset || 'today') as DatePreset;
  const isCustom = preset === 'custom';
  const { dateFrom: presetFrom, dateTo: presetTo } = presetRange(preset);
  const filter: ActivityFilter = {
    search: url.q ?? '',
    preset,
    ...(isCustom && url.from && url.to
      ? {
          dateFrom: startOfDay(new Date(`${url.from}T00:00:00`)).toISOString(),
          dateTo: endOfDay(new Date(`${url.to}T23:59:59`)).toISOString(),
        }
      : { dateFrom: presetFrom, dateTo: presetTo }),
  };

  const setFilter = (next: ActivityFilter) => {
    // Re-encode ActivityFilter → compact URL form.
    setUrl({
      q: next.search || '',
      preset: next.preset,
      from: next.preset === 'custom' && next.dateFrom ? isoToYmd(next.dateFrom) : '',
      to: next.preset === 'custom' && next.dateTo ? isoToYmd(next.dateTo) : '',
    });
  };

  return [filter, setFilter];
}

function isoToYmd(iso: string): string {
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  } catch {
    return '';
  }
}