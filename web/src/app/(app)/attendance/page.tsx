'use client';

import { motion } from 'framer-motion';
import { useAuth } from '@/lib/auth';

const attendanceData = [
  { employee: 'Yashodhan Kalia', date: '2026-03-10', status: 'Present', clockIn: '09:02 AM', shiftStart: '09:00 AM', lateBy: '2 min' },
  { employee: 'Stuti Srivastava', date: '2026-03-10', status: 'Late', clockIn: '09:32 AM', shiftStart: '09:00 AM', lateBy: '32 min' },
  { employee: 'Rakesh Pathania', date: '2026-03-10', status: 'Present', clockIn: '08:45 AM', shiftStart: '09:00 AM', lateBy: '-' },
  { employee: 'Kamal Dhami', date: '2026-03-10', status: 'Absent', clockIn: '-', shiftStart: '09:00 AM', lateBy: '-' },
  { employee: 'Tarun Saini', date: '2026-03-10', status: 'Leave', clockIn: '-', shiftStart: '09:00 AM', lateBy: '-' },
  { employee: 'Arush Sharma', date: '2026-03-10', status: 'Present', clockIn: '08:58 AM', shiftStart: '09:00 AM', lateBy: '-' },
  { employee: 'Priya Mehta', date: '2026-03-10', status: 'Late', clockIn: '09:15 AM', shiftStart: '09:00 AM', lateBy: '15 min' },
];

const statusColors: Record<string, string> = {
  Present: 'bg-success/15 text-success',
  Late: 'bg-warning/15 text-warning',
  Absent: 'bg-destructive/15 text-destructive',
  Leave: 'bg-info/15 text-info',
};

export default function AttendancePage() {
  const { user } = useAuth();
  const canSeeAll = user?.role === 'super_admin' || user?.role === 'org_admin';

  // Non-admin users only see their own attendance (simulated as first entry)
  const visibleData = canSeeAll ? attendanceData : attendanceData.filter(a => a.employee === user?.name).length > 0
    ? attendanceData.filter(a => a.employee === user?.name)
    : [{ employee: user?.name || 'You', date: '2026-03-10', status: 'Present', clockIn: '09:00 AM', shiftStart: '09:00 AM', lateBy: '-' }];

  const presentCount = visibleData.filter(a => a.status === 'Present').length;
  const lateCount = visibleData.filter(a => a.status === 'Late').length;
  const absentCount = visibleData.filter(a => a.status === 'Absent').length;
  const leaveCount = visibleData.filter(a => a.status === 'Leave').length;

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex gap-3">
        <input type="date" defaultValue="2026-03-10" className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground" />
        {canSeeAll && (
          <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
            <option>All Status</option>
            <option>Present</option><option>Late</option><option>Absent</option><option>Leave</option>
          </select>
        )}
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {[
          { label: 'Present', count: presentCount, color: 'text-success' },
          { label: 'Late', count: lateCount, color: 'text-warning' },
          { label: 'Absent', count: absentCount, color: 'text-destructive' },
          { label: 'On Leave', count: leaveCount, color: 'text-info' },
        ].map(s => (
          <div key={s.label} className="bg-card rounded-xl border border-border p-4 text-center">
            <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
            <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['Employee', 'Date', 'Status', 'Clock In', 'Shift Start', 'Late By'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visibleData.map((a, i) => (
              <motion.tr key={i} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-medium text-foreground">{a.employee}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{a.date}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[a.status]}`}>{a.status}</span></td>
                <td className="px-4 py-3 text-sm text-foreground">{a.clockIn}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{a.shiftStart}</td>
                <td className="px-4 py-3 text-sm text-warning">{a.lateBy}</td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
