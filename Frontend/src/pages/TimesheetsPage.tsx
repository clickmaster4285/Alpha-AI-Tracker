import { useState } from 'react';
import { motion } from 'framer-motion';
import { Clock, Check, X, Download, FileText } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Textarea } from '@/components/ui/textarea';

const timesheetData = [
  { id: '1', employee: 'Yashodhan Kalia', date: '2026-03-10', clockIn: '09:02 AM', clockOut: '06:15 PM', totalHours: '9h 13m', productive: '7h 45m', breakTime: '1h 28m', overtime: '1h 13m', status: 'pending', note: '' },
  { id: '2', employee: 'Stuti Srivastava', date: '2026-03-10', clockIn: '09:15 AM', clockOut: '05:45 PM', totalHours: '8h 30m', productive: '6h 50m', breakTime: '1h 40m', overtime: '0h 30m', status: 'approved', note: '' },
  { id: '3', employee: 'Rakesh Pathania', date: '2026-03-10', clockIn: '08:45 AM', clockOut: '06:30 PM', totalHours: '9h 45m', productive: '8h 10m', breakTime: '1h 35m', overtime: '1h 45m', status: 'approved', note: '' },
  { id: '4', employee: 'Kamal Dhami', date: '2026-03-10', clockIn: '10:05 AM', clockOut: '05:00 PM', totalHours: '6h 55m', productive: '5h 20m', breakTime: '1h 35m', overtime: '0h 0m', status: 'rejected', note: 'Incomplete hours' },
  { id: '5', employee: 'Arush Sharma', date: '2026-03-10', clockIn: '09:00 AM', clockOut: '06:00 PM', totalHours: '9h 00m', productive: '7h 30m', breakTime: '1h 30m', overtime: '1h 00m', status: 'pending', note: '' },
];

const statusColors: Record<string, string> = {
  pending: 'bg-warning/15 text-warning',
  approved: 'bg-success/15 text-success',
  rejected: 'bg-destructive/15 text-destructive',
};

export default function TimesheetsPage() {
  const [sheets, setSheets] = useState(timesheetData);
  const [rejectDialog, setRejectDialog] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState('');

  const approve = (id: string) => setSheets(sheets.map(s => s.id === id ? { ...s, status: 'approved' } : s));
  const reject = (id: string) => {
    setSheets(sheets.map(s => s.id === id ? { ...s, status: 'rejected', note: rejectReason } : s));
    setRejectDialog(null);
    setRejectReason('');
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3 items-start sm:items-center justify-between">
        <div className="flex gap-3">
          <input type="date" defaultValue="2026-03-10" className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground" />
          <Select defaultValue="all">
            <SelectTrigger className="w-40"><SelectValue placeholder="Status" /></SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Status</SelectItem>
              <SelectItem value="pending">Pending</SelectItem>
              <SelectItem value="approved">Approved</SelectItem>
              <SelectItem value="rejected">Rejected</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <Button variant="outline" size="sm" className="gap-1"><Download className="w-4 h-4" /> Export</Button>
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[900px]">
          <thead>
            <tr className="border-b border-border">
              {['Employee', 'Date', 'Clock In', 'Clock Out', 'Total', 'Productive', 'Break', 'Overtime', 'Status', 'Actions'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sheets.map((s, i) => (
              <motion.tr key={s.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-medium text-foreground">{s.employee}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{s.date}</td>
                <td className="px-4 py-3 text-sm text-foreground">{s.clockIn}</td>
                <td className="px-4 py-3 text-sm text-foreground">{s.clockOut}</td>
                <td className="px-4 py-3 text-sm font-semibold text-foreground">{s.totalHours}</td>
                <td className="px-4 py-3 text-sm text-success">{s.productive}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{s.breakTime}</td>
                <td className="px-4 py-3 text-sm text-warning">{s.overtime}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium capitalize ${statusColors[s.status]}`}>{s.status}</span></td>
                <td className="px-4 py-3">
                  {s.status === 'pending' && (
                    <div className="flex gap-1">
                      <button onClick={() => approve(s.id)} className="p-1.5 rounded hover:bg-success/10 text-success"><Check className="w-4 h-4" /></button>
                      <button onClick={() => setRejectDialog(s.id)} className="p-1.5 rounded hover:bg-destructive/10 text-destructive"><X className="w-4 h-4" /></button>
                    </div>
                  )}
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>

      <Dialog open={!!rejectDialog} onOpenChange={() => setRejectDialog(null)}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Reject Timesheet</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Textarea value={rejectReason} onChange={e => setRejectReason(e.target.value)} placeholder="Reason for rejection (required)" rows={3} />
            <Button onClick={() => rejectDialog && reject(rejectDialog)} className="w-full gradient-primary text-primary-foreground" disabled={!rejectReason}>Reject</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
