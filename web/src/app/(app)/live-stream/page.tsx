'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, Clock3, Eye, Loader2, Monitor, RefreshCw, ShieldCheck, Square } from 'lucide-react';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { employeesApi, liveViewApi, type Employee, type LiveViewSession } from '@/lib/api';

const PAGE_SIZE = 20;
const durations = [5, 15, 30];

export default function LiveStream() {
  return (
    <Suspense fallback={<div className="flex min-h-[400px] items-center justify-center"><Loader2 className="h-8 w-8 animate-spin text-primary" /></div>}>
      <LiveStreamInner />
    </Suspense>
  );
}

function LiveStreamInner() {
  const queryClient = useQueryClient();
  const [employeeSearch, setEmployeeSearch] = useState('');
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null);
  const [reason, setReason] = useState('');
  const [durationMinutes, setDurationMinutes] = useState(5);
  const [pickerOpen, setPickerOpen] = useState(false);
  const employeeSentinel = useRef<HTMLDivElement | null>(null);
  const sessionSentinel = useRef<HTMLDivElement | null>(null);

  const employeesQuery = useInfiniteQuery({
    queryKey: ['live-view-employees', employeeSearch],
    queryFn: ({ pageParam }) => employeesApi.list({
      page: pageParam,
      perPage: PAGE_SIZE,
      search: employeeSearch || undefined,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => last.page < last.totalPages ? last.page + 1 : undefined,
  });
  const employees = useMemo(
    () => employeesQuery.data?.pages.flatMap((page) => page.data) ?? [],
    [employeesQuery.data],
  );

  const sessionsQuery = useInfiniteQuery({
    queryKey: ['live-view-sessions'],
    queryFn: ({ pageParam }) => liveViewApi.list({ page: pageParam, perPage: PAGE_SIZE }),
    initialPageParam: 1,
    getNextPageParam: (last) => last.page < last.totalPages ? last.page + 1 : undefined,
  });
  const sessions = useMemo(
    () => sessionsQuery.data?.pages.flatMap((page) => page.data) ?? [],
    [sessionsQuery.data],
  );

  useEffect(() => {
    const element = employeeSentinel.current;
    if (!element) return;
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting && employeesQuery.hasNextPage && !employeesQuery.isFetchingNextPage) {
          employeesQuery.fetchNextPage();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [employeesQuery.hasNextPage, employeesQuery.isFetchingNextPage, employeesQuery.fetchNextPage]);

  useEffect(() => {
    const element = sessionSentinel.current;
    if (!element) return;
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting && sessionsQuery.hasNextPage && !sessionsQuery.isFetchingNextPage) {
          sessionsQuery.fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [sessionsQuery.hasNextPage, sessionsQuery.isFetchingNextPage, sessionsQuery.fetchNextPage]);

  const createMutation = useMutation({
    mutationFn: () => {
      if (!selectedEmployee) throw new Error('Select an employee first');
      return liveViewApi.create({
        employeeId: selectedEmployee.employeeId,
        reason,
        durationMinutes,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['live-view-sessions'] });
      toast.success('Viewing request created', { description: 'The employee must consent before media can start.' });
      setReason('');
      setSelectedEmployee(null);
      setPickerOpen(false);
    },
    onError: (error: Error) => toast.error('Could not create viewing request', { description: error.message }),
  });

  const stopMutation = useMutation({
    mutationFn: (id: string) => liveViewApi.stop(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['live-view-sessions'] });
      toast.success('Viewing session stopped');
    },
    onError: (error: Error) => toast.error('Could not stop session', { description: error.message }),
  });

  return (
    <div className="space-y-5 animate-fade-in">
      <div className="rounded-xl border border-amber-500/30 bg-amber-500/10 p-4">
        <div className="flex gap-3">
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" />
          <div>
            <h2 className="font-display font-semibold text-foreground">Live-only, consent-first viewing</h2>
            <p className="mt-1 text-sm text-muted-foreground">
              This control plane creates an audited request only. Screen media, recording, audio, and
              playback are not enabled yet. A future client release must show the employee an indicator
              and request OS capture consent before publishing anything.
            </p>
          </div>
        </div>
      </div>

      <section className="rounded-xl border border-border bg-card p-5">
        <div className="mb-4 flex items-center gap-3">
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10"><Monitor className="h-5 w-5 text-primary" /></div>
          <div>
            <h1 className="font-display text-lg font-semibold text-foreground">Request employee screen view</h1>
            <p className="text-sm text-muted-foreground">Every request has a reason and a maximum 30-minute lease.</p>
          </div>
        </div>

        <div className="grid gap-4 lg:grid-cols-[1.2fr_1fr_180px_auto] lg:items-end">
          <label className="relative block text-sm font-medium text-foreground">
            Employee
            <button type="button" onClick={() => setPickerOpen((open) => !open)} className="mt-1 flex h-10 w-full items-center justify-between rounded-lg border border-border bg-background px-3 text-left font-normal">
              <span className={selectedEmployee ? 'text-foreground' : 'text-muted-foreground'}>
                {selectedEmployee ? `${selectedEmployee.name} (${selectedEmployee.employeeId})` : 'Select an employee'}
              </span>
              <Eye className="h-4 w-4 text-muted-foreground" />
            </button>
            {pickerOpen && (
              <div className="absolute z-20 mt-2 w-full rounded-lg border border-border bg-card p-2 shadow-xl">
                <input
                  value={employeeSearch}
                  onChange={(event) => setEmployeeSearch(event.target.value)}
                  placeholder="Search name or employee ID"
                  className="mb-2 h-9 w-full rounded-md border border-border bg-background px-2 text-sm outline-none focus:border-primary"
                  autoFocus
                />
                <div className="max-h-56 overflow-y-auto">
                  {employees.map((employee) => (
                    <button key={employee.id} type="button" onClick={() => { setSelectedEmployee(employee); setPickerOpen(false); }} className="flex w-full items-center justify-between rounded-md px-2 py-2 text-left text-sm hover:bg-muted">
                      <span><span className="font-medium">{employee.name}</span><span className="ml-2 text-muted-foreground">{employee.employeeId}</span></span>
                      <span className={employee.isOnline ? 'text-emerald-500' : 'text-muted-foreground'}>{employee.isOnline ? 'Online' : 'Offline'}</span>
                    </button>
                  ))}
                  {employeesQuery.isLoading && <Loader2 className="mx-auto my-3 h-4 w-4 animate-spin" />}
                  {!employeesQuery.isLoading && employees.length === 0 && <p className="p-3 text-sm text-muted-foreground">No employees found.</p>}
                  <div ref={employeeSentinel} className="h-2" />
                </div>
              </div>
            )}
          </label>

          <label className="block text-sm font-medium text-foreground">
            Reason
            <input value={reason} onChange={(event) => setReason(event.target.value)} maxLength={1000} placeholder="Describe the support purpose" className="mt-1 h-10 w-full rounded-lg border border-border bg-background px-3 text-sm outline-none focus:border-primary" />
          </label>

          <label className="block text-sm font-medium text-foreground">
            Duration
            <select value={durationMinutes} onChange={(event) => setDurationMinutes(Number(event.target.value))} className="mt-1 h-10 w-full rounded-lg border border-border bg-background px-3 text-sm outline-none focus:border-primary">
              {durations.map((duration) => <option key={duration} value={duration}>{duration} minutes</option>)}
            </select>
          </label>

          <button type="button" disabled={!selectedEmployee || reason.trim().length < 3 || createMutation.isPending} onClick={() => createMutation.mutate()} className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground disabled:cursor-not-allowed disabled:opacity-50">
            {createMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
            Request view
          </button>
        </div>
      </section>

      <section className="rounded-xl border border-border bg-card">
        <div className="flex items-center justify-between border-b border-border p-5">
          <div><h2 className="font-display font-semibold text-foreground">Viewing requests</h2><p className="text-sm text-muted-foreground">Audited lifecycle records; media is not stored.</p></div>
          <button type="button" onClick={() => sessionsQuery.refetch()} className="rounded-md p-2 text-muted-foreground hover:bg-muted hover:text-foreground" aria-label="Refresh requests"><RefreshCw className={`h-4 w-4 ${sessionsQuery.isFetching ? 'animate-spin' : ''}`} /></button>
        </div>
        <div className="divide-y divide-border">
          {sessions.map((session) => <SessionRow key={session.id} session={session} onStop={() => stopMutation.mutate(session.id)} stopping={stopMutation.isPending && stopMutation.variables === session.id} />)}
          {sessionsQuery.isLoading && <div className="flex justify-center p-8"><Loader2 className="h-6 w-6 animate-spin text-primary" /></div>}
          {!sessionsQuery.isLoading && sessions.length === 0 && <div className="p-8 text-center text-sm text-muted-foreground">No viewing requests yet.</div>}
          <div ref={sessionSentinel} className="h-2" />
          {sessionsQuery.isFetchingNextPage && <p className="pb-4 text-center text-xs text-muted-foreground">Loading more…</p>}
        </div>
      </section>
    </div>
  );
}

function SessionRow({ session, onStop, stopping }: { session: LiveViewSession; onStop: () => void; stopping: boolean }) {
  const active = ['REQUESTED', 'APPROVED', 'STARTING', 'ACTIVE', 'STOPPING'].includes(session.status);
  return (
    <div className="flex flex-col gap-3 p-5 md:flex-row md:items-center md:justify-between">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium text-foreground">{session.employeeId}</span>
          <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${active ? 'bg-amber-500/15 text-amber-600' : 'bg-muted text-muted-foreground'}`}>{session.status}</span>
        </div>
        <p className="mt-1 truncate text-sm text-muted-foreground">{session.reason}</p>
        <p className="mt-2 flex items-center gap-1 text-xs text-muted-foreground"><Clock3 className="h-3 w-3" /> Expires {new Date(session.expiresAt).toLocaleString()}</p>
      </div>
      {active && <button type="button" onClick={onStop} disabled={stopping} className="inline-flex shrink-0 items-center justify-center gap-2 rounded-lg border border-destructive/40 px-3 py-2 text-sm font-medium text-destructive hover:bg-destructive/10 disabled:opacity-50"><Square className="h-3.5 w-3.5" />{stopping ? 'Stopping…' : 'Stop request'}</button>}
    </div>
  );
}
