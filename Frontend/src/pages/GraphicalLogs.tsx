import { useMemo, useState } from 'react';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend, LineChart, Line } from 'recharts';
import { getEmployees } from '@/lib/store';

const weeklyData = [
  { day: 'Mon', productive: 6.5, unproductive: 2.1, idle: 0.8, neutral: 1.2 },
  { day: 'Tue', productive: 7.2, unproductive: 1.8, idle: 0.5, neutral: 1.0 },
  { day: 'Wed', productive: 5.8, unproductive: 3.2, idle: 1.2, neutral: 0.8 },
  { day: 'Thu', productive: 8.1, unproductive: 1.2, idle: 0.3, neutral: 0.9 },
  { day: 'Fri', productive: 7.5, unproductive: 1.5, idle: 0.6, neutral: 1.1 },
  { day: 'Sat', productive: 2.0, unproductive: 0.5, idle: 0.1, neutral: 0.3 },
  { day: 'Sun', productive: 0, unproductive: 0, idle: 0, neutral: 0 },
];

const trendData = [
  { week: 'W1', efficiency: 42, productivity: 55 },
  { week: 'W2', efficiency: 48, productivity: 60 },
  { week: 'W3', efficiency: 45, productivity: 52 },
  { week: 'W4', efficiency: 55, productivity: 65 },
];

export default function GraphicalLogs() {
  const employees = useMemo(() => getEmployees(), []);
  const [selectedEmployee, setSelectedEmployee] = useState(employees[0]?.id || '');

  return (
    <div className="space-y-6 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select value={selectedEmployee} onChange={e => setSelectedEmployee(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground max-w-xs">
          {employees.map(e => <option key={e.id} value={e.id}>{e.name}</option>)}
        </select>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          02-Feb-2026 To 03-Mar-2026
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Weekly Activity Breakdown</h3>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={weeklyData}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem', fontSize: '0.875rem' }} />
                <Legend />
                <Bar dataKey="productive" fill="hsl(152, 60%, 45%)" radius={[3, 3, 0, 0]} />
                <Bar dataKey="unproductive" fill="hsl(0, 72%, 55%)" radius={[3, 3, 0, 0]} />
                <Bar dataKey="idle" fill="hsl(38, 92%, 55%)" radius={[3, 3, 0, 0]} />
                <Bar dataKey="neutral" fill="hsl(210, 15%, 70%)" radius={[3, 3, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Week-over-Week Performance Trends</h3>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={trendData}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="week" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem', fontSize: '0.875rem' }} />
                <Legend />
                <Line type="monotone" dataKey="efficiency" stroke="hsl(var(--primary))" strokeWidth={2} dot={{ r: 4 }} />
                <Line type="monotone" dataKey="productivity" stroke="hsl(152, 60%, 45%)" strokeWidth={2} dot={{ r: 4 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>
    </div>
  );
}
