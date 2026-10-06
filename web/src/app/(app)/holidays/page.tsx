'use client';

import { Suspense, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { Calendar, Check, Loader2, Plus, Trash2, Edit2, X } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  holidaysApi,
  type Holiday,
  type HolidayInput,
  type HolidayStatus,
} from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import EmptyState from '@/components/employees/EmptyState';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { cn } from '@/lib/utils';

const EMPTY_FORM: HolidayInput = { date: '', label: '', status: 'pending' };

const STATUS_OPTIONS: { value: HolidayStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
];

const STATUS_STYLE: Record<HolidayStatus, string> = {
  pending: 'bg-warning/15 text-warning border-warning/30',
  approved: 'bg-success/15 text-success border-success/30',
  rejected: 'bg-destructive/15 text-destructive border-destructive/30',
};

function normalizeStatus(raw: string | undefined): HolidayStatus {
  if (raw === 'approved' || raw === 'rejected' || raw === 'pending') return raw;
  return 'pending';
}

function HolidaysInner() {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useUrlQueryState<{ status: string }>(
    { status: {} },
    { status: 'all' },
  );
  const statusFilter = (filters.status || 'all') as HolidayStatus | 'all';

  const [showDialog, setShowDialog] = useState(false);
  const [editing, setEditing] = useState<Holiday | null>(null);
  const [form, setForm] = useState<HolidayInput>(EMPTY_FORM);

  const { data, isLoading, error } = useQuery({
    queryKey: ['holidays'],
    queryFn: () => holidaysApi.list(),
  });

  const holidays = useMemo(() => {
    const rows = data?.data ?? [];
    if (statusFilter === 'all') return rows;
    return rows.filter((h) => normalizeStatus(h.status) === statusFilter);
  }, [data?.data, statusFilter]);

  const createMutation = useMutation({
    mutationFn: (payload: HolidayInput) => holidaysApi.create(payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success('Holiday created');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to create holiday', { description: err.message }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, payload }: { id: number; payload: HolidayInput }) =>
      holidaysApi.update(id, payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success('Holiday updated');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to update holiday', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => holidaysApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success('Holiday deleted');
    },
    onError: (err: Error) => toast.error('Failed to delete holiday', { description: err.message }),
  });

  const setStatus = (holiday: Holiday, status: HolidayStatus) => {
    updateMutation.mutate({
      id: holiday.id,
      payload: { date: holiday.date, label: holiday.label, status },
    });
  };

  const openNew = () => {
    setEditing(null);
    setForm(EMPTY_FORM);
    setShowDialog(true);
  };

  const openEdit = (holiday: Holiday) => {
    setEditing(holiday);
    setForm({
      date: holiday.date,
      label: holiday.label,
      status: normalizeStatus(holiday.status),
    });
    setShowDialog(true);
  };

  const save = () => {
    if (!form.date || !form.label.trim()) {
      toast.error('Date and label are required');
      return;
    }
    const payload: HolidayInput = {
      date: form.date,
      label: form.label.trim(),
      status: form.status,
    };
    if (editing) {
      updateMutation.mutate({ id: editing.id, payload });
    } else {
      createMutation.mutate(payload);
    }
  };

  const handleDelete = (holiday: Holiday) => {
    if (
      typeof window !== 'undefined' &&
      !window.confirm(`Delete holiday "${holiday.label}" on ${holiday.date}?`)
    ) {
      return;
    }
    deleteMutation.mutate(holiday.id);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="text-center py-12">
        <p className="text-destructive font-medium">Failed to load holidays</p>
        <p className="text-sm text-muted-foreground mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">Company Holidays</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            Only <span className="font-medium text-foreground">approved</span> holidays are sent to
            desktop clients via <code className="text-xs">/schedules/me</code>.
          </p>
        </div>
        <Button onClick={openNew} size="sm" className="gap-1 gradient-primary text-primary-foreground">
          <Plus className="w-4 h-4" /> Add Holiday
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-1.5">
        {STATUS_OPTIONS.map((opt) => (
          <button
            key={opt.value}
            type="button"
            onClick={() => setFilters({ status: opt.value === 'all' ? '' : opt.value })}
            className={cn(
              'px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors border',
              statusFilter === opt.value
                ? 'bg-primary text-primary-foreground border-primary'
                : 'border-border text-muted-foreground hover:text-foreground hover:bg-muted',
            )}
          >
            {opt.label}
          </button>
        ))}
        <span className="text-xs text-muted-foreground ml-1">
          {holidays.length} shown
          {data?.total != null && data.total !== holidays.length ? ` of ${data.total}` : ''}
        </span>
      </div>

      {holidays.length === 0 ? (
        <EmptyState
          icon={Calendar}
          text={
            statusFilter === 'all'
              ? 'No company holidays configured yet.'
              : `No ${statusFilter} holidays.`
          }
        />
      ) : (
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[640px]">
            <thead>
              <tr className="border-b border-border">
                {['Date', 'Label', 'Status', 'Actions'].map((h) => (
                  <th
                    key={h}
                    className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground"
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {holidays.map((holiday, i) => {
                const status = normalizeStatus(holiday.status);
                return (
                  <motion.tr
                    key={holiday.id}
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ delay: i * 0.02 }}
                    className="border-b border-border last:border-0 hover:bg-muted/30"
                  >
                    <td className="px-4 py-3 text-sm text-foreground font-mono">{holiday.date}</td>
                    <td className="px-4 py-3 text-sm text-foreground">{holiday.label}</td>
                    <td className="px-4 py-3">
                      <span
                        className={cn(
                          'inline-flex items-center rounded-md border px-2 py-0.5 text-xs font-semibold capitalize',
                          STATUS_STYLE[status],
                        )}
                      >
                        {status}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap items-center gap-1">
                        {status !== 'approved' && (
                          <button
                            type="button"
                            onClick={() => setStatus(holiday, 'approved')}
                            disabled={updateMutation.isPending}
                            className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs font-medium text-success hover:bg-success/10"
                            title="Approve"
                          >
                            <Check className="w-3.5 h-3.5" />
                            Approve
                          </button>
                        )}
                        {status !== 'rejected' && (
                          <button
                            type="button"
                            onClick={() => setStatus(holiday, 'rejected')}
                            disabled={updateMutation.isPending}
                            className="inline-flex items-center gap-1 px-2 py-1 rounded-md text-xs font-medium text-destructive hover:bg-destructive/10"
                            title="Reject"
                          >
                            <X className="w-3.5 h-3.5" />
                            Reject
                          </button>
                        )}
                        {status !== 'pending' && (
                          <button
                            type="button"
                            onClick={() => setStatus(holiday, 'pending')}
                            disabled={updateMutation.isPending}
                            className="px-2 py-1 rounded-md text-xs font-medium text-muted-foreground hover:bg-muted"
                            title="Mark pending"
                          >
                            Pending
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() => openEdit(holiday)}
                          className="p-1.5 rounded hover:bg-muted"
                          aria-label={`Edit ${holiday.label}`}
                        >
                          <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                        </button>
                        <button
                          type="button"
                          onClick={() => handleDelete(holiday)}
                          className="p-1.5 rounded hover:bg-destructive/10"
                          aria-label={`Delete ${holiday.label}`}
                        >
                          <Trash2 className="w-3.5 h-3.5 text-destructive" />
                        </button>
                      </div>
                    </td>
                  </motion.tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editing ? 'Edit Holiday' : 'New Holiday'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Date</label>
              <Input
                type="date"
                value={form.date}
                onChange={(e) => setForm({ ...form, date: e.target.value })}
              />
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Label</label>
              <Input
                value={form.label}
                onChange={(e) => setForm({ ...form, label: e.target.value })}
                placeholder="e.g. Independence Day"
              />
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Status</label>
              <select
                value={form.status}
                onChange={(e) =>
                  setForm({ ...form, status: e.target.value as HolidayStatus })
                }
                className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
              >
                <option value="pending">Pending</option>
                <option value="approved">Approved</option>
                <option value="rejected">Rejected</option>
              </select>
              <p className="text-[11px] text-muted-foreground mt-1">
                New holidays default to pending. Approve them before they affect attendance.
              </p>
            </div>
            <Button
              onClick={save}
              disabled={createMutation.isPending || updateMutation.isPending}
              className="w-full gradient-primary text-primary-foreground"
            >
              {createMutation.isPending || updateMutation.isPending
                ? 'Saving…'
                : editing
                  ? 'Update Holiday'
                  : 'Create Holiday'}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function HolidaysPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <HolidaysInner />
    </Suspense>
  );
}
