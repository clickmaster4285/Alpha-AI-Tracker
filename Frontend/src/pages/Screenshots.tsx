import { useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { Clock, Monitor } from 'lucide-react';
import { getScreenshots, getEmployees } from '@/lib/store';

export default function Screenshots() {
  const screenshots = useMemo(() => getScreenshots(), []);
  const employees = useMemo(() => getEmployees(), []);
  const [selectedEmployee, setSelectedEmployee] = useState('');

  const filtered = selectedEmployee
    ? screenshots.filter(s => s.employeeId === selectedEmployee)
    : screenshots;

  // Group by employee
  const grouped = filtered.reduce((acc, s) => {
    if (!acc[s.employeeName]) acc[s.employeeName] = [];
    acc[s.employeeName].push(s);
    return acc;
  }, {} as Record<string, typeof filtered>);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select value={selectedEmployee} onChange={e => setSelectedEmployee(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option value="">All Employees</option>
          {employees.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
        <label className="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" className="rounded border-border" /> Select all
        </label>
      </div>

      {Object.entries(grouped).map(([name, shots]) => (
        <div key={name} className="space-y-3">
          <h3 className="font-display font-semibold text-foreground">{name}</h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
            {shots.map((shot, i) => (
              <motion.div
                key={shot.id}
                initial={{ opacity: 0, scale: 0.95 }}
                animate={{ opacity: 1, scale: 1 }}
                transition={{ delay: i * 0.05 }}
                className="bg-card rounded-xl border border-border overflow-hidden shadow-card hover:shadow-card-hover transition-all duration-300 group"
              >
                <div className="aspect-video bg-muted flex items-center justify-center relative">
                  <Monitor className="w-10 h-10 text-muted-foreground/30" />
                  <div className="absolute top-2 right-2 flex items-center gap-1 bg-card/80 backdrop-blur-sm rounded-md px-2 py-1">
                    <div className="w-4 h-4 rounded-full" style={{ background: `hsl(var(--primary))` }} />
                    <span className="text-[10px] text-foreground font-medium">{shot.application}</span>
                  </div>
                </div>
                <div className="p-3 flex items-center justify-between">
                  <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
                    <Clock className="w-3 h-3" />
                    {shot.time}
                  </div>
                  <span className="text-xs text-primary font-medium">• {shot.department}</span>
                </div>
              </motion.div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
