'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ExternalLink, Loader2 } from 'lucide-react';
import { liveStreamApi, type LiveStreamEmployee } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { LiveStreamEmployeeSidebar } from '@/components/live-stream/LiveStreamEmployeeSidebar';
import { LiveStreamPreviewGrid } from '@/components/live-stream/LiveStreamPreviewGrid';
import {
  LIVE_STREAM_MAX_TILES,
  parseLiveStreamIds,
  toggleLiveStreamId,
} from '@/components/live-stream/live-stream-selection';

/**
 * Full-window multi-monitor theater opened via ExternalLink from /live-stream.
 * Own route + layout (no app chrome). Selection synced via ?ids=.
 */
export default function LiveStreamTheaterPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center h-screen">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <LiveStreamTheaterInner />
    </Suspense>
  );
}

function LiveStreamTheaterInner() {
  const [filters, setFilters] = useUrlQueryState(
    { ids: {}, q: {} },
    { ids: '', q: '' },
    { debounceMs: 0, history: 'replace' },
  );
  const [searchInput, setSearchInput] = useState(filters.q);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.q) setFilters({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.q, setFilters]);
  useEffect(() => {
    setSearchInput(filters.q);
  }, [filters.q]);

  const selectedIds = useMemo(
    () => parseLiveStreamIds(filters.ids),
    [filters.ids],
  );

  const { data, isLoading, isError } = useQuery({
    queryKey: ['live-stream-employees'],
    queryFn: () => liveStreamApi.employees(),
    refetchInterval: 1_000,
    staleTime: 0,
  });

  const employees = useMemo(() => {
    const list = data?.data ?? [];
    const q = filters.q.trim().toLowerCase();
    const filtered = q
      ? list.filter(
          (e) =>
            e.name.toLowerCase().includes(q) ||
            e.employeeId.toLowerCase().includes(q) ||
            (e.department || '').toLowerCase().includes(q),
        )
      : list;
    return [...filtered].sort((a, b) => {
      if (a.online !== b.online) return a.online ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [data?.data, filters.q]);

  const byId = useMemo(() => {
    const m = new Map<string, LiveStreamEmployee>();
    for (const e of data?.data ?? []) m.set(e.employeeId, e);
    return m;
  }, [data?.data]);

  const toggleEmployee = (employeeId: string) => {
    setFilters({ ids: toggleLiveStreamId(selectedIds, employeeId).join(',') });
  };

  const removeEmployee = (employeeId: string) => {
    setFilters({ ids: selectedIds.filter((id) => id !== employeeId).join(',') });
  };

  const tileCount = selectedIds.length;

  return (
    <div className="flex gap-3 h-full p-3 min-h-0">
      <LiveStreamEmployeeSidebar
        employees={employees}
        selectedIds={selectedIds}
        maxTiles={LIVE_STREAM_MAX_TILES}
        searchInput={searchInput}
        onSearchChange={setSearchInput}
        isLoading={isLoading}
        isError={isError}
        onToggle={toggleEmployee}
        title="Theater"
        subtitle={`Watch wall · up to ${LIVE_STREAM_MAX_TILES} (${selectedIds.length}/${LIVE_STREAM_MAX_TILES})`}
      />

      <section className="flex-1 bg-card rounded-xl border border-border flex flex-col overflow-hidden min-w-0">
        <div className="px-4 py-3 border-b border-border flex items-center justify-between gap-3">
          <div>
            <p className="text-sm font-medium text-foreground">Screen preview</p>
            <p className="text-xs text-muted-foreground">
              {tileCount === 0
                ? 'Select employees from the sidebar'
                : `Watching ${tileCount} employee${tileCount === 1 ? '' : 's'}`}
            </p>
          </div>
          <a
            href="/live-stream"
            className="inline-flex items-center gap-1.5 text-xs text-muted-foreground hover:text-primary transition-colors"
            title="Back to Live Stream (this window)"
          >
            <ExternalLink className="w-3.5 h-3.5" />
            Main console
          </a>
        </div>

        <LiveStreamPreviewGrid
          selectedIds={selectedIds}
          byId={byId}
          maxTiles={LIVE_STREAM_MAX_TILES}
          onRemove={removeEmployee}
        />
      </section>
    </div>
  );
}
