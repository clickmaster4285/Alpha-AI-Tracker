'use client';

import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { Globe, Loader2, ExternalLink, ChevronRight, ChevronDown } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import { appItemsApi, type AppItem } from '@/lib/api';
import { formatDateTime, formatDuration, formatSeconds } from '@/lib/format';

const PER_PAGE = 30;

interface WebGroup {
  domain: string;
  items: AppItem[];
  count: number;
  totalDuration: number;
  lastVisitedAt: string;
}

function domainOf(item: AppItem): string {
  if (item.domain) return item.domain;
  try {
    const host = new URL(item.url || '').hostname.replace(/^www\./, '');
    if (host) return host;
  } catch { /* fall through */ }
  return 'Other';
}

function visitDurationSeconds(item: AppItem): number {
  if (!item.closedAt) return 0;
  return Math.max(0, (new Date(item.closedAt).getTime() - new Date(item.openedAt).getTime()) / 1000);
}

export default function EmployeeJourneyWeb() {
  return (
    <EmployeePage
      title="Web Activity"
      subtitle="Websites the employee visited, captured from browser journeys."
      icon={Globe}
    >
      {({ employee }) => <WebBody employeeId={employee.employeeId} />}
    </EmployeePage>
  );
}

function WebBody({ employeeId }: { employeeId: string }) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  const toggle = (domain: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(domain)) next.delete(domain);
      else next.add(domain);
      return next;
    });
  };

  const {
    data,
    isLoading,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['app-items', 'browser_tab', { employeeId, perPage: PER_PAGE }],
    queryFn: ({ pageParam }) => appItemsApi.list({
      employeeId,
      itemType: 'browser_tab',
      page: pageParam as number,
      perPage: PER_PAGE,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const items = useMemo(() => data?.pages.flatMap(p => p.data) ?? [], [data]);
  const total = data?.pages[0]?.total ?? 0;

  const groups = useMemo(() => {
    const map = new Map<string, WebGroup>();
    for (const item of items) {
      const domain = domainOf(item);
      const group = map.get(domain) ?? {
        domain,
        items: [],
        count: 0,
        totalDuration: 0,
        lastVisitedAt: item.openedAt,
      };
      group.items.push(item);
      group.count += 1;
      group.totalDuration += visitDurationSeconds(item);
      if (item.openedAt > group.lastVisitedAt) group.lastVisitedAt = item.openedAt;
      map.set(domain, group);
    }
    return [...map.values()].sort((a, b) => b.lastVisitedAt.localeCompare(a.lastVisitedAt));
  }, [items]);

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
        <p className="text-sm text-destructive font-medium">Failed to load web activity</p>
        <p className="text-xs text-muted-foreground">{(error as Error)?.message || 'Unknown error'}</p>
      </div>
    );
  }
  if (items.length === 0) {
    return <EmptyState icon={Globe} text="No web activity captured yet" />;
  }

  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
      <div className="px-4 py-3 border-b border-border flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Globe className="w-4 h-4 text-primary" />
          <h3 className="text-sm font-semibold text-foreground">Visited Sites</h3>
        </div>
        <span className="text-xs text-muted-foreground">{total.toLocaleString()} page{total === 1 ? '' : 's'} · {groups.length} site{groups.length === 1 ? '' : 's'}</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[820px]">
          <thead>
            <tr className="border-b border-border">
              {['Site', 'Visits', 'Duration', 'Last Visited'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {groups.map(g => {
              const isOpen = expanded.has(g.domain);
              const expandable = g.count > 1;
              return (
                <Fragment key={g.domain}>
                  <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                    <td className="px-4 py-3">
                      <div
                        className={`flex items-center gap-2 ${expandable ? 'cursor-pointer select-none' : ''}`}
                        onClick={expandable ? () => toggle(g.domain) : undefined}
                        title={expandable ? (isOpen ? 'Collapse pages' : 'Expand pages') : undefined}
                      >
                        {expandable ? (
                          isOpen
                            ? <ChevronDown className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                            : <ChevronRight className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                        ) : (
                          <span className="w-4 flex-shrink-0" />
                        )}
                        <div className="w-8 h-8 rounded-lg bg-info/10 flex items-center justify-center flex-shrink-0">
                          <Globe className="w-4 h-4 text-info" />
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm font-medium text-foreground truncate">{g.domain}</p>
                          <p className="text-xs text-muted-foreground truncate">
                            {g.count} page visit{g.count === 1 ? '' : 's'}
                            {g.count > 1 && !isOpen ? ' — click to view' : ''}
                          </p>
                        </div>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-sm text-foreground font-medium">{g.count}</td>
                    <td className="px-4 py-3 text-sm text-info font-medium whitespace-nowrap">{formatSeconds(g.totalDuration)}</td>
                    <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(g.lastVisitedAt)}</td>
                  </tr>
                  {isOpen && (
                    <tr className="border-b border-border bg-muted/20">
                      <td colSpan={4} className="px-0 py-0">
                        <ExpandedPages items={g.items} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
            {isFetchingNextPage && (
              <tr>
                <td colSpan={4} className="px-4 py-4">
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

function ExpandedPages({ items }: { items: AppItem[] }) {
  return (
    <div className="px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-2">
        {items.length} page visit{items.length === 1 ? '' : 's'}
      </div>
      <table className="w-full">
        <thead>
          <tr className="border-b border-border">
            {['Page', 'URL', 'Visited', 'Duration'].map(h => (
              <th key={h} className="text-left px-3 py-2 text-xs font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map(item => (
            <tr key={item.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
              <td className="px-3 py-2.5">
                <div className="flex items-center gap-2">
                  <div className="w-7 h-7 rounded-lg bg-info/10 flex items-center justify-center flex-shrink-0">
                    <Globe className="w-3.5 h-3.5 text-info" />
                  </div>
                  <div className="min-w-0 max-w-[260px]">
                    <p className="text-sm font-medium text-foreground truncate">{item.title || 'Untitled page'}</p>
                    {item.parentItemId && (
                      <p className="text-xs text-muted-foreground font-mono truncate">{item.identifier || '—'}</p>
                    )}
                  </div>
                </div>
              </td>
              <td className="px-3 py-2.5">
                {item.url ? (
                  <a
                    href={item.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 text-sm text-primary hover:underline underline-offset-2 max-w-[240px] truncate"
                    title={item.url}
                  >
                    <span className="truncate">{item.url}</span>
                    <ExternalLink className="w-3 h-3 flex-shrink-0" />
                  </a>
                ) : (
                  <span className="text-sm text-muted-foreground">—</span>
                )}
              </td>
              <td className="px-3 py-2.5 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(item.openedAt)}</td>
              <td className="px-3 py-2.5 text-sm text-foreground font-medium whitespace-nowrap">
                {item.closedAt ? formatDuration(item.openedAt, item.closedAt) : '—'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
