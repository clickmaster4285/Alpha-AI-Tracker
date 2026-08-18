'use client';

import { Fragment, useEffect, useMemo, useState } from 'react';
import { AppWindow, Timer, Layers, Activity, Loader2, ChevronRight, ChevronDown } from 'lucide-react';
import { keepPreviousData, useQueries } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { createDefaultFilter, type ActivityFilter } from '@/components/journey/ActivityFilters';
import { appSessionsApi, type AppSession } from '@/lib/api';
import { formatDateTime, formatSeconds } from '@/lib/format';

interface AppUsage {
  appDisplayName: string;
  processName: string;
  sessionCount: number;
  durationSeconds: number;
  lastActiveAt: string;
  running: boolean;
  sessions: AppSession[];
}

/** Total time the session was open: endedAt - startedAt (or now - startedAt while running). */
function sessionDurationSeconds(s: AppSession): number {
  const start = new Date(s.startedAt).getTime();
  const end = s.endedAt ? new Date(s.endedAt).getTime() : Date.now();
  return Math.max(0, (end - start) / 1000);
}

const AGGREGATE_PAGES = 5; // most recent 5 × 100 = 500 sessions
const PER_PAGE = 100;

export default function EmployeeJourneyApps() {
  return (
    <EmployeePage
      title="App Usage"
      subtitle="Time spent per application across the employee's most recent sessions."
      icon={AppWindow}
    >
      {({ employee }) => <AppUsageBody employeeId={employee.employeeId} />}
    </EmployeePage>
  );
}

function AppUsageBody({ employeeId }: { employeeId: string }) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState<ActivityFilter>(createDefaultFilter);
  const [isFiltering, setIsFiltering] = useState(false);

  const toggle = (key: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const queries = useQueries({
    queries: Array.from({ length: AGGREGATE_PAGES }, (_, i) => ({
      queryKey: ['app-sessions', 'usage', { employeeId, page: i + 1, perPage: PER_PAGE, ...filter }],
      queryFn: () => appSessionsApi.list({
        employeeId,
        page: i + 1,
        perPage: PER_PAGE,
        search: filter.search || undefined,
        dateFrom: filter.dateFrom,
        dateTo: filter.dateTo,
      }),
      // Keep the previous result rendered while a filter/search refetch runs,
      // so the table never flashes to a full-page spinner mid-typing.
      placeholderData: keepPreviousData,
    })),
  });

  // Reflect query activity into the filter bar spinner (search in flight).
  const anyFetching = queries.some(q => q.isFetching);
  useEffect(() => { setIsFiltering(anyFetching); }, [anyFetching]);

  // Only show the full-page spinner on the very first load (no data at all yet);
  // filter/search refetches keep the previous table rendered via keepPreviousData.
  const hasData = queries.some(q => (q.data?.data?.length ?? 0) > 0);
  const loading = !hasData && queries.some(q => q.isLoading);
  const error = queries.find(q => q.isError);
  const sessions: AppSession[] = queries.flatMap(q => q.data?.data ?? []);

  const usage = useMemo(() => {
    const map = new Map<string, AppUsage>();
    for (const s of sessions) {
      const key = s.appDisplayName || s.processName || s.id;
      const entry = map.get(key) ?? {
        appDisplayName: s.appDisplayName,
        processName: s.processName,
        sessionCount: 0,
        durationSeconds: 0,
        lastActiveAt: s.startedAt,
        running: false,
        sessions: [],
      };
      entry.sessionCount += 1;
      entry.durationSeconds += sessionDurationSeconds(s);
      entry.sessions.push(s);
      if (s.startedAt > entry.lastActiveAt) entry.lastActiveAt = s.startedAt;
      if (!s.endedAt) entry.running = true;
      map.set(key, entry);
    }
    return [...map.values()].sort((a, b) => b.durationSeconds - a.durationSeconds);
  }, [sessions]);

  const totalDuration = usage.reduce((n, u) => n + u.durationSeconds, 0);
  const runningCount = usage.filter(u => u.running).length;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  return (
    <div className="space-y-4">
      {/* Server-side filters: search + date (default today) + custom range — always visible,
          never unmounted by loading/error/empty states so the search input keeps focus. */}
      <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} />

      {loading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : error ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load app usage</p>
          <p className="text-xs text-muted-foreground">{(error.error as Error)?.message || 'Unknown error'}</p>
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
        <UsageTile icon={Layers} label="Sessions" value={sessions.length} accent="bg-info/15 text-info" />
        <UsageTile icon={Timer} label="Active Time" value={formatSeconds(totalDuration)} accent="bg-success/15 text-success" />
        <UsageTile icon={Activity} label="Open Now" value={runningCount} accent="bg-warning/15 text-warning" />
      </div>

      <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
        <table className="w-full min-w-[760px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Sessions', 'Duration', 'Last Active', 'Status'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {usage.map(u => {
              const rowKey = `${u.appDisplayName}|${u.processName}`;
              const isOpen = expanded.has(rowKey);
              const expandable = u.sessionCount > 1;
              return (
                <Fragment key={rowKey}>
                  <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                    <td className="px-4 py-3">
                      <div
                        className={`flex items-center gap-2 ${expandable ? 'cursor-pointer select-none' : ''}`}
                        onClick={expandable ? () => toggle(rowKey) : undefined}
                        title={expandable ? (isOpen ? 'Collapse sessions' : 'Expand sessions') : undefined}
                      >
                        {expandable ? (
                          isOpen
                            ? <ChevronDown className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                            : <ChevronRight className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                        ) : (
                          <span className="w-4 flex-shrink-0" />
                        )}
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
                    <td className="px-4 py-3 text-sm text-success font-medium whitespace-nowrap">{formatSeconds(u.durationSeconds)}</td>
                    <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(u.lastActiveAt)}</td>
                    <td className="px-4 py-3">
                      {u.running ? (
                        <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-success/15 text-success">
                          <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" /> Running
                        </span>
                      ) : (
                        <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground">
                          <span className="w-1.5 h-1.5 rounded-full bg-muted-foreground" /> Closed
                        </span>
                      )}
                    </td>
                  </tr>
                  {isOpen && (
                    <tr className="border-b border-border bg-muted/20">
                      <td colSpan={5} className="px-0 py-0">
                        <ExpandedSessions sessions={u.sessions} />
                      </td>
                    </tr>
                  )}
                </Fragment>
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

function ExpandedSessions({ sessions }: { sessions: AppSession[] }) {
  return (
    <div className="px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-2">
        {sessions.length} session{sessions.length === 1 ? '' : 's'}
      </div>
      <table className="w-full">
        <thead>
          <tr className="border-b border-border">
            {['Opened', 'Closed', 'Duration', 'Details'].map(h => (
              <th key={h} className="text-left px-3 py-2 text-xs font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sessions.map(s => (
            <tr key={s.id} className="border-b border-border last:border-0">
              <td className="px-3 py-2.5 text-sm text-foreground whitespace-nowrap">{formatDateTime(s.startedAt)}</td>
              <td className="px-3 py-2.5 text-sm text-muted-foreground whitespace-nowrap">
                {s.endedAt ? (
                  formatDateTime(s.endedAt)
                ) : (
                  <span className="inline-flex items-center gap-1.5 text-xs font-medium bg-success/15 text-success px-2 py-0.5 rounded-full">
                    <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" /> Running
                  </span>
                )}
              </td>
              <td className="px-3 py-2.5 text-sm text-foreground font-medium whitespace-nowrap">{formatSeconds(sessionDurationSeconds(s))}</td>
              <td className="px-3 py-2.5 text-xs text-muted-foreground">
                <span className="font-mono">{s.processName || '—'}</span>
                {s.contextLabel && <span className="text-muted-foreground"> · {s.contextLabel}</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
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
