'use client'

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Search } from 'lucide-react';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';

const appInventory = [
  { name: 'Notion', auth: 'Approved', users: 42, firstDetected: '2025-12-01', risk: 'Low' },
  { name: 'WeTransfer', auth: 'Unauthorized', users: 8, firstDetected: '2026-02-15', risk: 'High' },
  { name: 'ChatGPT', auth: 'Under Review', users: 35, firstDetected: '2026-01-10', risk: 'Medium' },
  { name: 'Dropbox', auth: 'Unauthorized', users: 12, firstDetected: '2026-01-20', risk: 'High' },
  { name: 'Slack', auth: 'Approved', users: 50, firstDetected: '2025-06-01', risk: 'Low' },
  { name: 'Trello', auth: 'Approved', users: 28, firstDetected: '2025-09-15', risk: 'Low' },
  { name: 'Telegram', auth: 'Unauthorized', users: 5, firstDetected: '2026-03-01', risk: 'Medium' },
];

const authColors: Record<string, string> = {
  Approved: 'bg-success/15 text-success',
  Unauthorized: 'bg-destructive/15 text-destructive',
  'Under Review': 'bg-warning/15 text-warning',
};

const riskColors: Record<string, string> = {
  High: 'bg-destructive/15 text-destructive',
  Medium: 'bg-warning/15 text-warning',
  Low: 'bg-success/15 text-success',
};

export default function ShadowITPage() {
  const [search, setSearch] = useState('');
  const filtered = appInventory.filter(a => a.name.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="grid grid-cols-2 sm:grid-cols-3 gap-4">
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-success">{appInventory.filter(a => a.auth === 'Approved').length}</p>
          <p className="text-xs text-muted-foreground mt-1">Approved</p>
        </div>
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-destructive">{appInventory.filter(a => a.auth === 'Unauthorized').length}</p>
          <p className="text-xs text-muted-foreground mt-1">Unauthorized</p>
        </div>
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-warning">{appInventory.filter(a => a.auth === 'Under Review').length}</p>
          <p className="text-xs text-muted-foreground mt-1">Under Review</p>
        </div>
      </div>

      <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
        <Search className="w-4 h-4 text-muted-foreground" />
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search apps..." className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['App Name', 'Status', 'Users', 'First Detected', 'Risk Level', 'Action'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filtered.map((app, i) => (
              <motion.tr key={app.name} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-medium text-foreground">{app.name}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${authColors[app.auth]}`}>{app.auth}</span></td>
                <td className="px-4 py-3 text-sm text-foreground">{app.users}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{app.firstDetected}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${riskColors[app.risk]}`}>{app.risk}</span></td>
                <td className="px-4 py-3">
                  <Select defaultValue={app.auth === 'Approved' ? 'approve' : 'flag'}>
                    <SelectTrigger className="h-7 text-xs w-24"><SelectValue /></SelectTrigger>
                    <SelectContent><SelectItem value="approve">Approve</SelectItem><SelectItem value="block">Block</SelectItem><SelectItem value="flag">Flag</SelectItem></SelectContent>
                  </Select>
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
