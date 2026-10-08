'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import {
  Plus,
  Shield,
  Trash2,
  Loader2,
  Edit2,
  Search,
  Usb,
  FolderOpen,
  CloudUpload,
  Power,
} from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/components/ui/dialog';
import { departmentsApi, dlpRulesApi, type DlpRule } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import EmptyState from '@/components/employees/EmptyState';
import { cn } from '@/lib/utils';

const severityStyles: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive border-destructive/20',
  high: 'bg-warning/15 text-warning border-warning/20',
  medium: 'bg-info/15 text-info border-info/20',
  low: 'bg-muted text-muted-foreground border-border',
};

const triggerMeta: Record<string, { label: string; icon: typeof Usb; hint: string }> = {
  usb: { label: 'USB device', icon: Usb, hint: 'Alert when a USB / removable device is plugged in' },
  file_transfer: { label: 'File transfer', icon: FolderOpen, hint: 'Alert when files are written to removable media' },
  cloud_upload: { label: 'Cloud upload', icon: CloudUpload, hint: 'Alert when browser URLs match your pattern' },
};

type FormState = {
  name: string;
  trigger: string;
  pattern: string;
  action: string;
  severity: string;
  enabled: boolean;
  applyToAll: boolean;
  departmentIds: number[];
};

const emptyForm = (): FormState => ({
  name: '',
  trigger: 'usb',
  pattern: '',
  action: 'alert_only',
  severity: 'high',
  enabled: true,
  applyToAll: true,
  departmentIds: [],
});

function formFromRule(rule: DlpRule): FormState {
  return {
    name: rule.name,
    trigger: rule.trigger,
    pattern: rule.pattern ?? '',
    action: rule.action || 'alert_only',
    severity: rule.severity || 'high',
    enabled: rule.enabled,
    applyToAll: rule.applyToAll,
    departmentIds: rule.departmentIds ?? [],
  };
}

export default function DLPRulesPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <DlpRulesInner />
    </Suspense>
  );
}

function DlpRulesInner() {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, trigger: {}, severity: {} },
    { q: '', trigger: '', severity: '' },
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

  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<DlpRule | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);

  const { data, isLoading } = useQuery({
    queryKey: ['dlp-rules'],
    queryFn: () => dlpRulesApi.list(),
  });
  const rules = data?.data ?? [];

  const { data: deptsData } = useQuery({
    queryKey: ['departments-all'],
    queryFn: () => departmentsApi.list(),
  });
  const departments = deptsData?.departments ?? [];
  const deptName = useMemo(() => {
    const map = new Map<number, string>();
    for (const d of departments) map.set(d.id, d.name);
    return map;
  }, [departments]);

  const filtered = useMemo(() => {
    const q = filters.q.trim().toLowerCase();
    return rules.filter(r => {
      if (filters.trigger && r.trigger !== filters.trigger) return false;
      if (filters.severity && r.severity !== filters.severity) return false;
      if (!q) return true;
      return (
        r.name.toLowerCase().includes(q) ||
        (r.pattern ?? '').toLowerCase().includes(q) ||
        r.trigger.toLowerCase().includes(q)
      );
    });
  }, [rules, filters]);

  const enabledCount = rules.filter(r => r.enabled).length;

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm());
    setDialogOpen(true);
  };

  const openEdit = (rule: DlpRule) => {
    setEditing(rule);
    setForm(formFromRule(rule));
    setDialogOpen(true);
  };

  const closeDialog = () => {
    setDialogOpen(false);
    setEditing(null);
    setForm(emptyForm());
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const body = {
        name: form.name.trim(),
        trigger: form.trigger,
        pattern: form.pattern.trim(),
        action: form.action,
        severity: form.severity,
        enabled: form.enabled,
        applyToAll: form.applyToAll,
        departmentIds: form.applyToAll ? [] : form.departmentIds,
      };
      if (editing) {
        return dlpRulesApi.update(editing.id, body);
      }
      return dlpRulesApi.create(body);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-rules'] });
      toast.success(editing ? 'Rule updated' : 'Rule created');
      closeDialog();
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to save rule'),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => dlpRulesApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-rules'] });
      toast.success('Rule deleted');
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to delete rule'),
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      dlpRulesApi.update(id, { enabled }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['dlp-rules'] }),
    onError: (err: Error) => toast.error(err.message || 'Failed to update rule'),
  });

  const toggleDept = (id: number) => {
    setForm(f => {
      const has = f.departmentIds.includes(id);
      return {
        ...f,
        departmentIds: has ? f.departmentIds.filter(x => x !== id) : [...f.departmentIds, id],
      };
    });
  };

  const canSave =
    form.name.trim().length > 0 &&
    (form.applyToAll || form.departmentIds.length > 0);

  return (
    <div className="space-y-5 animate-fade-in">
      <div className="flex flex-col sm:flex-row sm:items-end sm:justify-between gap-3">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">DLP Rules</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            {rules.length === 0
              ? 'Define policies for USB, file transfer, and cloud upload events.'
              : `${enabledCount} active · ${rules.length} total — edits apply to agents on next rule pull.`}
          </p>
        </div>
        <Button onClick={openCreate} size="sm" className="gap-1.5 gradient-primary text-primary-foreground self-start sm:self-auto">
          <Plus className="w-4 h-4" /> New Rule
        </Button>
      </div>

      <div className="flex flex-wrap gap-2 items-center">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 min-w-[200px] max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search rules…"
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <Select value={filters.trigger || 'all'} onValueChange={v => setFilters({ trigger: v === 'all' ? '' : v })}>
          <SelectTrigger className="w-[160px]"><SelectValue placeholder="Trigger" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All triggers</SelectItem>
            <SelectItem value="usb">USB</SelectItem>
            <SelectItem value="file_transfer">File Transfer</SelectItem>
            <SelectItem value="cloud_upload">Cloud Upload</SelectItem>
          </SelectContent>
        </Select>
        <Select value={filters.severity || 'all'} onValueChange={v => setFilters({ severity: v === 'all' ? '' : v })}>
          <SelectTrigger className="w-[150px]"><SelectValue placeholder="Severity" /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All severity</SelectItem>
            <SelectItem value="critical">Critical</SelectItem>
            <SelectItem value="high">High</SelectItem>
            <SelectItem value="medium">Medium</SelectItem>
            <SelectItem value="low">Low</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {isLoading ? (
        <div className="flex justify-center py-16"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>
      ) : filtered.length === 0 ? (
        <EmptyState
          icon={Shield}
          text={
            rules.length === 0
              ? 'No DLP rules yet. Create a rule to alert on USB plugs, removable-file transfers, or matching browser URLs.'
              : 'No rules match the current filters.'
          }
        />
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-3">
          {filtered.map((rule, i) => {
            const meta = triggerMeta[rule.trigger] ?? triggerMeta.usb;
            const Icon = meta.icon;
            return (
              <motion.div
                key={rule.id}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: Math.min(i, 12) * 0.03 }}
                className={cn(
                  'bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-all group',
                  !rule.enabled && 'opacity-70',
                )}
              >
                <div className="flex items-start gap-3">
                  <div className={cn(
                    'w-10 h-10 rounded-lg flex items-center justify-center shrink-0',
                    rule.enabled ? 'gradient-primary' : 'bg-muted',
                  )}>
                    <Icon className={cn('w-5 h-5', rule.enabled ? 'text-primary-foreground' : 'text-muted-foreground')} />
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0">
                        <h4 className="font-display font-bold text-foreground truncate">{rule.name}</h4>
                        <p className="text-xs text-muted-foreground mt-0.5">{meta.label}</p>
                      </div>
                      <div className="flex items-center gap-1 shrink-0 opacity-100 sm:opacity-0 sm:group-hover:opacity-100 transition-opacity">
                        <button
                          type="button"
                          title="Edit rule"
                          onClick={() => openEdit(rule)}
                          className="p-1.5 rounded-md hover:bg-muted text-muted-foreground hover:text-foreground"
                        >
                          <Edit2 className="w-3.5 h-3.5" />
                        </button>
                        <button
                          type="button"
                          title="Delete rule"
                          onClick={() => {
                            if (window.confirm(`Delete rule “${rule.name}”?`)) {
                              deleteMutation.mutate(rule.id);
                            }
                          }}
                          className="p-1.5 rounded-md hover:bg-destructive/10 text-muted-foreground hover:text-destructive"
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>
                      </div>
                    </div>

                    <div className="mt-3 rounded-lg bg-muted/50 border border-border/60 px-3 py-2">
                      <p className="text-[10px] uppercase tracking-wide text-muted-foreground font-semibold mb-0.5">Pattern</p>
                      <code className="text-xs text-foreground break-all">
                        {rule.pattern?.trim() ? rule.pattern : '(any — matches all events for this trigger)'}
                      </code>
                    </div>

                    <div className="flex flex-wrap items-center gap-1.5 mt-3">
                      <span className={cn('px-2 py-0.5 rounded-full text-[10px] font-semibold border capitalize', severityStyles[rule.severity] ?? severityStyles.medium)}>
                        {rule.severity}
                      </span>
                      <span className="px-2 py-0.5 rounded-full text-[10px] font-medium bg-accent text-accent-foreground">
                        {rule.action.replace(/_/g, ' ')}
                      </span>
                      {rule.applyToAll ? (
                        <span className="px-2 py-0.5 rounded-full text-[10px] font-medium bg-muted text-muted-foreground">
                          All departments
                        </span>
                      ) : (
                        (rule.departmentIds ?? []).map(id => (
                          <span key={id} className="px-2 py-0.5 rounded-full text-[10px] font-medium bg-muted text-muted-foreground">
                            {deptName.get(id) ?? `Dept #${id}`}
                          </span>
                        ))
                      )}
                    </div>

                    <div className="flex items-center justify-between mt-3 pt-3 border-t border-border">
                      <div className="flex items-center gap-2 text-xs text-muted-foreground">
                        <Power className="w-3.5 h-3.5" />
                        {rule.enabled ? 'Enabled' : 'Disabled'}
                      </div>
                      <Switch
                        checked={rule.enabled}
                        onCheckedChange={checked => toggleMutation.mutate({ id: rule.id, enabled: checked })}
                        aria-label={rule.enabled ? 'Disable rule' : 'Enable rule'}
                      />
                    </div>
                  </div>
                </div>
              </motion.div>
            );
          })}
        </div>
      )}

      <Dialog open={dialogOpen} onOpenChange={open => { if (!open) closeDialog(); }}>
        <DialogContent className="bg-card max-w-lg max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editing ? 'Edit DLP Rule' : 'New DLP Rule'}
            </DialogTitle>
            <DialogDescription>
              {editing
                ? 'Changes sync to desktop agents on their next rule pull (about every 5 minutes).'
                : 'Agents pull active rules and raise alerts when events match.'}
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 mt-1">
            <div className="space-y-1.5">
              <Label htmlFor="dlp-rule-name">Name</Label>
              <Input
                id="dlp-rule-name"
                placeholder="e.g. USB not allowed"
                value={form.name}
                onChange={e => setForm(f => ({ ...f, name: e.target.value }))}
              />
            </div>

            <div className="space-y-1.5">
              <Label>Trigger</Label>
              <div className="grid grid-cols-1 gap-2">
                {Object.entries(triggerMeta).map(([value, meta]) => {
                  const Icon = meta.icon;
                  const active = form.trigger === value;
                  return (
                    <button
                      key={value}
                      type="button"
                      onClick={() => setForm(f => ({ ...f, trigger: value }))}
                      className={cn(
                        'flex items-start gap-3 rounded-lg border px-3 py-2.5 text-left transition-all',
                        active
                          ? 'border-primary/60 bg-primary/10'
                          : 'border-border bg-background hover:border-primary/30',
                      )}
                    >
                      <Icon className={cn('w-4 h-4 mt-0.5 shrink-0', active ? 'text-primary' : 'text-muted-foreground')} />
                      <span>
                        <span className="block text-sm font-medium text-foreground">{meta.label}</span>
                        <span className="block text-[11px] text-muted-foreground mt-0.5">{meta.hint}</span>
                      </span>
                    </button>
                  );
                })}
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="dlp-rule-pattern">Pattern</Label>
              <Input
                id="dlp-rule-pattern"
                placeholder="Keywords or globs, comma-separated (empty = any)"
                value={form.pattern}
                onChange={e => setForm(f => ({ ...f, pattern: e.target.value }))}
              />
              <p className="text-[11px] text-muted-foreground">
                Matches against device name, file path, or URL. Examples: <code className="text-[10px] bg-muted px-1 rounded">*.xlsx</code>, <code className="text-[10px] bg-muted px-1 rounded">drive.google.com</code>
              </p>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-1.5">
                <Label>Action</Label>
                <Select value={form.action} onValueChange={v => setForm(f => ({ ...f, action: v }))}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="alert_only">Alert only</SelectItem>
                    <SelectItem value="block">Block (v2)</SelectItem>
                    <SelectItem value="alert_and_block">Alert + block (v2)</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-1.5">
                <Label>Severity</Label>
                <Select value={form.severity} onValueChange={v => setForm(f => ({ ...f, severity: v }))}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="critical">Critical</SelectItem>
                    <SelectItem value="high">High</SelectItem>
                    <SelectItem value="medium">Medium</SelectItem>
                    <SelectItem value="low">Low</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>

            <div className="rounded-lg border border-border p-3 space-y-3">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <p className="text-sm font-medium text-foreground">Enabled</p>
                  <p className="text-[11px] text-muted-foreground">Disabled rules are ignored by agents</p>
                </div>
                <Switch
                  checked={form.enabled}
                  onCheckedChange={checked => setForm(f => ({ ...f, enabled: checked }))}
                />
              </div>
              <div className="h-px bg-border" />
              <div className="flex items-center justify-between gap-3">
                <div>
                  <p className="text-sm font-medium text-foreground">All departments</p>
                  <p className="text-[11px] text-muted-foreground">Or limit to selected departments</p>
                </div>
                <Switch
                  checked={form.applyToAll}
                  onCheckedChange={checked => setForm(f => ({
                    ...f,
                    applyToAll: checked,
                    departmentIds: checked ? [] : f.departmentIds,
                  }))}
                />
              </div>
              {!form.applyToAll && (
                <div className="flex flex-wrap gap-1.5 pt-1">
                  {departments.length === 0 ? (
                    <p className="text-xs text-muted-foreground">No departments found.</p>
                  ) : (
                    departments.map(d => {
                      const active = form.departmentIds.includes(d.id);
                      return (
                        <button
                          key={d.id}
                          type="button"
                          onClick={() => toggleDept(d.id)}
                          className={cn(
                            'px-2.5 py-1 rounded-full text-xs font-medium border transition-colors',
                            active
                              ? 'border-primary/50 bg-primary/15 text-primary'
                              : 'border-border bg-background text-muted-foreground hover:text-foreground',
                          )}
                        >
                          {d.name}
                        </button>
                      );
                    })
                  )}
                </div>
              )}
            </div>

            <div className="flex gap-2 pt-1">
              <Button type="button" variant="outline" className="flex-1" onClick={closeDialog}>
                Cancel
              </Button>
              <Button
                type="button"
                className="flex-1 gradient-primary text-primary-foreground"
                disabled={!canSave || saveMutation.isPending}
                onClick={() => {
                  if (!form.name.trim()) {
                    toast.error('Name is required');
                    return;
                  }
                  if (!form.applyToAll && form.departmentIds.length === 0) {
                    toast.error('Select at least one department');
                    return;
                  }
                  saveMutation.mutate();
                }}
              >
                {saveMutation.isPending ? 'Saving…' : editing ? 'Save changes' : 'Create rule'}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
