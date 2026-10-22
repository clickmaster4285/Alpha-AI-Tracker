// Human-readable labels for the DLP vocabulary.
//
// The server stores machine tokens (`usb_storage`, `blocked`, `false_positive`,
// ...) so the UI must translate them. The maps are DATA, not a hardcoded
// product list — a new token the agent reports simply falls back to a
// prettified version of itself (see `titleize`), so an unrecognised value is
// still readable instead of blank.

import type { DlpAlertStatus, DlpSeverity } from '@/lib/api';

/** Alert type -> short human label. */
export const ALERT_TYPE_LABELS: Record<string, string> = {
  usb_storage: 'USB storage',
  usb_file: 'USB file',
  web_navigate: 'Website visit',
  software_install: 'Software install',
  software_uninstall: 'Software uninstall',
  cloud_sync_folder: 'Cloud sync folder',
  file_transfer: 'File transfer',
  email: 'Email',
  clipboard: 'Clipboard',
  screenshot: 'Screenshot',
  print: 'Print',
  document: 'Document',
};

/** What the client actually did when it raised the alert. */
export const ACTION_LABELS: Record<string, string> = {
  alerted: 'Alerted only',
  blocked: 'Blocked',
  blocked_failed: 'Block failed',
};

export const STATUS_LABELS: Record<DlpAlertStatus, string> = {
  open: 'Open',
  investigating: 'Investigating',
  resolved: 'Resolved',
  false_positive: 'False Positive',
};

export const SEVERITY_LABELS: Record<DlpSeverity, string> = {
  critical: 'Critical',
  high: 'High',
  medium: 'Medium',
  low: 'Low',
};

/** Turn an unknown snake_case token into a readable label. */
export function titleize(token?: string | null): string {
  if (!token) return '—';
  const s = token.replace(/[_-]+/g, ' ').trim();
  if (!s) return '—';
  return s.charAt(0).toUpperCase() + s.slice(1);
}

export function alertTypeLabel(type?: string | null): string {
  if (!type) return 'Unknown event';
  return ALERT_TYPE_LABELS[type] ?? titleize(type);
}

export function actionLabel(action?: string | null): string {
  if (!action) return '—';
  return ACTION_LABELS[action] ?? titleize(action);
}

export function statusLabel(status?: string | null): string {
  if (!status) return '—';
  return STATUS_LABELS[status as DlpAlertStatus] ?? titleize(status);
}

export function severityLabel(severity?: string | null): string {
  if (!severity) return '—';
  return SEVERITY_LABELS[severity as DlpSeverity] ?? titleize(severity);
}

/**
 * Extract a short, human "what happened" string from the evidence object.
 *
 * The evidence JSON is free-form (the agent decides the shape), so we look
 * for the first known, human-meaningful key instead of dumping raw JSON at
 * the user. Falls back to the first scalar value.
 */
export function evidenceSummary(evidence?: Record<string, unknown> | null): string {
  if (!evidence || typeof evidence !== 'object') return '';
  const preferred = [
    'path', 'file', 'domain', 'url', 'app', 'application', 'process',
    'device', 'volume', 'reason', 'action', 'extension', 'target', 'source',
  ];
  for (const key of preferred) {
    const v = evidence[key];
    if (typeof v === 'string' && v.trim()) return v.trim();
    if (typeof v === 'number' && Number.isFinite(v)) return String(v);
  }
  for (const v of Object.values(evidence)) {
    if (typeof v === 'string' && v.trim()) return v.trim();
    if (typeof v === 'number' && Number.isFinite(v)) return String(v);
  }
  return '';
}
