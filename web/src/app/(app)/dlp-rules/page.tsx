'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Shield, Trash2, Upload, Loader2 } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { dlpApi, type DlpAction, type DlpSeverity, type DlpTrigger } from '@/lib/api';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const triggerLabels: Record<string, string> = {
  usb_storage: 'USB Storage',
  usb_file: 'USB File',
  web_navigate: 'Web Navigate',
  software_install: 'Software Install',
  software_uninstall: 'Software Uninstall',
  cloud_sync_folder: 'Cloud Sync Folder',
  file_transfer: 'File Transfer',
  email: 'Email',
};

const actionLabels: Record<string, string> = {
  alert: 'Alert Only',
  block: 'Block',
  alert_block: 'Alert + Block',
};

export default function DLPRulesPage() {
  const qc = useQueryClient();
  const [showDialog, setShowDialog] = useState(false);
  const [name, setName] = useState('');
  const [trigger, setTrigger] = useState<DlpTrigger>('usb_storage');
  const [pattern, setPattern] = useState('');
  const [action, setAction] = useState<DlpAction>('alert');
  const [severity, setSeverity] = useState<DlpSeverity>('high');
  const [publishMsg, setPublishMsg] = useState('');

  const { data, isLoading, isError } = useQuery({
    queryKey: ['dlp-rules'],
    queryFn: () => dlpApi.rules.list(),
  });

  const createMutation = useMutation({
    mutationFn: () => {
      const match: Record<string, unknown> = {};
      const trimmed = pattern.trim();
      if (trimmed) {
        if (trigger === 'web_navigate') {
          match.domains = trimmed.split(/[,\s]+/).filter(Boolean);
        } else {
          match.extensions = trimmed.split(/[,\s]+/).filter(Boolean);
          match.pattern = trimmed;
        }
      }
      return dlpApi.rules.create({
        name: name.trim(),
        trigger,
        action,
        severity,
        match,
        scope: { all: true },
        enabled: true,
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['dlp-rules'] });
      setShowDialog(false);
      setName('');
      setPattern('');
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => dlpApi.rules.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dlp-rules'] }),
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  const rules = data?.rules ?? [];

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h3 className="font-display font-bold text-lg text-foreground">DLP Rules</h3>
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
          <Button onClick={() => setShowDialog(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground">
            <Plus className="w-4 h-4" /> Add Rule
          </Button>
        </div>
      </div>

      {publishMsg && <p className="text-xs text-success">{publishMsg}</p>}
      {publishMutation.isError && <p className="text-xs text-destructive">Publish failed</p>}

      {isLoading && (
        <div className="flex justify-center py-12"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>
      )}
      {isError && <p className="text-sm text-destructive">Failed to load rules</p>}
      {!isLoading && !isError && rules.length === 0 && (
        <p className="text-sm text-muted-foreground py-8 text-center">No DLP rules yet. Create one and Publish so Windows agents can pull it.</p>
      )}

      <div className="space-y-3">
        {rules.map((rule, i) => (
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
                    Trigger: {triggerLabels[rule.trigger] ?? rule.trigger}
                    {rule.match && Object.keys(rule.match).length > 0 && (
                      <> • Pattern: <code className="bg-muted px-1 rounded text-[10px]">{JSON.stringify(rule.match)}</code></>
                    )}
                  </p>
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
                  <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-muted text-muted-foreground">Disabled</span>
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
        ))}
      </div>

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">New DLP Rule</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Input placeholder="Rule Name *" value={name} onChange={(e) => setName(e.target.value)} />
            <Select value={trigger} onValueChange={(v) => setTrigger(v as DlpTrigger)}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="usb_storage">USB Storage</SelectItem>
                <SelectItem value="usb_file">USB File</SelectItem>
                <SelectItem value="web_navigate">Web Navigate</SelectItem>
                <SelectItem value="cloud_sync_folder">Cloud Sync Folder</SelectItem>
                <SelectItem value="software_install">Software Install</SelectItem>
                <SelectItem value="file_transfer">File Transfer</SelectItem>
                <SelectItem value="email">Email</SelectItem>
              </SelectContent>
            </Select>
            <Input
              placeholder="Pattern / extensions / domains (comma-separated)"
              value={pattern}
              onChange={(e) => setPattern(e.target.value)}
            />
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
            <Button
              className="w-full gradient-primary text-primary-foreground"
              disabled={!name.trim() || createMutation.isPending}
              onClick={() => createMutation.mutate()}
            >
              {createMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Create Rule'}
            </Button>
            {createMutation.isError && <p className="text-xs text-destructive">Create failed</p>}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
