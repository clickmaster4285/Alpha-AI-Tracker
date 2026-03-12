import { useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import { getEmployees } from '@/lib/store';
import { Clock, TrendingUp, Coffee, Timer } from 'lucide-react';

const weeklyHours = [
  { day: 'Mon', regular: 8, overtime: 1.5, break: 1 },
  { day: 'Tue', regular: 7.5, overtime: 0.5, break: 1 },
  { day: 'Wed', regular: 8, overtime: 2, break: 0.5 },
  { day: 'Thu', regular: 7, overtime: 0, break: 1.5 },
  { day: 'Fri', regular: 8, overtime: 1, break: 1 },
  { day: 'Sat', regular: 4, overtime: 0, break: 0.5 },
  { day: 'Sun', regular: 0, overtime: 0, break: 0 },
];

export default function HoursInsights() {
  const employees = useMemo(() => getEmployees(), []);
  const [selectedEmployee, setSelectedEmployee] = useState(employees[0]?.id || '');

  return (
    <div className="space-y-6 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select value={selectedEmployee} onChange={e => setSelectedEmployee(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground max-w-xs">
          <option value="">All Employees</option>
          {employees.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">02-Feb-2026 To 03-Mar-2026</div>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        {[
          { label: 'Total Working Hours', value: '42.5 hrs', icon: Clock },
          { label: 'Overtime Hours', value: '5 hrs', icon: TrendingUp },
          { label: 'Break Time', value: '5.5 hrs', icon: Coffee },
          { label: 'Avg Daily Hours', value: '7.1 hrs', icon: Timer },
        ].map((stat, i) => (
          <motion.div key={stat.label} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card">
            <div className="flex items-center gap-2 mb-2"><stat.icon className="w-4 h-4 text-primary" /><span className="text-sm text-muted-foreground">{stat.label}</span></div>
            <p className="text-2xl font-display font-bold text-foreground">{stat.value}</p>
          </motion.div>
        ))}
      </div>

      {/* Chart */}
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-bold text-foreground mb-4">Weekly Hours Breakdown</h3>
        <div className="h-[350px]">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={weeklyHours}>
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
              <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
              <Legend />
              <Bar dataKey="regular" fill="hsl(var(--primary))" radius={[3, 3, 0, 0]} name="Regular Hours" />
              <Bar dataKey="overtime" fill="hsl(38, 92%, 55%)" radius={[3, 3, 0, 0]} name="Overtime" />
              <Bar dataKey="break" fill="hsl(210, 15%, 70%)" radius={[3, 3, 0, 0]} name="Break" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>

      {/* Employee Hours Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[600px]">
          <thead>
            <tr className="border-b border-border">
              {['Employee', 'Department', 'Regular Hrs', 'Overtime', 'Break', 'Total'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {employees.slice(0, 8).map((emp, i) => {
              const regular = +(Math.random() * 30 + 20).toFixed(1);
              const ot = +(Math.random() * 8).toFixed(1);
              const brk = +(Math.random() * 5).toFixed(1);
              return (
                <motion.tr key={emp.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-7 h-7 rounded-full flex items-center justify-center text-[10px] font-bold text-primary-foreground" style={{ backgroundColor: emp.avatarColor }}>{emp.avatar}</div>
                      <span className="text-sm font-medium text-foreground">{emp.name}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{emp.department}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{regular} hrs</td>
                  <td className="px-4 py-3 text-sm text-foreground">{ot} hrs</td>
                  <td className="px-4 py-3 text-sm text-foreground">{brk} hrs</td>
                  <td className="px-4 py-3 text-sm font-semibold text-foreground">{(regular + ot).toFixed(1)} hrs</td>
                </motion.tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
