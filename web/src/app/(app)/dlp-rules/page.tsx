'use client';

import { Suspense, useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Shield, Trash2, Loader2 } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { departmentsApi, dlpRulesApi, type DlpRule } from '@/lib/api';
import EmptyState from '@/components/employees/EmptyState';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

export default function DLPRulesPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <DlpRulesInner />
    </Suspense>
  );
}

function DlpRulesInner() {
  const queryClient = useQueryClient();
  const [showDialog, setShowDialog] = useState(false);
  const [name, setName] = useState('');
  const [trigger, setTrigger] = useState('usb');
  const [pattern, setPattern] = useState('');
  const [action, setAction] = useState('alert_only');
  const [severity, setSeverity] = useState('high');
  const [applyToAll, setApplyToAll] = useState(true);
  const [deptId, setDeptId] = useState<string>('');

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

  const createMutation = useMutation({
    mutationFn: () =>
      dlpRulesApi.create({
        name: name.trim(),
        trigger,
        pattern: pattern.trim(),
        action,
        severity,
        enabled: true,
        applyToAll,
        departmentIds: applyToAll || !deptId ? undefined : [Number(deptId)],
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dlp-rules'] });
      toast.success('Rule created');
      setShowDialog(false);
      setName('');
      setPattern('');
      setApplyToAll(true);
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to create rule'),
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

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between">
        <h3 className="font-display font-bold text-lg text-foreground">DLP Rules</h3>
        <Button onClick={() => setShowDialog(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground">
          <Plus className="w-4 h-4" /> Add Rule
        </Button>
      </div>

      {isLoading ? (
        <div className="flex justify-center py-16"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>
      ) : rules.length === 0 ? (
        <EmptyState
          icon={Shield}
          text="No DLP rules yet. Create a rule to alert on USB plugs, removable-file transfers, or browser URLs matching your patterns."
        />
      ) : (
        <div className="space-y-3">
          {rules.map((rule: DlpRule, i: number) => (
            <motion.div
              key={rule.id}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: i * 0.03 }}
              className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="flex items-center gap-3 min-w-0">
                  <Shield className="w-5 h-5 text-primary flex-shrink-0" />
                  <div className="min-w-0">
                    <h4 className="font-display font-bold text-foreground truncate">{rule.name}</h4>
                    <p className="text-xs text-muted-foreground mt-0.5">
                      Trigger: {rule.trigger} • Pattern:{' '}
                      <code className="bg-muted px-1 rounded text-[10px]">{rule.pattern || '(any)'}</code>
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium capitalize ${severityColors[rule.severity] ?? 'bg-muted'}`}>
                    {rule.severity}
                  </span>
                  <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-accent text-accent-foreground">
                    {rule.action.replace(/_/g, ' ')}
                  </span>
                  <Button
                    variant="outline"
                    size="sm"
                    className="h-7 text-xs"
                    onClick={() => toggleMutation.mutate({ id: rule.id, enabled: !rule.enabled })}
                  >
                    {rule.enabled ? 'Enabled' : 'Disabled'}
                  </Button>
                  <button
                    type="button"
                    className="p-1.5 rounded hover:bg-destructive/10"
                    onClick={() => deleteMutation.mutate(rule.id)}
                  >
                    <Trash2 className="w-3.5 h-3.5 text-destructive" />
                  </button>
                </div>
              </div>
              <div className="flex gap-1 mt-3 flex-wrap">
                {rule.applyToAll ? (
                  <span className="px-2 py-0.5 rounded bg-muted text-muted-foreground text-[10px] font-medium">All Departments</span>
                ) : (
                  (rule.departmentIds ?? []).map(id => (
                    <span key={id} className="px-2 py-0.5 rounded bg-muted text-muted-foreground text-[10px] font-medium">
                      Dept #{id}
                    </span>
                  ))
                )}
              </div>
            </motion.div>
          ))}
        </div>
      )}

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">New DLP Rule</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Input placeholder="Rule Name *" value={name} onChange={e => setName(e.target.value)} />
            <Select value={trigger} onValueChange={setTrigger}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="usb">USB</SelectItem>
                <SelectItem value="file_transfer">File Transfer</SelectItem>
                <SelectItem value="cloud_upload">Cloud Upload</SelectItem>
              </SelectContent>
            </Select>
            <Input
              placeholder="Pattern / keywords (comma-separated; empty = any)"
              value={pattern}
              onChange={e => setPattern(e.target.value)}
            />
            <div className="grid grid-cols-2 gap-3">
              <Select value={action} onValueChange={setAction}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="alert_only">Alert Only</SelectItem>
                  <SelectItem value="block">Block (v2)</SelectItem>
                  <SelectItem value="alert_and_block">Alert + Block (v2)</SelectItem>
                </SelectContent>
              </Select>
              <Select value={severity} onValueChange={setSeverity}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="critical">Critical</SelectItem>
                  <SelectItem value="high">High</SelectItem>
                  <SelectItem value="medium">Medium</SelectItem>
                  <SelectItem value="low">Low</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <Select
              value={applyToAll ? 'all' : 'dept'}
              onValueChange={v => setApplyToAll(v === 'all')}
            >
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="all">All Departments</SelectItem>
                <SelectItem value="dept">One Department</SelectItem>
              </SelectContent>
            </Select>
            {!applyToAll && (
              <Select value={deptId} onValueChange={setDeptId}>
                <SelectTrigger><SelectValue placeholder="Department" /></SelectTrigger>
                <SelectContent>
                  {departments.map(d => (
                    <SelectItem key={d.id} value={String(d.id)}>{d.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <Button
              className="w-full gradient-primary text-primary-foreground"
              disabled={!name.trim() || createMutation.isPending}
              onClick={() => createMutation.mutate()}
            >
              {createMutation.isPending ? 'Creating…' : 'Create Rule'}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
