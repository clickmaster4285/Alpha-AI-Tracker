'use client';

import { Suspense, useEffect, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Loader2, Search, ShieldAlert } from 'lucide-react';
import { useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { dlpAlertsApi, type DlpAlert } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import EmptyState from '@/components/employees/EmptyState';

const PER_PAGE = 20;

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const statusColors: Record<string, string> = {
  open: 'bg-destructive/15 text-destructive',
  investigating: 'bg-warning/15 text-warning',
  resolved: 'bg-success/15 text-success',
  false_positive: 'bg-muted text-muted-foreground',
};

const typeColors: Record<string, string> = {
  file_transfer: 'bg-info/15 text-info',
  usb: 'bg-warning/15 text-warning',
  cloud_upload: 'bg-primary/15 text-primary',
};

function labelTrigger(t: string) {
  return t.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

export default function DLPAlertsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <DlpAlertsInner />
    </Suspense>
  );
}

function DlpAlertsInner() {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, status: {}, severity: {}, trigger: {} },
    { q: '', status: '', severity: '', trigger: '' },
    { debounceMs: 0, history: 'replace' },
  );
  const [searchInput, setSearchInput] = useState(filters.q);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.q) setFilters({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.q, setFilters]);
  useEffect(() => { setSearchInput(filters.q); }, [filters.q]);

  const {
    data,
    isLoading,
    isFetchingNextPage,
    hasNextPage,
    fetchNextPage,
  } = useInfiniteQuery({
    queryKey: ['dlp-alerts', filters.q, filters.status, filters.severity, filters.trigger],
    initialPageParam: 1,
    queryFn: ({ pageParam }) =>
      dlpAlertsApi.list({
        page: pageParam,
        perPage: PER_PAGE,
        q: filters.q || undefined,
        status: filters.status || undefined,
        severity: filters.severity || undefined,
        trigger: filters.trigger || undefined,
      }),
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

  const alerts = data?.pages.flatMap(p => p.data) ?? [];
  const total = data?.pages[0]?.total ?? 0;

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) fetchNextPage();
      },
      { rootMargin: '300px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const patchMutation = useMutation({
    mutationFn: ({ id, status }: { id: string; status: string }) =>
      dlpAlertsApi.patch(id, { status }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-alerts'] });
      toast.success('Alert updated');
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to update alert'),
  });

  const openCount = alerts.filter(a => a.status === 'open').length;
  const invCount = alerts.filter(a => a.status === 'investigating').length;
  const resCount = alerts.filter(a => a.status === 'resolved').length;

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {[
          { label: 'Open (loaded)', count: openCount, color: 'text-destructive' },
          { label: 'Investigating', count: invCount, color: 'text-warning' },
          { label: 'Resolved', count: resCount, color: 'text-success' },
          { label: 'Total matching', count: total, color: 'text-foreground' },
        ].map(s => (
          <div key={s.label} className="bg-card rounded-xl border border-border p-4 text-center">
            <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
            <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      <div className="flex flex-wrap gap-2 items-center">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs flex-1 min-w-[180px]">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search alerts..."
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <Select value={filters.status || 'all'} onValueChange={v => setFilters({ status: v === 'all' ? '' : v })}>
          <SelectTrigger className="w-[160px]"><SelectValue placeholder="Status" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All statuses</SelectItem>
            <SelectItem value="open">Open</SelectItem>
            <SelectItem value="investigating">Investigating</SelectItem>
            <SelectItem value="resolved">Resolved</SelectItem>
            <SelectItem value="false_positive">False Positive</SelectItem>
          </SelectContent>
        </Select>
        <Select value={filters.severity || 'all'} onValueChange={v => setFilters({ severity: v === 'all' ? '' : v })}>
          <SelectTrigger className="w-[140px]"><SelectValue placeholder="Severity" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All severity</SelectItem>
            <SelectItem value="critical">Critical</SelectItem>
            <SelectItem value="high">High</SelectItem>
            <SelectItem value="medium">Medium</SelectItem>
            <SelectItem value="low">Low</SelectItem>
          </SelectContent>
        </Select>
        <Select value={filters.trigger || 'all'} onValueChange={v => setFilters({ trigger: v === 'all' ? '' : v })}>
          <SelectTrigger className="w-[160px]"><SelectValue placeholder="Trigger" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All triggers</SelectItem>
            <SelectItem value="usb">USB</SelectItem>
            <SelectItem value="file_transfer">File Transfer</SelectItem>
            <SelectItem value="cloud_upload">Cloud Upload</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {isLoading ? (
        <div className="flex justify-center py-16"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>
      ) : alerts.length === 0 ? (
        <EmptyState
          icon={ShieldAlert}
          text="No DLP alerts yet. Alerts appear when the desktop DLP agent matches an admin rule against USB, removable-file, or browser URL events."
        />
      ) : (
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[900px]">
            <thead>
              <tr className="border-b border-border">
                {['Alert ID', 'Type', 'Employee', 'Severity', 'Timestamp', 'File / URL', 'Status'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {alerts.map((a: DlpAlert, i: number) => (
                <motion.tr
                  key={a.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: Math.min(i, 20) * 0.02 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30"
                >
                  <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{a.id.slice(0, 8)}</td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${typeColors[a.trigger] ?? 'bg-muted'}`}>
                      {labelTrigger(a.trigger)}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground">{a.employeeName || a.employeeId}</td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium capitalize ${severityColors[a.severity] ?? 'bg-muted'}`}>
                      {a.severity}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">
                    {a.eventAt ? new Date(a.eventAt).toLocaleString() : '—'}
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground max-w-[240px] truncate" title={a.fileOrUrl}>
                    {a.fileOrUrl || '—'}
                  </td>
                  <td className="px-4 py-3">
                    <Select
                      value={a.status}
                      onValueChange={v => patchMutation.mutate({ id: a.id, status: v })}
                    >
                      <SelectTrigger className={`h-8 w-[150px] text-xs ${statusColors[a.status] ?? ''}`}>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="open">Open</SelectItem>
                        <SelectItem value="investigating">Investigating</SelectItem>
                        <SelectItem value="resolved">Resolved</SelectItem>
                        <SelectItem value="false_positive">False Positive</SelectItem>
                      </SelectContent>
                    </Select>
                  </td>
                </motion.tr>
              ))}
            </tbody>
          </table>
          <div ref={sentinelRef} className="h-4" />
          {isFetchingNextPage && (
            <p className="text-center text-xs text-muted-foreground py-3">Loading more…</p>
          )}
          {!hasNextPage && alerts.length > 0 && (
            <p className="text-center text-xs text-muted-foreground py-3">Showing all {total}</p>
          )}
        </div>
      )}
    </div>
  );
}
