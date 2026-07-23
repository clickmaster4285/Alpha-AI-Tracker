'use client'

import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, ChevronDown, ChevronUp, Loader2, Monitor, Cpu, HardDrive } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { activityLogsApi, employeesApi, departmentsApi, type ActivityLog } from '@/lib/api';

const TABS = ['App Log', 'System Log', 'Productive/Unproductive'] as const;
type Tab = typeof TABS[number];

export default function ComprehensiveLogs() {
  const [tab, setTab] = useState<Tab>('App Log');
  const [selectedEmployee, setSelectedEmployee] = useState('');
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedApp, setExpandedApp] = useState<string | null>(null);
  const [page, setPage] = useState(1);

  // Fetch employees for dropdown
  const { data: employeesData } = useQuery({
    queryKey: ['employees', { perPage: 100 }],
    queryFn: () => employeesApi.list({ perPage: 100 }),
  });
  const employees = employeesData?.data || [];

  // Fetch departments for filter
  const { data: deptResponse } = useQuery({
    queryKey: ['departments'],
    queryFn: () => departmentsApi.list(),
  });
  const departments = deptResponse?.departments || [];

  // Fetch activity logs
  const { data: logsData, isLoading: logsLoading, error: logsError } = useQuery({
    queryKey: ['activity-logs', { employeeId: selectedEmployee, search: searchQuery, page }],
    queryFn: () => activityLogsApi.list({
      employeeId: selectedEmployee || undefined,
      search: searchQuery || undefined,
      page,
      perPage: 50,
    }),
  });

  const logs = logsData?.data || [];
  const totalPages = logsData?.totalPages || 1;

  // Group logs by process name for App Log view
  const groupedLogs = useMemo(() => {
    const groups = new Map<string, ActivityLog[]>();
    logs.forEach(log => {
      const key = log.processName;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(log);
    });
    return Array.from(groups.entries()).map(([name, entries]) => ({
      application: name,
      entries,
      totalDuration: entries.length > 0
        ? formatDuration(entries[entries.length - 1].timestamp, entries[0].timestamp)
        : '0m',
    }));
  }, [logs]);

  // Separate foreground (productive) and background (unproductive) logs
  const productiveEntries = useMemo(() => {
    return logs.filter(l => l.isForeground).slice(0, 20);
  }, [logs]);

  const unproductiveEntries = useMemo(() => {
    return logs.filter(l => !l.isForeground).slice(0, 20);
  }, [logs]);

  const selectedEmp = employees.find(e => e.id === selectedEmployee);

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Filters */}
      <div className="flex flex-col lg:flex-row gap-3">
        <div className="flex flex-col sm:flex-row gap-3 flex-1">
          <select
            value={selectedEmployee}
            onChange={e => { setSelectedEmployee(e.target.value); setPage(1); }}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Employees</option>
            {employees.map(e => (
              <option key={e.id} value={e.employeeId}>{e.name} ({e.employeeId})</option>
            ))}
          </select>
          <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 max-w-sm">
            <Search className="w-4 h-4 text-muted-foreground" />
            <input
              value={searchQuery}
              onChange={e => { setSearchQuery(e.target.value); setPage(1); }}
              placeholder="Search by process or window..."
              className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
            />
          </div>
        </div>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          {logs.length} log entries
        </div>
      </div>

      {/* Tabs */}
      <div className="flex border-b border-border gap-1">
        {TABS.map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2.5 text-sm font-medium transition-colors relative
              ${tab === t ? 'text-primary' : 'text-muted-foreground hover:text-foreground'}`}
          >
            {t}
            {tab === t && <motion.div layoutId="log-tab" className="absolute bottom-0 left-0 right-0 h-0.5 gradient-primary rounded-full" />}
          </button>
        ))}
      </div>

      {/* Loading */}
      {logsLoading && (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}

      {/* Error */}
      {logsError && (
        <div className="text-center py-12">
          <p className="text-destructive">Failed to load logs: {(logsError as Error).message}</p>
        </div>
      )}

      {/* Content */}
      {!logsLoading && !logsError && tab === 'App Log' && (
        <div className="bg-card rounded-xl border border-border overflow-hidden">
          {groupedLogs.length === 0 ? (
            <div className="text-center py-12 text-muted-foreground text-sm">
              No activity logs found
            </div>
          ) : (
            <table className="w-full">
              <thead>
                <tr className="border-b border-border">
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Application</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Process Details</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Employee</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Time</th>
                </tr>
              </thead>
              <tbody>
                {groupedLogs.slice(0, 20).map(group => (
                  <tr key={group.application} className="border-b border-border last:border-0 hover:bg-muted/30">
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <div className="w-6 h-6 rounded bg-accent flex items-center justify-center text-[10px] font-bold text-accent-foreground">
                          <Monitor className="w-3.5 h-3.5" />
                        </div>
                        <span className="text-sm font-medium text-foreground">{group.application}</span>
                      </div>
                    </td>
                    <td className="px-4 py-3">
                      {group.entries.slice(0, 3).map((entry, i) => (
                        <p key={i} className="text-sm text-muted-foreground">
                          {entry.windowTitle || entry.processName}
                        </p>
                      ))}
                      {group.entries.length > 3 && (
                        <p className="text-xs text-muted-foreground mt-1">+{group.entries.length - 3} more entries</p>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-foreground">
                      {group.entries[0]?.employeeName || group.entries[0]?.employeeId || '-'}
                    </td>
                    <td className="px-4 py-3 text-sm text-muted-foreground">
                      {new Date(group.entries[0]?.timestamp || '').toLocaleTimeString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {/* Pagination */}
          {totalPages > 1 && (
            <div className="flex items-center justify-between px-4 py-3 border-t border-border">
              <p className="text-sm text-muted-foreground">Page {page} of {totalPages}</p>
              <div className="flex gap-2">
                <button
                  onClick={() => setPage(p => Math.max(1, p - 1))}
                  disabled={page <= 1}
                  className="px-3 py-1 rounded border border-border text-sm disabled:opacity-50 hover:bg-muted"
                >
                  Previous
                </button>
                <button
                  onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                  disabled={page >= totalPages}
                  className="px-3 py-1 rounded border border-border text-sm disabled:opacity-50 hover:bg-muted"
                >
                  Next
                </button>
              </div>
            </div>
          )}
        </div>
      )}

      {!logsLoading && !logsError && tab === 'System Log' && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {logs.slice(0, 20).map(log => (
            <div key={log.id} className="bg-card rounded-xl border border-border p-4">
              <div className="flex items-center gap-3 mb-2">
                <div className="w-8 h-8 rounded-lg bg-accent flex items-center justify-center">
                  <Cpu className="w-4 h-4 text-accent-foreground" />
                </div>
                <div>
                  <p className="text-sm font-medium text-foreground">{log.processName}</p>
                  <p className="text-xs text-muted-foreground">{log.employeeName || log.employeeId}</p>
                </div>
              </div>
              <div className="flex items-center gap-3 text-sm text-muted-foreground mb-1">
                <span className="px-2 py-0.5 rounded text-xs bg-primary/10 text-primary">{log.platform}</span>
                <span>{log.cpuPercent.toFixed(1)}% CPU</span>
                <span>{(log.memoryBytes / 1024 / 1024).toFixed(0)} MB</span>
              </div>
              {log.windowTitle && (
                <p className="text-xs text-muted-foreground mt-1">
                  <span className="font-medium">Window:</span> {log.windowTitle}
                </p>
              )}
              <p className="text-xs text-muted-foreground mt-1">
                {new Date(log.timestamp).toLocaleString()}
              </p>
            </div>
          ))}
          {logs.length === 0 && (
            <div className="col-span-2 text-center py-12 text-muted-foreground text-sm">
              No system logs found
            </div>
          )}
        </div>
      )}

      {!logsLoading && !logsError && tab === 'Productive/Unproductive' && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <div className="bg-card rounded-xl border border-border p-4">
            <h4 className="font-display font-semibold text-foreground mb-3 flex items-center gap-2">
              <span className="w-2 h-2 rounded-full bg-success" />
              Productive (Foreground)
            </h4>
            {productiveEntries.length === 0 ? (
              <p className="text-sm text-muted-foreground">No productive entries</p>
            ) : (
              productiveEntries.map(entry => (
                <div key={entry.id} className="border-b border-border last:border-0">
                  <button
                    onClick={() => setExpandedApp(expandedApp === entry.id ? null : entry.id)}
                    className="w-full flex items-center justify-between py-3 text-sm"
                  >
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-foreground">{entry.processName}</span>
                      <span className="text-muted-foreground">{formatDuration(entry.timestamp, entry.timestamp)}</span>
                    </div>
                    {expandedApp === entry.id ? <ChevronUp className="w-4 h-4 text-muted-foreground" /> : <ChevronDown className="w-4 h-4 text-muted-foreground" />}
                  </button>
                  {expandedApp === entry.id && (
                    <div className="pb-3 pl-4 space-y-1">
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Window:</span> {entry.windowTitle || 'N/A'}
                      </p>
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Employee:</span> {entry.employeeName || entry.employeeId}
                      </p>
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Time:</span> {new Date(entry.timestamp).toLocaleString()}
                      </p>
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
          <div className="bg-card rounded-xl border border-border p-4">
            <h4 className="font-display font-semibold text-foreground mb-3 flex items-center gap-2">
              <span className="w-2 h-2 rounded-full bg-warning" />
              Non Productive (Background)
            </h4>
            {unproductiveEntries.length === 0 ? (
              <p className="text-sm text-muted-foreground">No non-productive entries</p>
            ) : (
              unproductiveEntries.map(entry => (
                <div key={entry.id} className="border-b border-border last:border-0">
                  <button
                    onClick={() => setExpandedApp(expandedApp === entry.id ? null : entry.id)}
                    className="w-full flex items-center justify-between py-3 text-sm"
                  >
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-foreground">{entry.processName}</span>
                      <span className="text-muted-foreground">{formatDuration(entry.timestamp, entry.timestamp)}</span>
                    </div>
                    {expandedApp === entry.id ? <ChevronUp className="w-4 h-4 text-muted-foreground" /> : <ChevronDown className="w-4 h-4 text-muted-foreground" />}
                  </button>
                  {expandedApp === entry.id && (
                    <div className="pb-3 pl-4 space-y-1">
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Window:</span> {entry.windowTitle || 'N/A'}
                      </p>
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Employee:</span> {entry.employeeName || entry.employeeId}
                      </p>
                      <p className="text-sm text-muted-foreground">
                        <span className="font-medium">Time:</span> {new Date(entry.timestamp).toLocaleString()}
                      </p>
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function formatDuration(start: string, end: string): string {
  const diff = new Date(end).getTime() - new Date(start).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return '<1m';
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  const remaining = mins % 60;
  return `${hours}h ${remaining}m`;
}
