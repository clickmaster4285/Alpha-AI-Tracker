'use client';

import { useEffect, useState } from 'react';
import { Check, Copy, Loader2, ShieldAlert } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { dlpApi, type DlpAlert, type DlpAlertStatus } from '@/lib/api';
import { actionLabel, alertTypeLabel, severityLabel, statusLabel, titleize } from '@/lib/dlp-labels';
import { formatDateTime } from '@/lib/format';

const SEVERITY_BADGE: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

interface Props {
  alert: DlpAlert | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/**
 * Full-detail popup for a single DLP alert.
 *
 * The list page shows one on-the-point line per alert; everything else
 * (evidence JSON, machine, policy version, rule id, triage fields) lives
 * here so the list stays scannable.
 */
export default function DlpAlertDetailDialog({ alert, open, onOpenChange }: Props) {
  const qc = useQueryClient();
  const [assignedTo, setAssignedTo] = useState('');
  const [notes, setNotes] = useState('');
  const [copied, setCopied] = useState(false);
  // Local status mirror: the parent re-fetches the row after a mutation, but
  // the dialog must show the new value immediately (and keep showing it if
  // the row leaves the current result set).
  const [status, setStatus] = useState<DlpAlertStatus>(alert?.status ?? 'open');

  // Re-seed the editable triage fields whenever a different alert is opened.
  useEffect(() => {
    setAssignedTo(alert?.assignedTo ?? '');
    setNotes(alert?.notes ?? '');
    setCopied(false);
    setStatus((alert?.status as DlpAlertStatus) ?? 'open');
  }, [alert?.id, alert?.assignedTo, alert?.notes, alert?.status]);

  const statusMutation = useMutation({
    mutationFn: ({ id, status: next }: { id: string; status: DlpAlertStatus }) =>
      dlpApi.alerts.update(id, { status: next }),
    onMutate: ({ status: next }) => setStatus(next),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dlp-alerts'] }),
  });

  const detailsMutation = useMutation({
    mutationFn: ({ id, assignedTo: who, note }: { id: string; assignedTo: string; note: string }) =>
      dlpApi.alerts.update(id, { assignedTo: who, notes: note }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['dlp-alerts'] }),
  });

  if (!alert) return null;

  const evidenceEntries = Object.entries(alert.evidence ?? {});
  const statusBusy = statusMutation.isPending;
  const detailsBusy = detailsMutation.isPending;

  const copyEvidence = async () => {
    try {
      await navigator.clipboard.writeText(JSON.stringify(alert.evidence ?? {}, null, 2));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable (insecure context) — ignore */
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <div className="flex items-center gap-2 pr-6">
            <ShieldAlert className="w-5 h-5 text-primary shrink-0" />
            <DialogTitle className="text-base">{alertTypeLabel(alert.type)}</DialogTitle>
            <span
              className={`px-2 py-0.5 rounded-full text-xs font-medium ${SEVERITY_BADGE[alert.severity] ?? ''}`}
            >
              {severityLabel(alert.severity)}
            </span>
          </div>
          <DialogDescription className="text-xs">
            {actionLabel(alert.actionTaken)} · {statusLabel(status)} ·{' '}
            {formatDateTime(alert.occurredAt)}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          {/* ── Facts ─────────────────────────────────────────────── */}
          <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">
            <Fact label="Employee" value={alert.employeeName || alert.employeeId} />
            <Fact label="Employee ID" value={alert.employeeId} mono />
            <Fact label="Machine" value={alert.machineId || '—'} mono />
            <Fact label="Occurred" value={formatDateTime(alert.occurredAt)} />
            <Fact label="Action taken" value={actionLabel(alert.actionTaken)} />
            <Fact label="Policy version" value={alert.policyVersion ? `v${alert.policyVersion}` : '—'} />
            <Fact label="Alert ID" value={alert.id} mono />
            <Fact label="Matched rule" value={alert.ruleId || '—'} mono />
            <Fact label="Synced at" value={formatDateTime(alert.syncedAt)} />
            <Fact label="Raw type" value={titleize(alert.type)} />
          </dl>

          {/* ── Evidence ──────────────────────────────────────────── */}
          <section>
            <div className="flex items-center justify-between mb-2">
              <h3 className="text-sm font-semibold text-foreground">Evidence</h3>
              {evidenceEntries.length > 0 && (
                <button
                  type="button"
                  onClick={copyEvidence}
                  className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors"
                >
                  {copied ? <Check className="w-3.5 h-3.5 text-success" /> : <Copy className="w-3.5 h-3.5" />}
                  {copied ? 'Copied' : 'Copy JSON'}
                </button>
              )}
            </div>
            {evidenceEntries.length === 0 ? (
              <p className="text-xs text-muted-foreground">No evidence was reported with this alert.</p>
            ) : (
              <div className="rounded-lg border border-border bg-muted/30 divide-y divide-border">
                {evidenceEntries.map(([key, value]) => (
                  <div key={key} className="flex items-start gap-3 px-3 py-2">
                    <span className="text-xs font-medium text-muted-foreground w-36 shrink-0 break-words">
                      {titleize(key)}
                    </span>
                    <span className="text-xs text-foreground font-mono break-all min-w-0">
                      {typeof value === 'object' && value !== null
                        ? JSON.stringify(value)
                        : String(value)}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </section>

          {/* ── Triage ────────────────────────────────────────────── */}
          <section className="space-y-3">
            <h3 className="text-sm font-semibold text-foreground">Triage</h3>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label htmlFor="dlp-alert-status" className="text-xs text-muted-foreground">
                  Status
                </Label>
                <Select
                  value={status}
                  disabled={statusBusy}
                  onValueChange={(v) =>
                    statusMutation.mutate({ id: alert.id, status: v as DlpAlertStatus })
                  }
                >
                  <SelectTrigger id="dlp-alert-status" className="h-9">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="open">Open</SelectItem>
                    <SelectItem value="investigating">Investigating</SelectItem>
                    <SelectItem value="resolved">Resolved</SelectItem>
                    <SelectItem value="false_positive">False Positive</SelectItem>
                  </SelectContent>
                </Select>
                {statusMutation.isError && (
                  <p className="text-xs text-destructive">Could not update the status.</p>
                )}
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="dlp-alert-assigned" className="text-xs text-muted-foreground">
                  Assigned to
                </Label>
                <Input
                  id="dlp-alert-assigned"
                  value={assignedTo}
                  onChange={(e) => setAssignedTo(e.target.value)}
                  placeholder="Who is handling this?"
                  className="h-9"
                />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="dlp-alert-notes" className="text-xs text-muted-foreground">
                Notes
              </Label>
              <Textarea
                id="dlp-alert-notes"
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="Investigation notes…"
                rows={3}
              />
            </div>

            <div className="flex items-center justify-end gap-3">
              {detailsMutation.isError && (
                <p className="text-xs text-destructive mr-auto">Could not save the notes.</p>
              )}
              {detailsMutation.isSuccess && (
                <p className="text-xs text-success mr-auto">Saved.</p>
              )}
              <Button
                size="sm"
                variant="outline"
                disabled={detailsBusy}
                onClick={() =>
                  detailsMutation.mutate({ id: alert.id, assignedTo, note: notes })
                }
              >
                {detailsBusy && <Loader2 className="w-4 h-4 animate-spin" />}
                Save details
              </Button>
            </div>
          </section>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd
        className={`text-sm text-foreground break-words ${mono ? 'font-mono text-xs' : ''}`}
        title={value}
      >
        {value || '—'}
      </dd>
    </div>
  );
}
