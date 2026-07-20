'use client'

import { useState } from 'react';
import { motion } from 'framer-motion';
import { AlertTriangle, Search } from 'lucide-react';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';

const initialAlerts = [
  { id: 'DLP-001', type: 'File Transfer', employee: 'Arush Sharma', severity: 'Critical', timestamp: '2026-03-10 14:30', file: 'customer_data.xlsx', status: 'Open', assignedTo: '', notes: '' },
  { id: 'DLP-002', type: 'USB', employee: 'Kamal Dhami', severity: 'High', timestamp: '2026-03-10 13:15', file: 'financials_q1.pdf', status: 'Investigating', assignedTo: 'Security Analyst', notes: 'Checking device logs' },
  { id: 'DLP-003', type: 'Cloud Upload', employee: 'Tarun Saini', severity: 'Medium', timestamp: '2026-03-10 11:45', file: 'source_code.zip → Google Drive', status: 'Open', assignedTo: '', notes: '' },
  { id: 'DLP-004', type: 'Email', employee: 'Salman Hussain', severity: 'Low', timestamp: '2026-03-09 16:20', file: 'meeting_notes.docx', status: 'Resolved', assignedTo: 'IT Admin', notes: 'False positive' },
  { id: 'DLP-005', type: 'Cloud Upload', employee: 'Muskaan Makkad', severity: 'High', timestamp: '2026-03-09 15:00', file: 'api_keys.env → Dropbox', status: 'Open', assignedTo: '', notes: '' },
];

const severityColors: Record<string, string> = {
  Critical: 'bg-destructive/15 text-destructive',
  High: 'bg-warning/15 text-warning',
  Medium: 'bg-info/15 text-info',
  Low: 'bg-muted text-muted-foreground',
};

const statusColors: Record<string, string> = {
  Open: 'bg-destructive/15 text-destructive',
  Investigating: 'bg-warning/15 text-warning',
  Resolved: 'bg-success/15 text-success',
  'False Positive': 'bg-muted text-muted-foreground',
};

const typeColors: Record<string, string> = {
  'File Transfer': 'bg-info/15 text-info',
  USB: 'bg-warning/15 text-warning',
  'Cloud Upload': 'bg-primary/15 text-primary',
  Email: 'bg-accent text-accent-foreground',
};

export default function DLPAlertsPage() {
  const [alerts, setAlerts] = useState(initialAlerts);
  const [search, setSearch] = useState('');

  const filtered = alerts.filter(a => a.employee.toLowerCase().includes(search.toLowerCase()) || a.id.toLowerCase().includes(search.toLowerCase()));

  const updateStatus = (id: string, status: string) => setAlerts(alerts.map(a => a.id === id ? { ...a, status } : a));

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {[
          { label: 'Open', count: alerts.filter(a => a.status === 'Open').length, color: 'text-destructive' },
          { label: 'Investigating', count: alerts.filter(a => a.status === 'Investigating').length, color: 'text-warning' },
          { label: 'Resolved', count: alerts.filter(a => a.status === 'Resolved').length, color: 'text-success' },
          { label: 'Total', count: alerts.length, color: 'text-foreground' },
        ].map(s => (
          <div key={s.label} className="bg-card rounded-xl border border-border p-4 text-center">
            <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
            <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
          </div>
        ))}
      </div>

      <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
        <Search className="w-4 h-4 text-muted-foreground" />
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search alerts..." className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[900px]">
          <thead>
            <tr className="border-b border-border">
              {['Alert ID', 'Type', 'Employee', 'Severity', 'Timestamp', 'File / URL', 'Status', 'Actions'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filtered.map((a, i) => (
              <motion.tr key={a.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{a.id}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${typeColors[a.type]}`}>{a.type}</span></td>
                <td className="px-4 py-3 text-sm text-foreground">{a.employee}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${severityColors[a.severity]}`}>{a.severity}</span></td>
                <td className="px-4 py-3 text-xs text-muted-foreground">{a.timestamp}</td>
                <td className="px-4 py-3 text-xs text-muted-foreground max-w-[150px] truncate">{a.file}</td>
                <td className="px-4 py-3">
                  <Select value={a.status} onValueChange={v => updateStatus(a.id, v)}>
                    <SelectTrigger className="h-7 text-xs w-32"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="Open">Open</SelectItem>
                      <SelectItem value="Investigating">Investigating</SelectItem>
                      <SelectItem value="Resolved">Resolved</SelectItem>
                      <SelectItem value="False Positive">False Positive</SelectItem>
                    </SelectContent>
                  </Select>
                </td>
                <td className="px-4 py-3 text-xs text-primary cursor-pointer hover:underline">View</td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
