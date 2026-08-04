'use client'

import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, ChevronDown, ChevronUp, Loader2, Monitor, Globe, FolderOpen, ExternalLink } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { appSessionsApi, appItemsApi, employeesApi, type AppSession, type AppItem } from '@/lib/api';

export default function ComprehensiveLogs() {
  const [selectedEmployee, setSelectedEmployee] = useState('');
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedSession, setExpandedSession] = useState<string | null>(null);
  const [page, setPage] = useState(1);

  // Fetch employees for dropdown
  const { data: employeesData } = useQuery({
    queryKey: ['employees', { perPage: 100 }],
    queryFn: () => employeesApi.list({ perPage: 100 }),
  });
  const employees = employeesData?.data || [];

  // Fetch app sessions (replaces old activity-logs)
  const { data: sessionsData, isLoading, error } = useQuery({
    queryKey: ['app-sessions', { employeeId: selectedEmployee, search: searchQuery, page }],
    queryFn: () => appSessionsApi.list({
      employeeId: selectedEmployee || undefined,
      search: searchQuery || undefined,
      page,
      perPage: 50,
    }),
  });

  const sessions = sessionsData?.data || [];
  const totalPages = sessionsData?.totalPages || 1;

  // Fetch app items for URL display
  const { data: itemsData } = useQuery({
    queryKey: ['app-items', { employeeId: selectedEmployee, itemType: 'browser_navigation' }],
    queryFn: () => appItemsApi.list({
      employeeId: selectedEmployee || undefined,
      itemType: 'browser_navigation',
      perPage: 100,
    }),
    enabled: !!selectedEmployee,
  });

  const browserNavItems = (itemsData?.data || []) as AppItem[];
  const selectedEmp = employees.find(e => e.id === selectedEmployee);

  // Group URLs by session for display
  const urlsBySession = useMemo(() => {
    const map = new Map<string, AppItem[]>();
    for (const item of browserNavItems) {
      if (!map.has(item.appSessionId)) map.set(item.appSessionId, []);
      map.get(item.appSessionId)!.push(item);
    }
    return map;
  }, [browserNavItems]);

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
          {sessions.length} app sessions
        </div>
      </div>

      {/* Loading */}
      {isLoading && (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="text-center py-12">
          <p className="text-destructive">Failed to load app sessions: {(error as Error).message}</p>
        </div>
      )}

      {/* App Sessions Table */}
      {!isLoading && !error && (
        <div className="bg-card rounded-xl border border-border overflow-hidden">
          {sessions.length === 0 ? (
            <div className="text-center py-12 text-muted-foreground text-sm">
              No app sessions found
            </div>
          ) : (
            <table className="w-full">
              <thead>
                <tr className="border-b border-border">
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Application</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Employee</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Platform</th>
                  <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Duration</th>
                </tr>
              </thead>
              <tbody>
                {sessions.map(session => (
                  <tr key={session.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <div className="w-6 h-6 rounded bg-accent flex items-center justify-center text-[10px] font-bold text-accent-foreground">
                          <Monitor className="w-3.5 h-3.5" />
                        </div>
                        <span className="text-sm font-medium text-foreground">{session.processName}</span>
                      </div>
                      {session.appDisplayName && session.appDisplayName !== session.processName && (
                        <p className="text-xs text-muted-foreground ml-8">{session.appDisplayName}</p>
                      )}
                      {urlsBySession.get(session.id)?.length > 0 && (
                        <div className="mt-1 ml-8 space-y-0.5">
                          {urlsBySession.get(session.id)!.slice(0, 3).map((item) => {
                            const isUrl = item.identifier.startsWith('http://') || item.identifier.startsWith('https://');
                            return (
                              <span key={item.id} className="text-xs text-muted-foreground flex items-center gap-1">
                                {isUrl ? (
                                  <a href={item.identifier} target="_blank" rel="noopener noreferrer" className="text-blue-400 hover:text-blue-300 flex items-center gap-1">
                                    <ExternalLink className="w-3 h-3" />
                                    {item.title || item.identifier}
                                  </a>
                                ) : (
                                  <>
                                    <Globe className="w-3 h-3" />
                                    {item.title || item.identifier}
                                  </>
                                )}
                              </span>
                            );
                          })}
                          {urlsBySession.get(session.id)!.length > 3 && (
                            <p className="text-xs text-muted-foreground">+{urlsBySession.get(session.id)!.length - 3} more</p>
                          )}
                        </div>
                      )}
                    </td>
                    <td className="px-4 py-3 text-sm text-foreground">
                      {session.employeeId}
                    </td>
                    <td className="px-4 py-3">
                      <span className="px-2 py-0.5 rounded text-xs bg-primary/10 text-primary">
                        {session.platform || 'unknown'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-sm text-muted-foreground">
                      <div className="flex items-center gap-1">
                        {new Date(session.startedAt).toLocaleTimeString()}
                        {session.endedAt && (
                          <span className="text-xs">
                            → {new Date(session.endedAt).toLocaleTimeString()}
                          </span>
                        )}
                      </div>
                      <p className="text-xs">
                        {formatDuration(session.startedAt, session.endedAt || new Date().toISOString())}
                      </p>
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
