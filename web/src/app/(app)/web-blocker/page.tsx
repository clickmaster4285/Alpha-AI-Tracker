'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Globe, Loader2, Plus, Trash2, Upload } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { dlpApi, type DlpAction, type DlpSeverity } from '@/lib/api';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const actionLabels: Record<string, string> = {
  alert: 'Alert Only',
  block: 'Block',
  alert_block: 'Alert + Block',
};

const DAY_OPTIONS = [
  { id: 'mon', label: 'Mon' },
  { id: 'tue', label: 'Tue' },
  { id: 'wed', label: 'Wed' },
  { id: 'thu', label: 'Thu' },
  { id: 'fri', label: 'Fri' },
  { id: 'sat', label: 'Sat' },
  { id: 'sun', label: 'Sun' },
] as const;

type DayId = (typeof DAY_OPTIONS)[number]['id'];

function splitList(raw: string): string[] {
  return raw
    .split(/[\n,]+/)
    .map((s) => s.trim())
    .filter(Boolean);
}

export default function WebBlockerPage() {
  const qc = useQueryClient();
  const [showDialog, setShowDialog] = useState(false);
  const [name, setName] = useState('');
  const [domains, setDomains] = useState('');
  const [paths, setPaths] = useState('');
  const [action, setAction] = useState<DlpAction>('alert_block');
  const [severity, setSeverity] = useState<DlpSeverity>('high');
  const [scheduleOn, setScheduleOn] = useState(false);
  const [scheduleDays, setScheduleDays] = useState<DayId[]>(['mon', 'tue', 'wed', 'thu', 'fri']);
  const [startTime, setStartTime] = useState('09:00');
  const [endTime, setEndTime] = useState('18:00');
  const [timezone, setTimezone] = useState(
    () => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  );
  const [publishMsg, setPublishMsg] = useState('');

  const { data, isLoading, isError } = useQuery({
    queryKey: ['web-blocker-rules'],
    queryFn: () => dlpApi.webBlocker.list(),
  });

  const rules = data?.rules ?? [];

  const createMutation = useMutation({
    mutationFn: () => {
      const domainList = splitList(domains);
      const pathList = splitList(paths);
      if (domainList.length === 0 && pathList.length === 0) {
        return Promise.reject(new Error('Add at least one domain or URL path'));
      }
      return dlpApi.webBlocker.create({
        name: name.trim(),
        action,
        severity,
        domains: domainList,
        paths: pathList,
        scheduleEnabled: scheduleOn,
        scheduleStart: scheduleOn ? startTime : '',
        scheduleEnd: scheduleOn ? endTime : '',
        scheduleTimezone: scheduleOn ? timezone.trim() || 'UTC' : 'UTC',
        scheduleDays: scheduleOn ? scheduleDays : [],
        scopeAll: true,
        enabled: true,
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['web-blocker-rules'] });
      setShowDialog(false);
      setName('');
      setDomains('');
      setPaths('');
      setScheduleOn(false);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => dlpApi.webBlocker.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['web-blocker-rules'] }),
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  const toggleDay = (id: DayId) => {
    setScheduleDays((prev) =>
      prev.includes(id) ? prev.filter((d) => d !== id) : [...prev, id],
    );
  };

  const canCreate =
    name.trim().length > 0 &&
    (splitList(domains).length > 0 || splitList(paths).length > 0) &&
    (!scheduleOn || scheduleDays.length > 0);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">Web Blocker</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            Relational web rules (domains / paths / schedule). Domains hard-block on Windows after
            Publish (hosts + firewall). Paths are alert-match only.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-1"
            disabled={publishMutation.isPending}
            onClick={() => publishMutation.mutate()}
          >
            {publishMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
            Publish
          </Button>
          <Button
            onClick={() => setShowDialog(true)}
            size="sm"
            className="gap-1 gradient-primary text-primary-foreground"
          >
            <Plus className="w-4 h-4" /> Add rule
          </Button>
        </div>
      </div>

      {publishMsg && <p className="text-xs text-success">{publishMsg}</p>}
      {publishMutation.isError && <p className="text-xs text-destructive">Publish failed</p>}

      {isLoading && (
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}
      {isError && <p className="text-sm text-destructive">Failed to load web blocker rules</p>}
      {!isLoading && !isError && rules.length === 0 && (
        <p className="text-sm text-muted-foreground py-8 text-center">
          No web block rules yet. Add domains or paths, then Publish so Windows agents can pull them.
        </p>
      )}

      <div className="space-y-3">
        {rules.map((rule, i) => {
          const sched =
            rule.scheduleEnabled
              ? `${(rule.scheduleDays ?? []).join(', ') || 'all days'} · ${rule.scheduleStart || '?'}–${rule.scheduleEnd || '?'} (${rule.scheduleTimezone || 'UTC'})`
              : null;
          return (
            <motion.div
              key={rule.id}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: i * 0.03 }}
              className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="flex items-center gap-3 min-w-0">
                  <Globe className="w-5 h-5 text-primary flex-shrink-0" />
                  <div className="min-w-0 space-y-1">
                    <h4 className="font-display font-bold text-foreground truncate">{rule.name}</h4>
                    {(rule.domains?.length ?? 0) > 0 && (
                      <p className="text-xs text-muted-foreground">
                        Domains:{' '}
                        <code className="bg-muted px-1 rounded text-[10px]">{rule.domains.join(', ')}</code>
                      </p>
                    )}
                    {(rule.paths?.length ?? 0) > 0 && (
                      <p className="text-xs text-muted-foreground">
                        Paths:{' '}
                        <code className="bg-muted px-1 rounded text-[10px]">{rule.paths.join(', ')}</code>
                        <span className="ml-1 text-[10px] text-warning">(alert match)</span>
                      </p>
                    )}
                    {sched && <p className="text-xs text-muted-foreground">Schedule: {sched}</p>}
                  </div>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${severityColors[rule.severity] ?? ''}`}>
                    {rule.severity}
                  </span>
                  <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-accent text-accent-foreground">
                    {actionLabels[rule.action] ?? rule.action}
                  </span>
                  {!rule.enabled && (
                    <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-muted text-muted-foreground">
                      Disabled
                    </span>
                  )}
                  <button
                    type="button"
                    onClick={() => deleteMutation.mutate(rule.id)}
                    className="p-1.5 rounded hover:bg-destructive/10"
                    aria-label="Delete rule"
                  >
                    <Trash2 className="w-3.5 h-3.5 text-destructive" />
                  </button>
                </div>
              </div>
            </motion.div>
          );
        })}
      </div>

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card max-h-[90vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="font-display">New web block rule</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <Input placeholder="Rule name *" value={name} onChange={(e) => setName(e.target.value)} />
            <div>
              <label className="text-xs font-medium text-muted-foreground">Domains</label>
              <Input
                className="mt-1"
                placeholder="example.com, *.dropbox.com"
                value={domains}
                onChange={(e) => setDomains(e.target.value)}
              />
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground">URL paths</label>
              <Input
                className="mt-1"
                placeholder="/share/*, /c/chat"
                value={paths}
                onChange={(e) => setPaths(e.target.value)}
              />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Select value={action} onValueChange={(v) => setAction(v as DlpAction)}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="alert">Alert Only</SelectItem>
                  <SelectItem value="block">Block</SelectItem>
                  <SelectItem value="alert_block">Alert + Block</SelectItem>
                </SelectContent>
              </Select>
              <Select value={severity} onValueChange={(v) => setSeverity(v as DlpSeverity)}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="critical">Critical</SelectItem>
                  <SelectItem value="high">High</SelectItem>
                  <SelectItem value="medium">Medium</SelectItem>
                  <SelectItem value="low">Low</SelectItem>
                </SelectContent>
              </Select>
            </div>

            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={scheduleOn}
                onChange={(e) => setScheduleOn(e.target.checked)}
                className="rounded border-border"
              />
              Timer-based restriction
            </label>

            {scheduleOn && (
              <div className="space-y-2 rounded-lg border border-border p-3 bg-muted/30">
                <div className="flex flex-wrap gap-1.5">
                  {DAY_OPTIONS.map((d) => {
                    const on = scheduleDays.includes(d.id);
                    return (
                      <button
                        key={d.id}
                        type="button"
                        onClick={() => toggleDay(d.id)}
                        className={`px-2 py-1 rounded text-xs font-medium border ${
                          on
                            ? 'bg-primary text-primary-foreground border-primary'
                            : 'bg-card text-muted-foreground border-border'
                        }`}
                      >
                        {d.label}
                      </button>
                    );
                  })}
                </div>
                <div className="grid grid-cols-2 gap-2">
                  <div>
                    <label className="text-[10px] text-muted-foreground">Start</label>
                    <Input type="time" value={startTime} onChange={(e) => setStartTime(e.target.value)} />
                  </div>
                  <div>
                    <label className="text-[10px] text-muted-foreground">End</label>
                    <Input type="time" value={endTime} onChange={(e) => setEndTime(e.target.value)} />
                  </div>
                </div>
                <Input
                  value={timezone}
                  onChange={(e) => setTimezone(e.target.value)}
                  placeholder="Asia/Karachi"
                />
              </div>
            )}

            <Button
              className="w-full gradient-primary text-primary-foreground"
              disabled={!canCreate || createMutation.isPending}
              onClick={() => createMutation.mutate()}
            >
              {createMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Create rule'}
            </Button>
            {createMutation.isError && (
              <p className="text-xs text-destructive">
                {(createMutation.error as Error)?.message || 'Create failed'}
              </p>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
