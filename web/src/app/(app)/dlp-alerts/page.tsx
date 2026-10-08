'use client';

import { Suspense, useEffect, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import {
  Loader2,
  Search,
  ShieldAlert,
  Usb,
  FolderOpen,
  CloudUpload,
  ExternalLink,
  StickyNote,
} from 'lucide-react';
import { useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/components/ui/dialog';
import { dlpAlertsApi, type DlpAlert } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import EmptyState from '@/components/employees/EmptyState';
import { cn } from '@/lib/utils';

const PER_PAGE = 20;

const severityStyles: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive border-destructive/20',
  high: 'bg-warning/15 text-warning border-warning/20',
  medium: 'bg-info/15 text-info border-info/20',
  low: 'bg-muted text-muted-foreground border-border',
};

const statusStyles: Record<string, string> = {
  open: 'bg-destructive/15 text-destructive border-destructive/20',
  investigating: 'bg-warning/15 text-warning border-warning/20',
  resolved: 'bg-success/15 text-success border-success/20',
  false_positive: 'bg-muted text-muted-foreground border-border',
};

const triggerMeta: Record<string, { label: string; icon: typeof Usb; chip: string }> = {
  usb: { label: 'USB', icon: Usb, chip: 'bg-warning/15 text-warning border-warning/20' },
  file_transfer: { label: 'File transfer', icon: FolderOpen, chip: 'bg-info/15 text-info border-info/20' },
  cloud_upload: { label: 'Cloud upload', icon: CloudUpload, chip: 'bg-primary/15 text-primary border-primary/20' },
};

function labelStatus(s: string) {
  return s.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

function formatWhen(iso?: string) {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return iso;
  }
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

  const [selected, setSelected] = useState<DlpAlert | null>(null);
  const [triageStatus, setTriageStatus] = useState('open');
  const [triageNotes, setTriageNotes] = useState('');

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

  const openTriage = (alert: DlpAlert) => {
    setSelected(alert);
    setTriageStatus(alert.status || 'open');
    setTriageNotes(alert.notes || '');
  };

  const closeTriage = () => {
    setSelected(null);
    setTriageNotes('');
  };

  const patchMutation = useMutation({
    mutationFn: ({ id, status, notes }: { id: string; status: string; notes?: string }) =>
      dlpAlertsApi.patch(id, { status, notes }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-alerts'] });
      toast.success('Alert updated');
      closeTriage();
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to update alert'),
  });

  const quickStatus = useMutation({
    mutationFn: ({ id, status }: { id: string; status: string }) =>
      dlpAlertsApi.patch(id, { status }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-alerts'] });
      toast.success('Status updated');
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to update alert'),
  });

  const openCount = alerts.filter(a => a.status === 'open').length;
  const invCount = alerts.filter(a => a.status === 'investigating').length;
  const resCount = alerts.filter(a => a.status === 'resolved').length;

  return (
    <div className="space-y-5 animate-fade-in">
      <div>
        <h3 className="font-display font-bold text-lg text-foreground">DLP Alerts</h3>
        <p className="text-xs text-muted-foreground mt-0.5">
          Policy hits from desktop agents — triage open items and leave notes for your team.
        </p>
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        {[
          { label: 'Open (loaded)', count: openCount, color: 'text-destructive', ring: 'border-destructive/20' },
          { label: 'Investigating', count: invCount, color: 'text-warning', ring: 'border-warning/20' },
          { label: 'Resolved', count: resCount, color: 'text-success', ring: 'border-success/20' },
          { label: 'Total matching', count: total, color: 'text-foreground', ring: 'border-border' },
        ].map(s => (
          <div key={s.label} className={cn('bg-card rounded-xl border p-4 text-center shadow-card', s.ring)}>
            <p className={cn('text-2xl font-display font-bold tabular-nums', s.color)}>{s.count}</p>
            <p className="text-[11px] text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      <div className="flex flex-wrap gap-2 items-center">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 min-w-[200px] max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search employee, path, URL…"
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
          text="No DLP alerts yet. Alerts appear when a desktop agent matches an admin rule against USB, removable-file, or browser URL events."
        />
      ) : (
        <div className="bg-card rounded-xl border border-border overflow-hidden shadow-card">
          <div className="overflow-x-auto">
            <table className="w-full min-w-[960px]">
              <thead>
                <tr className="border-b border-border bg-muted/30">
                  {['Type', 'Employee', 'Severity', 'When', 'File / URL', 'Status', ''].map(h => (
                    <th key={h || 'actions'} className="text-left px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {alerts.map((a: DlpAlert, i: number) => {
                  const meta = triggerMeta[a.trigger] ?? triggerMeta.usb;
                  const Icon = meta.icon;
                  return (
                    <motion.tr
                      key={a.id}
                      initial={{ opacity: 0 }}
                      animate={{ opacity: 1 }}
                      transition={{ delay: Math.min(i, 20) * 0.015 }}
                      className="border-b border-border last:border-0 hover:bg-muted/25 cursor-pointer"
                      onClick={() => openTriage(a)}
                    >
                      <td className="px-4 py-3">
                        <span className={cn('inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium border', meta.chip)}>
                          <Icon className="w-3 h-3" />
                          {meta.label}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <p className="text-sm font-medium text-foreground">{a.employeeName || a.employeeId}</p>
                        {a.employeeName && (
                          <p className="text-[11px] text-muted-foreground font-mono">{a.employeeId}</p>
                        )}
                      </td>
                      <td className="px-4 py-3">
                        <span className={cn('px-2.5 py-1 rounded-full text-xs font-semibold border capitalize', severityStyles[a.severity] ?? severityStyles.medium)}>
                          {a.severity}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">
                        {formatWhen(a.eventAt)}
                      </td>
                      <td className="px-4 py-3 max-w-[280px]">
                        <p className="text-sm text-foreground truncate" title={a.fileOrUrl}>
                          {a.fileOrUrl || '—'}
                        </p>
                        {a.notes ? (
                          <p className="text-[11px] text-muted-foreground mt-0.5 flex items-center gap-1 truncate">
                            <StickyNote className="w-3 h-3 shrink-0" />
                            {a.notes}
                          </p>
                        ) : null}
                      </td>
                      <td className="px-4 py-3" onClick={e => e.stopPropagation()}>
                        <Select
                          value={a.status}
                          onValueChange={v => quickStatus.mutate({ id: a.id, status: v })}
                        >
                          <SelectTrigger className={cn('h-8 w-[148px] text-xs border', statusStyles[a.status] ?? '')}>
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
                      <td className="px-4 py-3 text-right" onClick={e => e.stopPropagation()}>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-8 gap-1 text-xs text-muted-foreground"
                          onClick={() => openTriage(a)}
                        >
                          Review
                          <ExternalLink className="w-3 h-3" />
                        </Button>
                      </td>
                    </motion.tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div ref={sentinelRef} className="h-4" />
          {isFetchingNextPage && (
            <p className="text-center text-xs text-muted-foreground py-3">Loading more…</p>
          )}
          {!hasNextPage && alerts.length > 0 && (
            <p className="text-center text-xs text-muted-foreground py-3 border-t border-border">
              Showing all {total}
            </p>
          )}
        </div>
      )}

      <Dialog open={!!selected} onOpenChange={open => { if (!open) closeTriage(); }}>
        <DialogContent className="bg-card max-w-lg max-h-[90vh] overflow-y-auto">
          {selected && (
            <>
              <DialogHeader>
                <DialogTitle className="font-display">Triage alert</DialogTitle>
                <DialogDescription>
                  Update status and leave a note for other admins.
                </DialogDescription>
              </DialogHeader>

              <div className="space-y-4 mt-1">
                <div className="rounded-xl border border-border bg-muted/30 p-4 space-y-3">
                  <div className="flex flex-wrap items-center gap-2">
                    {(() => {
                      const meta = triggerMeta[selected.trigger] ?? triggerMeta.usb;
                      const Icon = meta.icon;
                      return (
                        <span className={cn('inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium border', meta.chip)}>
                          <Icon className="w-3 h-3" />
                          {meta.label}
                        </span>
                      );
                    })()}
                    <span className={cn('px-2.5 py-1 rounded-full text-xs font-semibold border capitalize', severityStyles[selected.severity] ?? severityStyles.medium)}>
                      {selected.severity}
                    </span>
                    <span className={cn('px-2.5 py-1 rounded-full text-xs font-medium border', statusStyles[selected.status] ?? '')}>
                      {labelStatus(selected.status)}
                    </span>
                  </div>
                  <div>
                    <p className="text-[10px] uppercase tracking-wide text-muted-foreground font-semibold">Employee</p>
                    <p className="text-sm font-medium text-foreground mt-0.5">
                      {selected.employeeName || selected.employeeId}
                      {selected.employeeName ? (
                        <span className="text-muted-foreground font-mono text-xs ml-2">{selected.employeeId}</span>
                      ) : null}
                    </p>
                  </div>
                  <div>
                    <p className="text-[10px] uppercase tracking-wide text-muted-foreground font-semibold">When</p>
                    <p className="text-sm text-foreground mt-0.5">{formatWhen(selected.eventAt)}</p>
                  </div>
                  <div>
                    <p className="text-[10px] uppercase tracking-wide text-muted-foreground font-semibold">File / URL</p>
                    <p className="text-sm text-foreground mt-0.5 break-all font-mono bg-background/80 border border-border rounded-lg px-3 py-2">
                      {selected.fileOrUrl || '—'}
                    </p>
                  </div>
                  <p className="text-[11px] text-muted-foreground font-mono">ID {selected.id}</p>
                </div>

                <div className="space-y-1.5">
                  <Label>Status</Label>
                  <Select value={triageStatus} onValueChange={setTriageStatus}>
                    <SelectTrigger><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="open">Open</SelectItem>
                      <SelectItem value="investigating">Investigating</SelectItem>
                      <SelectItem value="resolved">Resolved</SelectItem>
                      <SelectItem value="false_positive">False Positive</SelectItem>
                    </SelectContent>
                  </Select>
                </div>

                <div className="space-y-1.5">
                  <Label htmlFor="dlp-alert-notes">Notes</Label>
                  <Textarea
                    id="dlp-alert-notes"
                    rows={4}
                    placeholder="Context for the team (optional)"
                    value={triageNotes}
                    onChange={e => setTriageNotes(e.target.value)}
                  />
                </div>

                <div className="flex gap-2 pt-1">
                  <Button type="button" variant="outline" className="flex-1" onClick={closeTriage}>
                    Cancel
                  </Button>
                  <Button
                    type="button"
                    className="flex-1 gradient-primary text-primary-foreground"
                    disabled={patchMutation.isPending}
                    onClick={() =>
                      patchMutation.mutate({
                        id: selected.id,
                        status: triageStatus,
                        notes: triageNotes.trim(),
                      })
                    }
                  >
                    {patchMutation.isPending ? 'Saving…' : 'Save triage'}
                  </Button>
                </div>
              </div>
            </>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
