'use client';

import { useMemo } from 'react';
import { AppWindow, Timer, Layers, Activity, Loader2 } from 'lucide-react';
import { useQueries } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import { appSessionsApi, type AppSession } from '@/lib/api';
import { formatDateTime, formatSeconds } from '@/lib/format';

interface AppUsage {
  appDisplayName: string;
  processName: string;
  sessionCount: number;
  foregroundSeconds: number;
  backgroundSeconds: number;
  lastActiveAt: string;
  running: boolean;
}

const AGGREGATE_PAGES = 5; // most recent 5 × 100 = 500 sessions
const PER_PAGE = 100;

export default function EmployeeJourneyApps() {
  return (
    <EmployeePage
      title="App Usage"
      subtitle="Time spent per application across the employee's most recent sessions."
      icon={AppWindow}
    >
      {({ employee }) => <AppUsageBody employeeId={employee.employeeId} />}
    </EmployeePage>
  );
}

function AppUsageBody({ employeeId }: { employeeId: string }) {
  const queries = useQueries({
    queries: Array.from({ length: AGGREGATE_PAGES }, (_, i) => ({
      queryKey: ['app-sessions', 'usage', { employeeId, page: i + 1, perPage: PER_PAGE }],
      queryFn: () => appSessionsApi.list({ employeeId, page: i + 1, perPage: PER_PAGE }),
    })),
  });

  const loading = queries.some(q => q.isLoading);
  const error = queries.find(q => q.isError);
  const sessions: AppSession[] = queries.flatMap(q => q.data?.data ?? []);

  const usage = useMemo(() => {
    const map = new Map<string, AppUsage>();
    for (const s of sessions) {
      const key = s.appDisplayName || s.processName || s.id;
      const entry = map.get(key) ?? {
        appDisplayName: s.appDisplayName,
        processName: s.processName,
        sessionCount: 0,
        foregroundSeconds: 0,
        backgroundSeconds: 0,
        lastActiveAt: s.startedAt,
        running: false,
      };
      entry.sessionCount += 1;
      entry.foregroundSeconds += s.foregroundSeconds ?? 0;
      entry.backgroundSeconds += s.backgroundSeconds ?? 0;
      if (s.startedAt > entry.lastActiveAt) entry.lastActiveAt = s.startedAt;
      if (!s.endedAt) entry.running = true;
      map.set(key, entry);
    }
    return [...map.values()].sort(
      (a, b) => (b.foregroundSeconds + b.backgroundSeconds) - (a.foregroundSeconds + a.backgroundSeconds),
    );
  }, [sessions]);

  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }
  if (error) {
    return (
      <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
        <p className="text-sm text-destructive font-medium">Failed to load app usage</p>
        <p className="text-xs text-muted-foreground">{(error.error as Error)?.message || 'Unknown error'}</p>
      </div>
    );
  }
  if (usage.length === 0) {
    return <EmptyState icon={AppWindow} text="No app usage synced yet" />;
  }

  const totalFg = usage.reduce((n, u) => n + u.foregroundSeconds, 0);
  const totalBg = usage.reduce((n, u) => n + u.backgroundSeconds, 0);
  const runningCount = usage.filter(u => u.running).length;

  return (
    <div className="space-y-4">
      {/* Stat tiles */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <UsageTile icon={AppWindow} label="Applications" value={usage.length} accent="bg-primary/10 text-primary" />
        <UsageTile icon={Layers} label="Sessions" value={sessions.length} accent="bg-info/15 text-info" />
        <UsageTile icon={Timer} label="Active Time" value={formatSeconds(totalFg + totalBg)} accent="bg-success/15 text-success" />
        <UsageTile icon={Activity} label="Open Now" value={runningCount} accent="bg-warning/15 text-warning" />
      </div>

      <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
        <table className="w-full min-w-[760px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Sessions', 'Foreground', 'Background', 'Last Active', 'Status'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {usage.map(u => (
              <tr key={u.appDisplayName + u.processName} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                      <AppWindow className="w-4 h-4 text-primary" />
                    </div>
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-foreground truncate">{u.appDisplayName || u.processName}</p>
                      {u.processName && u.processName !== u.appDisplayName && (
                        <p className="text-xs text-muted-foreground font-mono truncate">{u.processName}</p>
                      )}
                    </div>
                  </div>
                </td>
                <td className="px-4 py-3 text-sm text-foreground font-medium">{u.sessionCount}</td>
                <td className="px-4 py-3 text-sm text-success font-medium whitespace-nowrap">{formatSeconds(u.foregroundSeconds)}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatSeconds(u.backgroundSeconds)}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(u.lastActiveAt)}</td>
                <td className="px-4 py-3">
                  {u.running ? (
                    <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-success/15 text-success">
                      <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" /> Running
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground">
                      <span className="w-1.5 h-1.5 rounded-full bg-muted-foreground" /> Closed
                    </span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function UsageTile({ icon: Icon, label, value, accent }: {
  icon: React.ElementType; label: string; value: number | string; accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center ${accent}`}>
        <Icon className="w-4 h-4" />
      </div>
      <p className="mt-3 text-2xl font-bold text-foreground font-display">{value}</p>
      <p className="text-xs text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}
