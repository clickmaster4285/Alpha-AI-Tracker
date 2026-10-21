'use client';

import { Suspense, useEffect, useRef } from 'react';
import { motion } from 'framer-motion';
import { Search, Loader2 } from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { dlpApi, type DlpAlertStatus } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { formatDateTime } from '@/lib/format';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const PER_PAGE = 30;

function DLPAlertsInner() {
  const qc = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, status: {} },
    { q: '', status: '' },
    { debounceMs: 300 },
  );

  const query = useInfiniteQuery({
    queryKey: ['dlp-alerts', filters],
    queryFn: ({ pageParam }) =>
      dlpApi.alerts.list({
        q: filters.q || undefined,
        status: filters.status || undefined,
        page: pageParam,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page * last.perPage < last.total ? last.page + 1 : undefined,
    placeholderData: keepPreviousData,
  });

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const obs = new IntersectionObserver((entries) => {
      if (entries[0]?.isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
        query.fetchNextPage();
      }
    }, { rootMargin: '200px' });
    obs.observe(el);
    return () => obs.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  const updateMutation = useMutation({
    mutationFn: ({ id, status }: { id: string; status: DlpAlertStatus }) =>
      dlpApi.alerts.update(id, { status }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dlp-alerts'] }),
  });

  const pages = query.data?.pages ?? [];
  const alerts = pages.flatMap((p) => p.alerts);
  const counts = pages[0];

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {[
          { label: 'Open', count: counts?.open ?? 0, color: 'text-destructive' },
          { label: 'Investigating', count: counts?.investigating ?? 0, color: 'text-warning' },
          { label: 'Resolved', count: counts?.resolved ?? 0, color: 'text-success' },
          { label: 'Total', count: counts?.total ?? 0, color: 'text-foreground' },
        ].map((s) => (
          <div key={s.label} className="bg-card rounded-xl border border-border p-4 text-center">
            <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
            <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs flex-1">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={filters.q}
            onChange={(e) => setFilters({ ...filters, q: e.target.value })}
            placeholder="Search alerts..."
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <Select
          value={filters.status || 'all'}
          onValueChange={(v) => setFilters({ ...filters, status: v === 'all' ? '' : v })}
        >
          <SelectTrigger className="w-40 h-9"><SelectValue placeholder="Status" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All statuses</SelectItem>
            <SelectItem value="open">Open</SelectItem>
            <SelectItem value="investigating">Investigating</SelectItem>
            <SelectItem value="resolved">Resolved</SelectItem>
            <SelectItem value="false_positive">False Positive</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {query.isLoading && (
        <div className="flex justify-center py-12"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>
      )}
      {query.isError && <p className="text-sm text-destructive">Failed to load alerts</p>}

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[900px]">
          <thead>
            <tr className="border-b border-border">
              {['Alert ID', 'Type', 'Employee', 'Severity', 'When', 'Evidence', 'Status'].map((h) => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {alerts.map((a, i) => {
              const evidence = a.evidence && typeof a.evidence === 'object'
                ? (a.evidence.path as string)
                  || (a.evidence.domain as string)
                  || (a.evidence.file as string)
                  || JSON.stringify(a.evidence)
                : '';
              return (
                <motion.tr
                  key={a.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: Math.min(i, 20) * 0.02 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30"
                >
                  <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{a.id.slice(0, 8)}…</td>
                  <td className="px-4 py-3 text-sm text-foreground">{a.type || '—'}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{a.employeeName || a.employeeId}</td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${severityColors[a.severity] ?? ''}`}>
                      {a.severity}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground">{formatDateTime(a.occurredAt)}</td>
                  <td className="px-4 py-3 text-xs text-muted-foreground max-w-[180px] truncate" title={evidence}>
                    {evidence || '—'}
                  </td>
                  <td className="px-4 py-3">
                    <Select
                      value={a.status}
                      onValueChange={(v) => updateMutation.mutate({ id: a.id, status: v as DlpAlertStatus })}
                    >
                      <SelectTrigger className="h-7 text-xs w-36"><SelectValue /></SelectTrigger>
                      <SelectContent>
                        <SelectItem value="open">Open</SelectItem>
                        <SelectItem value="investigating">Investigating</SelectItem>
                        <SelectItem value="resolved">Resolved</SelectItem>
                        <SelectItem value="false_positive">False Positive</SelectItem>
                      </SelectContent>
                    </Select>
                  </td>
                </motion.tr>
              );
            })}
            {!query.isLoading && alerts.length === 0 && (
              <tr>
                <td colSpan={7} className="px-4 py-10 text-center text-sm text-muted-foreground">
                  No DLP alerts yet — Windows agents will sync incidents here after Publish.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div ref={sentinelRef} className="h-8 flex items-center justify-center text-xs text-muted-foreground">
        {query.isFetchingNextPage && <Loader2 className="w-4 h-4 animate-spin" />}
        {!query.hasNextPage && alerts.length > 0 && `Showing all ${counts?.total ?? alerts.length}`}
      </div>
    </div>
  );
}

export default function DLPAlertsPage() {
  return (
    <Suspense fallback={<div className="flex justify-center py-16"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>}>
      <DLPAlertsInner />
    </Suspense>
  );
}
