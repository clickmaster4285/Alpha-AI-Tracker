'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { motion } from 'framer-motion';
import { CalendarDays, Loader2 } from 'lucide-react';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useAuth } from '@/lib/auth';
import {
  attendanceApi,
  employeesApi,
  type AttendanceRecord,
  type AttendanceStatus,
  type Employee,
} from '@/lib/api';
import { formatDateTimeInZone, formatSeconds } from '@/lib/format';
import EmptyState from '@/components/employees/EmptyState';

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

type Row = AttendanceRecord & { employeeName: string };

export default function AttendancePage() {
  const { user } = useAuth();
  const [date, setDate] = useState(localToday);
  const [statusFilter, setStatusFilter] = useState<'all' | AttendanceStatus>('all');

  const isSelfOnly = Boolean(user?.employeeId) && user?.role === 'employee';

  const { data: employeesData, isLoading: employeesLoading, error: employeesError } = useQuery({
    queryKey: ['employees', 'attendance-log'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
    enabled: !isSelfOnly,
  });

  const employees: Employee[] = useMemo(() => {
    if (isSelfOnly && user?.employeeId) {
      return [{
        id: user.id,
        employeeId: user.employeeId,
        name: user.name,
        email: user.email,
        department: user.department || '',
        departmentId: 0,
        shiftId: null,
        shift: '',
        trackingEnabled: true,
        trackingStatus: 'tracked',
        isOnline: false,
        avatar: user.avatar,
        avatarColor: user.avatarColor,
        hasUserLogin: true,
        createdAt: '',
        updatedAt: '',
      }];
    }
    return employeesData?.data ?? [];
  }, [employeesData, isSelfOnly, user]);

  const attendanceQueries = useQueries({
    queries: employees.map(emp => ({
      queryKey: ['attendance', 'day', date, emp.employeeId],
      queryFn: () => attendanceApi.day(emp.employeeId, date),
      enabled: employees.length > 0,
    })),
  });

  const rows: Row[] = useMemo(() => {
    return employees.map((emp, i) => {
      const record = attendanceQueries[i]?.data;
      return {
        employeeId: emp.employeeId,
        employeeName: emp.name,
        workDate: date,
        firstActiveAt: record?.firstActiveAt ?? null,
        lastActiveAt: record?.lastActiveAt ?? null,
        timezone: record?.timezone ?? '',
        activeSeconds: record?.activeSeconds ?? 0,
        idleSeconds: record?.idleSeconds ?? 0,
        offShiftSeconds: record?.offShiftSeconds ?? 0,
        status: record?.status ?? 'unknown',
        lateMinutes: record?.lateMinutes ?? 0,
      };
    });
  }, [employees, attendanceQueries, date]);

  const filtered = useMemo(
    () => (statusFilter === 'all' ? rows : rows.filter(r => r.status === statusFilter)),
    [rows, statusFilter],
  );

  const stats = useMemo(() => ({
    present: rows.filter(r => r.status === 'present').length,
    late: rows.filter(r => r.status === 'late').length,
    absent: rows.filter(r => r.status === 'absent').length,
    offShift: rows.filter(r => r.status === 'off_shift').length,
  }), [rows]);

  const attendanceLoading = attendanceQueries.some(q => q.isLoading);
  const attendanceError = attendanceQueries.find(q => q.error)?.error as Error | undefined;

  if (employeesLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (employeesError) {
    return (
      <div className="text-center py-12">
        <p className="text-destructive font-medium">Failed to load employees</p>
        <p className="text-sm text-muted-foreground mt-1">{(employeesError as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3 sm:items-center sm:justify-between">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">Attendance Log</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            Server-computed daily status from synced session events.
          </p>
        </div>
        <div className="flex gap-3">
          <input
            type="date"
            value={date}
            onChange={e => setDate(e.target.value)}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          />
          {!isSelfOnly && (
            <select
              value={statusFilter}
              onChange={e => setStatusFilter(e.target.value as typeof statusFilter)}
              className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
            >
              <option value="all">All Status</option>
              {Object.entries(STATUS_LABEL).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {[
          { label: 'Present', count: stats.present, color: 'text-success' },
          { label: 'Late', count: stats.late, color: 'text-warning' },
          { label: 'Absent', count: stats.absent, color: 'text-destructive' },
          { label: 'Off Shift', count: stats.offShift, color: 'text-info' },
        ].map(s => (
          <div key={s.label} className="bg-card rounded-xl border border-border p-4 text-center">
            <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
            <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      {attendanceLoading ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : attendanceError ? (
        <div className="text-center py-12">
          <p className="text-destructive font-medium">Failed to load attendance</p>
          <p className="text-sm text-muted-foreground mt-1">{attendanceError.message}</p>
        </div>
      ) : filtered.length === 0 ? (
        <EmptyState icon={CalendarDays} text="No attendance rows match the current filters." />
      ) : (
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[900px]">
            <thead>
              <tr className="border-b border-border">
                {['Employee', 'Date', 'Status', 'First Active', 'Last Seen', 'Active', 'Idle', 'Late By'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {filtered.map((a, i) => {
                const empUuid = employees.find(e => e.employeeId === a.employeeId)?.id ?? a.employeeId;
                return (
                <motion.tr
                  key={a.employeeId}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: i * 0.02 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30"
                >
                  <td className="px-4 py-3 text-sm font-medium text-foreground">
                    <Link
                      href={{ pathname: '/timesheets', query: { employeeId: empUuid, from: date, to: date } }}
                      className="hover:text-primary hover:underline"
                    >
                      {a.employeeName}
                      <span className="block text-xs text-muted-foreground font-mono">{a.employeeId}</span>
                    </Link>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{a.workDate}</td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[a.status]}`}>
                      {STATUS_LABEL[a.status]}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(a.firstActiveAt, a.timezone)}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(a.lastActiveAt, a.timezone)}</td>
                  <td className="px-4 py-3 text-sm text-success">{formatSeconds(a.activeSeconds)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{formatSeconds(a.idleSeconds)}</td>
                  <td className="px-4 py-3 text-sm text-warning">
                    {a.lateMinutes > 0 ? `${a.lateMinutes} min` : '—'}
                  </td>
                </motion.tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
