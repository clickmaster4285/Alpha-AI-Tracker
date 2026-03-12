import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, Download } from 'lucide-react';
import { getUserActivityStatuses, getDepartments } from '@/lib/store';

export default function UserActivity() {
  const data = useMemo(() => getUserActivityStatuses(), []);
  const departments = useMemo(() => getDepartments(), []);
  const [search, setSearch] = useState('');
  const [dept, setDept] = useState('');

  const filtered = data.filter(d => {
    const matchSearch = d.userName.toLowerCase().includes(search.toLowerCase());
    return matchSearch;
  });

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3 items-start sm:items-center justify-between">
        <div className="flex flex-col sm:flex-row gap-3 flex-1">
          <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-sm">
            <Search className="w-4 h-4 text-muted-foreground" />
            <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search Users" className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
          </div>
          <select value={dept} onChange={e => setDept(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
            <option value="">Global Department</option>
            {departments.map(d => <option key={d} value={d}>{d}</option>)}
          </select>
        </div>
        <button className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90">
          <Download className="w-4 h-4" /> Download All
        </button>
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[800px]">
          <thead>
            <tr className="border-b border-border">
              {['Sr. No.', 'User name', 'Last Clock-in', 'Last Clock-out', 'Total Time', 'Total Productive Time', 'Total Extra Time'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filtered.map((row, i) => (
              <motion.tr
                key={row.id}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: i * 0.03 }}
                className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
              >
                <td className="px-4 py-3 text-sm text-foreground">{row.srNo}</td>
                <td className="px-4 py-3 text-sm font-medium text-foreground">{row.userName}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{row.lastClockIn}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{row.lastClockOut}</td>
                <td className="px-4 py-3 text-sm text-foreground">{row.totalTime}</td>
                <td className="px-4 py-3 text-sm text-foreground">{row.totalProductiveTime}</td>
                <td className="px-4 py-3 text-sm text-foreground">{row.totalExtraTime}</td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
