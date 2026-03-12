import { motion } from 'framer-motion';
import { Search, MoreVertical } from 'lucide-react';
import { useState } from 'react';

const employees = [
  { name: 'Yashodhan Kalia', email: 'yashodhan@company.com', role: 'Employee', department: 'Engineering', status: 'Active', agent: 'Installed', lastActive: '2026-03-10 10:15' },
  { name: 'Stuti Srivastava', email: 'stuti@company.com', role: 'Employee', department: 'Design', status: 'Active', agent: 'Installed', lastActive: '2026-03-10 09:45' },
  { name: 'Kamal Dhami', email: 'kamal@company.com', role: 'Employee', department: 'QA', status: 'Invited', agent: 'Not Installed', lastActive: '-' },
  { name: 'Priya Mehta', email: 'priya@company.com', role: 'Manager', department: 'Design', status: 'Active', agent: 'Installed', lastActive: '2026-03-10 10:20' },
  { name: 'Ravi Kumar', email: 'ravi@company.com', role: 'Employee', department: 'Sales', status: 'Inactive', agent: 'Offline', lastActive: '2026-03-05' },
];

const statusColors: Record<string, string> = { Active: 'bg-success/15 text-success', Invited: 'bg-info/15 text-info', Inactive: 'bg-muted text-muted-foreground' };
const agentColors: Record<string, string> = { Installed: 'bg-success/15 text-success', 'Not Installed': 'bg-warning/15 text-warning', Offline: 'bg-destructive/15 text-destructive' };

export default function SettingsUserManagementPage() {
  const [search, setSearch] = useState('');
  const filtered = employees.filter(e => e.name.toLowerCase().includes(search.toLowerCase()) || e.email.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="space-y-4 animate-fade-in">
      <h3 className="font-display font-bold text-lg text-foreground">Employee Directory</h3>
      <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
        <Search className="w-4 h-4 text-muted-foreground" />
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search employees..." className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
      </div>
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[800px]">
          <thead><tr className="border-b border-border">
            {['Name', 'Email', 'Role', 'Department', 'Status', 'Agent', 'Last Active', 'Actions'].map(h => <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>)}
          </tr></thead>
          <tbody>
            {filtered.map((e, i) => (
              <motion.tr key={i} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-medium text-foreground">{e.name}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{e.email}</td>
                <td className="px-4 py-3"><span className="px-2 py-0.5 rounded bg-accent text-accent-foreground text-xs">{e.role}</span></td>
                <td className="px-4 py-3 text-sm text-foreground">{e.department}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[e.status]}`}>{e.status}</span></td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${agentColors[e.agent]}`}>{e.agent}</span></td>
                <td className="px-4 py-3 text-xs text-muted-foreground">{e.lastActive}</td>
                <td className="px-4 py-3"><button className="p-1.5 rounded hover:bg-muted"><MoreVertical className="w-4 h-4 text-muted-foreground" /></button></td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
