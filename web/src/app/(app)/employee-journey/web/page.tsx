'use client';

import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { Globe, Loader2, ExternalLink, ChevronRight, ChevronDown } from 'lucide-react';
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { createDefaultFilter, type ActivityFilter } from '@/components/journey/ActivityFilters';
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

const BROWSER_NAMES: Record<string, string> = {
  chrome: 'Chrome', chromium: 'Chromium', firefox: 'Firefox', 'firefox-esr': 'Firefox ESR',
  msedge: 'Edge', edge: 'Edge', brave: 'Brave', opera: 'Opera', vivaldi: 'Vivaldi',
  'microsoft-edge': 'Edge', 'google-chrome': 'Chrome', 'google-chrome-stable': 'Chrome',
};

/** Browser/source of a journey row, read from metadata_json.processName (e.g. "chrome"). */
function browserOf(item: AppItem): string | null {
  try {
    const meta = item.metadataJson ? JSON.parse(item.metadataJson) as { processName?: string } : null;
    const name = meta?.processName;
    if (!name) return null;
    return BROWSER_NAMES[name.toLowerCase()] ?? null;
  } catch { return null; }
}

function BrowserBadge({ item }: { item: AppItem }) {
  const browser = browserOf(item);
  if (!browser) return null;
  return (
    <span className="px-1.5 py-0.5 rounded-md text-[10px] font-medium bg-muted text-muted-foreground whitespace-nowrap">
      {browser}
    </span>
  );
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
  const [filter, setFilter] = useState<ActivityFilter>(createDefaultFilter);
  const [isFiltering, setIsFiltering] = useState(false);

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
    isFetching,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['app-items', 'browser_tab', { employeeId, perPage: PER_PAGE, ...filter }],
    queryFn: ({ pageParam }) => appItemsApi.list({
      employeeId,
      itemType: 'browser_tab',
      page: pageParam as number,
      perPage: PER_PAGE,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    // Keep the previous result rendered while a filter/search refetch runs,
    // so the table never flashes to a full-page spinner mid-typing.
    placeholderData: keepPreviousData,
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  useEffect(() => { setIsFiltering(isFetching); }, [isFetching]);

  const items = useMemo(() => data?.pages.flatMap(p => p.data) ?? [], [data]);
  const total = data?.pages[0]?.total ?? 0;
  const searchActive = filter.search.trim() !== '';

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

  const filtered = filter.search !== '' || filter.preset !== 'all';

  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
      {/* Server-side filters: search + date (default today) + custom range — always visible,
          never unmounted by loading/error/empty states so the search input keeps focus. */}
      <div className="px-4 py-3 border-b border-border">
        <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} />
      </div>

      {isLoading && items.length === 0 ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load web activity</p>
          <p className="text-xs text-muted-foreground">{(error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : items.length === 0 ? (
        <div className="py-12">
          <EmptyState
            icon={Globe}
            text={filtered ? 'No web activity matches the current filters' : 'No web activity captured yet'}
          />
        </div>
      ) : (
        <div className="overflow-x-auto">
          {/* SEARCH ACTIVE → flat list of exact matching pages (the user wants to see
              the exact URLs, not a domain group). NO SEARCH → grouped by site. */}
          {searchActive ? (
            <>
              <div className="px-4 py-3 border-b border-border flex items-center justify-between flex-wrap gap-2">
                <div className="flex items-center gap-2">
                  <Globe className="w-4 h-4 text-primary" />
                  <h3 className="text-sm font-semibold text-foreground">Matching Pages</h3>
                </div>
                <span className="text-xs text-muted-foreground">{total.toLocaleString()} page{total === 1 ? '' : 's'}</span>
              </div>
              <table className="w-full min-w-[820px]">
                <thead>
                  <tr className="border-b border-border">
                    {['Page', 'URL', 'Domain', 'Visited', 'Duration'].map(h => (
                      <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {items.map(item => <PageRow key={item.id} item={item} withDomain />)}
                  {isFetchingNextPage && <LoadingRow colSpan={5} />}
                </tbody>
              </table>
            </>
          ) : (
            <>
              <div className="px-4 py-3 border-b border-border flex items-center justify-between flex-wrap gap-2">
                <div className="flex items-center gap-2">
                  <Globe className="w-4 h-4 text-primary" />
                  <h3 className="text-sm font-semibold text-foreground">Visited Sites</h3>
                </div>
                <span className="text-xs text-muted-foreground">{total.toLocaleString()} page{total === 1 ? '' : 's'} · {groups.length} site{groups.length === 1 ? '' : 's'}</span>
              </div>
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
                    // Distinct browsers used across this site's visits (e.g. Chrome + Firefox).
                    const browsers = [...new Set(g.items.map(browserOf).filter((b): b is string => !!b))];
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
                                <div className="flex items-center gap-1.5">
                                  <p className="text-sm font-medium text-foreground truncate">{g.domain}</p>
                                  {browsers.slice(0, 3).map(b => (
                                    <span key={b} className="px-1.5 py-0.5 rounded-md text-[10px] font-medium bg-muted text-muted-foreground whitespace-nowrap">
                                      {b}
                                    </span>
                                  ))}
                                  {browsers.length > 3 && (
                                    <span className="text-[10px] text-muted-foreground whitespace-nowrap">+{browsers.length - 3}</span>
                                  )}
                                </div>
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
                  {isFetchingNextPage && <LoadingRow colSpan={4} />}
                </tbody>
              </table>
            </>
          )}
        </div>
      )}
      {hasNextPage && (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          Scroll for more
        </div>
      )}
    </div>
  );
}

function LoadingRow({ colSpan }: { colSpan: number }) {
  return (
    <tr>
      <td colSpan={colSpan} className="px-4 py-4">
        <div className="flex items-center justify-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
        </div>
      </td>
    </tr>
  );
}

/** One exact page visit row (Page + browser badge, URL, optional domain, visited, duration). */
function PageRow({ item, withDomain = false }: { item: AppItem; withDomain?: boolean }) {
  return (
    <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
      <td className="px-4 py-3">
        <div className="flex items-center gap-2">
          <div className="w-8 h-8 rounded-lg bg-info/10 flex items-center justify-center flex-shrink-0">
            <Globe className="w-4 h-4 text-info" />
          </div>
          <div className="min-w-0 max-w-[280px]">
            <div className="flex items-center gap-1.5">
              <p className="text-sm font-medium text-foreground truncate">{item.title || 'Untitled page'}</p>
              <BrowserBadge item={item} />
            </div>
            {item.parentItemId && (
              <p className="text-xs text-muted-foreground font-mono truncate">{item.identifier || '—'}</p>
            )}
          </div>
        </div>
      </td>
      <td className="px-4 py-3">
        {item.url ? (
          <a
            href={item.url}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-sm text-primary hover:underline underline-offset-2 max-w-[260px] truncate"
            title={item.url}
          >
            <span className="truncate">{item.url}</span>
            <ExternalLink className="w-3 h-3 flex-shrink-0" />
          </a>
        ) : (
          <span className="text-sm text-muted-foreground">—</span>
        )}
      </td>
      {withDomain && (
        <td className="px-4 py-3">
          {item.domain ? (
            <span className="px-2 py-0.5 rounded-md text-xs font-mono bg-muted text-muted-foreground">{item.domain}</span>
          ) : (
            <span className="text-sm text-muted-foreground">—</span>
          )}
        </td>
      )}
      <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(item.openedAt)}</td>
      <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap">
        {item.closedAt ? formatDuration(item.openedAt, item.closedAt) : '—'}
      </td>
    </tr>
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
          {items.map(item => <PageRow key={item.id} item={item} />)}
        </tbody>
      </table>
    </div>
  );
}
