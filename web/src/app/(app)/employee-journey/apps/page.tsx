'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { AppWindow, Timer, Layers, Activity, Loader2 } from 'lucide-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { appSessionsApi, type AppUsageRow } from '@/lib/api';
import { formatDateTime, formatSeconds } from '@/lib/format';

export default function EmployeeJourneyApps() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="App Usage"
        subtitle="Time spent per application across the employee&apos;s most recent sessions."
        icon={AppWindow}
      >
        {({ employee, filter, setFilter }) => (
          <AppUsageBody employeeId={employee.employeeId} filter={filter} setFilter={setFilter} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function AppUsageBody({
  employeeId,
  filter,
  setFilter,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
}) {
  const [isFiltering, setIsFiltering] = useState(false);

  // The per-app aggregate is computed server-side in a single
  // GROUP BY (app_display_name, process_name), so one query is
  // enough — no more 5×100 fan-out. Each row already carries
  // firstOpenedAt / lastClosedAt, so the "Duration" cell is just
  // (lastClosedAt - firstOpenedAt). This is correct even when the
  // client emits multiple rows per window (e.g. one row per tab) —
  // the bug where "3 tabs × 10 min" rendered as "30 min" is fixed
  // at the server, not by summing in the page.
  const query = useQuery({
    queryKey: ['app-sessions', 'usage', { employeeId, ...filter }],
    queryFn: () => appSessionsApi.usage({
      employeeId,
      page: 1,
      perPage: 100,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    // Keep the previous result rendered while a filter/search refetch runs,
    // so the table never flashes to a full-page spinner mid-typing.
    placeholderData: keepPreviousData,
  });

  useEffect(() => { setIsFiltering(query.isFetching); }, [query.isFetching]);

  // Defense in depth (Layer 2): even if the server somehow returned
  // a stale sum, the page recomputes duration as (lastClosedAt -
  // firstOpenedAt) here too. This is the user's stated rule:
  // "first opened → last close, NOT Σ durations". MAX/MIN over the
  // already-aggregated row is a no-op (one row per app), so the
  // cost is negligible. If a future server change reintroduces the
  // sum, the page stays correct.
  const usage: AppUsageRow[] = useMemo(() => {
    const rows = query.data?.data ?? [];
    return rows.map(r => {
      const first = new Date(r.firstOpenedAt).getTime();
      const last = new Date(r.lastClosedAt).getTime();
      const openRangeSeconds = Math.max(0, (last - first) / 1000);
      return { ...r, totalDurationSeconds: openRangeSeconds };
    }).sort((a, b) => b.totalDurationSeconds - a.totalDurationSeconds);
  }, [query.data]);

  // Total = sum of per-app (lastClosed - firstOpened) so the "Active
  // Time" tile reflects the user's mental model: "the time the app
  // was open", not the sum of overlapping child sessions.
  const totalDuration = usage.reduce((n, u) => n + u.totalDurationSeconds, 0);
  const totalSessions = usage.reduce((n, u) => n + u.sessionCount, 0);
  const runningCount = usage.filter(u =>
    new Date(u.lastClosedAt).getTime() > Date.now() - 60_000
  ).length;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  return (
    <div className="space-y-4">
      {/* Server-side filters: search + date (default today) + custom range — always visible,
          never unmounted by loading/error/empty states so the search input keeps focus. */}
      <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} />

      {query.isLoading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load app usage</p>
          <p className="text-xs text-muted-foreground">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : usage.length === 0 ? (
        <EmptyState
          icon={AppWindow}
          text={filtered ? 'No app usage matches the current filters' : 'No app usage synced yet'}
        />
      ) : (
        <>
      {/* Stat tiles */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <UsageTile icon={AppWindow} label="Applications" value={usage.length} accent="bg-primary/10 text-primary" />
        <UsageTile icon={Layers} label="Sessions" value={totalSessions} accent="bg-info/15 text-info" />
        <UsageTile icon={Timer} label="Active Time" value={formatSeconds(totalDuration)} accent="bg-success/15 text-success" />
        <UsageTile icon={Activity} label="Open Now" value={runningCount} accent="bg-warning/15 text-warning" />
      </div>

      <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
        <table className="w-full min-w-[760px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Sessions', 'Duration', 'First Opened', 'Last Closed'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {usage.map(u => {
              const rowKey = `${u.appDisplayName}|${u.processName}`;
              return (
                <tr key={rowKey} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <span className="w-4 flex-shrink-0" />
                      <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                        <AppWindow className="w-4 h-4 text-primary" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-foreground truncate">{u.appDisplayName || u.processName}</p>
                        {u.processName && u.processName !== u.appDisplayName && (
                          <p className="text-xs text-muted-foreground font-mono truncate">{u.processName}</p>
                        )}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground font-medium">{u.sessionCount}</td>
                  <td className="px-4 py-3 text-sm text-success font-medium whitespace-nowrap">{formatSeconds(u.totalDurationSeconds)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(u.firstOpenedAt)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(u.lastClosedAt)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
        </>
      )}
    </div>
  );
}

function UsageTile({ icon: Icon, label, value, accent }: {
  icon: React.ElementType; label: string; value: number | string; accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center ${accent}`}>
        <Icon className="w-4 h-4" />
      </div>
      <p className="mt-3 text-2xl font-bold text-foreground font-display">{value}</p>
      <p className="text-xs text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}
