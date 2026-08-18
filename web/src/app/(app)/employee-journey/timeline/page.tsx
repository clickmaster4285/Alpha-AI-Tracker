'use client';

import { useEffect, useMemo, useRef } from 'react';
import { Route, Activity, AppWindow, Loader2 } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import FocusTime from '@/components/journey/FocusTime';
import { appSessionsApi } from '@/lib/api';
import { formatDateTime, formatDuration } from '@/lib/format';

const PER_PAGE = 30;

export default function EmployeeJourneyTimeline() {
  return (
    <EmployeePage
      title="Session Timeline"
      subtitle="Chronological view of every app session the employee ran."
      icon={Route}
    >
      {({ employee }) => <TimelineBody employeeId={employee.employeeId} />}
    </EmployeePage>
  );
}

function TimelineBody({ employeeId }: { employeeId: string }) {
  // Server-side pagination with infinite scroll (same pattern as the old
  // employee detail Activity tab).
  const {
    data,
    isLoading,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['app-sessions', { employeeId, perPage: PER_PAGE }],
    queryFn: ({ pageParam }) => appSessionsApi.list({ employeeId, page: pageParam as number, perPage: PER_PAGE }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const sessions = useMemo(() => data?.pages.flatMap(p => p.data) ?? [], [data]);
  const total = data?.pages[0]?.total ?? 0;

  // Sentinel that triggers the next page fetch when scrolled into view.
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }
  if (isError) {
    return (
      <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
        <p className="text-sm text-destructive font-medium">Failed to load activity</p>
        <p className="text-xs text-muted-foreground">{(error as Error)?.message || 'Unknown error'}</p>
      </div>
    );
  }
  if (sessions.length === 0) {
    return <EmptyState icon={Activity} text="No app activity synced yet" />;
  }

  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
      <div className="px-4 py-3 border-b border-border flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Activity className="w-4 h-4 text-primary" />
          <h3 className="text-sm font-semibold text-foreground">App Activity</h3>
        </div>
        <span className="text-xs text-muted-foreground">{total.toLocaleString()} session{total === 1 ? '' : 's'}</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[980px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Status', 'Opened', 'Closed', 'Duration', 'Foreground / Background'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sessions.map(s => {
              const fg = s.foregroundSeconds ?? 0;
              const bg = s.backgroundSeconds ?? 0;
              return (
                <tr key={s.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                        <AppWindow className="w-4 h-4 text-primary" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-foreground truncate">{s.appDisplayName || s.processName}</p>
                        <div className="flex items-center gap-1.5">
                          {s.appDisplayName && s.appDisplayName !== s.processName && (
                            <span className="text-xs text-muted-foreground font-mono">{s.processName}</span>
                          )}
                          <span className="px-1.5 py-px rounded text-[10px] font-mono bg-primary/10 text-primary capitalize">{s.platform || '—'}</span>
                          {s.contextLabel && <span className="text-xs text-muted-foreground truncate max-w-[140px]">· {s.contextLabel}</span>}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    {s.endedAt ? (
                      <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground">
                        <span className="w-1.5 h-1.5 rounded-full bg-muted-foreground" /> Closed
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-success/15 text-success">
                        <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" /> Running
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(s.startedAt)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">
                    {s.endedAt ? formatDateTime(s.endedAt) : '—'}
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap">
                    {formatDuration(s.startedAt, s.endedAt || new Date().toISOString())}
                  </td>
                  <td className="px-4 py-3">
                    <FocusTime fg={fg} bg={bg} />
                  </td>
                </tr>
              );
            })}
            {isFetchingNextPage && (
              <tr>
                <td colSpan={6} className="px-4 py-4">
                  <div className="flex items-center justify-center gap-2 text-sm text-muted-foreground">
                    <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
                  </div>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {hasNextPage && (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          Scroll for more
        </div>
      )}
    </div>
  );
}
