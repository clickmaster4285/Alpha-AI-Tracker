'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Calendar, Loader2, Plus, Trash2, Edit2 } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { holidaysApi, type Holiday, type HolidayInput } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import EmptyState from '@/components/employees/EmptyState';

const EMPTY_FORM: HolidayInput = { date: '', label: '' };

export default function HolidaysPage() {
  const queryClient = useQueryClient();
  const [showDialog, setShowDialog] = useState(false);
  const [editing, setEditing] = useState<Holiday | null>(null);
  const [form, setForm] = useState<HolidayInput>(EMPTY_FORM);

  const { data, isLoading, error } = useQuery({
    queryKey: ['holidays'],
    queryFn: () => holidaysApi.list(),
  });

  const holidays = data?.data ?? [];

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

  const openNew = () => {
    setEditing(null);
    setForm(EMPTY_FORM);
    setShowDialog(true);
  };

  const openEdit = (holiday: Holiday) => {
    setEditing(holiday);
    setForm({ date: holiday.date, label: holiday.label });
    setShowDialog(true);
  };

  const save = () => {
    if (!form.date || !form.label.trim()) {
      toast.error('Date and label are required');
      return;
    }
    if (editing) {
      updateMutation.mutate({ id: editing.id, payload: form });
    } else {
      createMutation.mutate(form);
    }
  };

  const handleDelete = (holiday: Holiday) => {
    if (typeof window !== 'undefined' && !window.confirm(`Delete holiday "${holiday.label}" on ${holiday.date}?`)) {
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
            Holidays are mirrored to desktop clients through <code className="text-xs">/schedules/me</code>.
          </p>
        </div>
        <Button onClick={openNew} size="sm" className="gap-1 gradient-primary text-primary-foreground">
          <Plus className="w-4 h-4" /> Add Holiday
        </Button>
      </div>

      {holidays.length === 0 ? (
        <EmptyState icon={Calendar} text="No company holidays configured yet." />
      ) : (
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[520px]">
            <thead>
              <tr className="border-b border-border">
                {['Date', 'Label', 'Actions'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {holidays.map((holiday, i) => (
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
                    <div className="flex gap-1">
                      <button
                        onClick={() => openEdit(holiday)}
                        className="p-1.5 rounded hover:bg-muted"
                        aria-label={`Edit ${holiday.label}`}
                      >
                        <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                      </button>
                      <button
                        onClick={() => handleDelete(holiday)}
                        className="p-1.5 rounded hover:bg-destructive/10"
                        aria-label={`Delete ${holiday.label}`}
                      >
                        <Trash2 className="w-3.5 h-3.5 text-destructive" />
                      </button>
                    </div>
                  </td>
                </motion.tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader>
            <DialogTitle className="font-display">{editing ? 'Edit Holiday' : 'New Holiday'}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Date</label>
              <Input
                type="date"
                value={form.date}
                onChange={e => setForm({ ...form, date: e.target.value })}
              />
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Label</label>
              <Input
                value={form.label}
                onChange={e => setForm({ ...form, label: e.target.value })}
                placeholder="e.g. Independence Day"
              />
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
