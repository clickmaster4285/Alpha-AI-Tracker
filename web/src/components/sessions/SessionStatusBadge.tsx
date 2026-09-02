'use client';

import { Activity, AlertCircle, CheckCircle2 } from 'lucide-react';
import type { AppSession } from '@/lib/api';

export type SessionStatus = 'ACTIVE' | 'STALE' | 'CLOSED';

export function sessionStatus(s: Pick<AppSession, 'endedAt' | 'status'>): SessionStatus {
  // Server is the authority on the status column (2026-09-02 3-state lifecycle).
  // Pre-031 rows don't carry the field — fall back to the legacy
  // "endedAt ? CLOSED : ACTIVE" interpretation so the UI keeps rendering.
  const declared = s.status?.toUpperCase();
  if (declared === 'ACTIVE' || declared === 'STALE' || declared === 'CLOSED') {
    return declared;
  }
  return s.endedAt ? 'CLOSED' : 'ACTIVE';
}

/** True when the session is genuinely still open on a live tracker. */
export function isSessionLive(s: Pick<AppSession, 'endedAt' | 'status'>): boolean {
  return sessionStatus(s) === 'ACTIVE' && !s.endedAt;
}

/**
 * Status pill used across employee-journey and logs/comprehensive.
 * ACTIVE  → green pulse + "Running"
 * STALE   → amber dot + "Stale · last sync X ago" (computed by caller)
 * CLOSED  → muted dot + "Closed"
 */
export default function SessionStatusBadge({
  status,
  staleSinceLabel,
  className = '',
}: {
  status: SessionStatus;
  staleSinceLabel?: string;
  className?: string;
}) {
  if (status === 'ACTIVE') {
    return (
      <span className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-success/15 text-success ${className}`}>
        <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" />
        <Activity className="w-3 h-3" />
        Running
      </span>
    );
  }
  if (status === 'STALE') {
    return (
      <span
        className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-warning/15 text-warning ${className}`}
        title={staleSinceLabel ? `Tracker last synced ${staleSinceLabel} — may still be running offline` : undefined}
      >
        <span className="w-1.5 h-1.5 rounded-full bg-warning" />
        <AlertCircle className="w-3 h-3" />
        Stale{staleSinceLabel ? ` · ${staleSinceLabel}` : ''}
      </span>
    );
  }
  return (
    <span className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground ${className}`}>
      <CheckCircle2 className="w-3 h-3" />
      Closed
    </span>
  );
}