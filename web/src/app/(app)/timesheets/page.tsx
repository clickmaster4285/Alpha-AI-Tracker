'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Clock, Loader2 } from 'lucide-react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import EmployeeSelector from '@/components/EmployeeSelector';
import EmptyState from '@/components/employees/EmptyState';
import {
  attendanceApi,
  employeesApi,
  type AttendanceRecord,
  type AttendanceStatus,
  type Employee,
} from '@/lib/api';
import { formatDateTimeInZone, formatSeconds } from '@/lib/format';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const PER_PAGE = 31;

const STATUS_LABEL: Record<AttendanceStatus, string> = {
  present: 'Present',
  late: 'Late',
  absent: 'Absent',
  half_day: 'Half Day',
  off_shift: 'Off Shift',
  unknown: 'Unknown',
};

const statusColors: Record<AttendanceStatus, string> = {
  present: 'bg-success/15 text-success',
  late: 'bg-warning/15 text-warning',
  absent: 'bg-destructive/15 text-destructive',
  half_day: 'bg-warning/15 text-warning',
  off_shift: 'bg-info/15 text-info',
  unknown: 'bg-muted text-muted-foreground',
};

function localToday(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function daysAgo(n: number): string {
  const d = new Date();
  d.setDate(d.getDate() - n);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export default function TimesheetsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <TimesheetsPageInner />
    </Suspense>
  );
}

function TimesheetsPageInner() {
  // URL-synced filter state — `/attendance` deep-links here with
  // `?employeeId=&from=&to=` and the page must round-trip those values
  // both ways (URL → state on mount, state → URL on every change).
  const [filters, setFilters] = useUrlQueryState(
    { employeeId: {}, from: {}, to: {} },
    { employeeId: '', from: daysAgo(13), to: localToday() },
  );

  const [employee, setEmployee] = useState<Employee | null>(null);
  const from = filters.from;
  const to = filters.to;

  const { data: employeesData } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  // Resolve the URL-selected employee UUID → Employee object once the list
  // arrives. The selector's onChange below pushes the new id back into the URL.
  useEffect(() => {
    if (!filters.employeeId || !employeesData) {
      setEmployee(prev => (prev && prev.id === filters.employeeId ? prev : null));
      return;
    }
    const match = employeesData.data.find(e => e.id === filters.employeeId);
    setEmployee(prev => (prev?.id === match?.id ? prev : (match ?? null)));
  }, [filters.employeeId, employeesData]);

  const handleSelectEmployee = (emp: Employee | null) => {
    setEmployee(emp);
    setFilters({ employeeId: emp?.id ?? '' });
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex md:flex-row flex-col justify-between">
        <div className="flex flex-col gap-4 ">
          <div>
            <h3 className="font-display font-bold text-lg text-foreground">Timesheets</h3>
            <p className="text-xs text-muted-foreground mt-0.5">
              Daily active, idle, and off-shift totals computed on the server.
            </p>
          </div>
        </div>

        <div className="flex flex-col xl:flex-row  gap-3 ">
          <EmployeeSelector
            value={employee?.id ?? ''}
            onChange={handleSelectEmployee}
            placeholder="Select employee…"
            className="w-full lg:w-80"
          />
          <div className="flex gap-3 ">
            <input
              type="date"
              value={from}
              max={to}
              onChange={e => setFilters({ from: e.target.value })}
              className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground w-full sm:w-auto"
            />
            <input
              type="date"
              value={to}
              min={from}
              onChange={e => setFilters({ to: e.target.value })}
              className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground w-full sm:w-auto"
            />

          </div>
        </div>
      </div>
      {!employee ? (
        <EmptyState icon={Clock} text="Select an employee to view their timesheet history." />
      ) : (
        <TimesheetBody employee={employee} from={from} to={to} />
      )}
    </div>
  );
}

function TimesheetBody({
  employee,
  from,
  to,
}: {
  employee: Employee;
  from: string;
  to: string;
}) {
  const {
    data,
    isLoading,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['attendance', 'range', employee.employeeId, from, to, PER_PAGE],
    queryFn: ({ pageParam }) =>
      attendanceApi.range({
        employeeId: employee.employeeId,
        from,
        to,
        page: pageParam as number,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: last => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const rows = useMemo(
    () => data?.pages.flatMap(p => p.data) ?? [],
    [data],
  );
  const total = data?.pages[0]?.total ?? 0;

  const totals = useMemo(() => rows.reduce(
    (acc, row) => ({
      active: acc.active + row.activeSeconds,
      idle: acc.idle + row.idleSeconds,
      offShift: acc.offShift + row.offShiftSeconds,
    }),
    { active: 0, idle: 0, offShift: 0 },
  ), [rows]);

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="text-center py-12">
        <p className="text-destructive font-medium">Failed to load timesheets</p>
        <p className="text-sm text-muted-foreground mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  if (rows.length === 0) {
    return <EmptyState icon={Clock} text="No attendance rows in this date range." />;
  }

  return (
    <>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        {[
          { label: 'Total Active', value: formatSeconds(totals.active), color: 'text-success' },
          { label: 'Total Idle / Locked', value: formatSeconds(totals.idle), color: 'text-muted-foreground' },
          { label: 'Total Off Shift', value: formatSeconds(totals.offShift), color: 'text-info' },
        ].map(tile => (
          <div key={tile.label} className="bg-card rounded-xl border border-border p-4">
            <p className={`text-xl font-display font-bold ${tile.color}`}>{tile.value}</p>
            <p className="text-xs text-muted-foreground mt-1">{tile.label}</p>
          </div>
        ))}
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[960px]">
          <thead>
            <tr className="border-b border-border">
              {['Date', 'Clock In', 'Clock Out', 'Active', 'Idle', 'Off Shift', 'Status'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row: AttendanceRecord, i) => (
              <motion.tr
                key={`${row.employeeId}-${row.workDate}`}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: i * 0.02 }}
                className="border-b border-border last:border-0 hover:bg-muted/30"
              >
                <td className="px-4 py-3 text-sm text-muted-foreground">{row.workDate}</td>
                <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(row.firstActiveAt, row.timezone)}</td>
                <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(row.lastActiveAt, row.timezone)}</td>
                <td className="px-4 py-3 text-sm text-success font-medium">{formatSeconds(row.activeSeconds)}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{formatSeconds(row.idleSeconds)}</td>
                <td className="px-4 py-3 text-sm text-info">{formatSeconds(row.offShiftSeconds)}</td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[row.status]}`}>
                    {STATUS_LABEL[row.status]}
                  </span>
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>

      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground text-center">
          Showing all {total.toLocaleString()} day{total === 1 ? '' : 's'} for {employee.name}
        </p>
      )}
    </>
  );
}
