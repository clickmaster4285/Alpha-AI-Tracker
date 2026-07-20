'use client'

import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, TrendingUp, TrendingDown, Clock, Target } from 'lucide-react';
import { PieChart, Pie, Cell, Tooltip, Legend } from 'recharts';
import { getEmployees } from '@/lib/store';

const attendanceData = [
  { name: 'On Time', value: 0, color: 'hsl(210, 80%, 55%)' },
  { name: 'Late', value: 16.67, color: 'hsl(38, 70%, 55%)' },
  { name: 'Absent', value: 83.33, color: 'hsl(330, 60%, 55%)' },
];

const summaryPoints = [
  'Employee was monitored for eight calendar days from 2026-02-06 to 2026-02-13.',
  'The data shows the employee spent the majority of the scheduled shift on unproductive and neutral activities.',
  'Attendance was poor, with an 83.33% absence rate and a 16.67% late arrival rate.',
  'The analysis highlights a 7.59% upside potential that could recover 0.61 lost workdays.',
];

export default function UserInsights() {
  const employees = useMemo(() => getEmployees(), []);
  const [selectedEmployee, setSelectedEmployee] = useState(employees[0]?.id || '');
  const selectedEmp = employees.find(e => e.id === selectedEmployee);

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Employee selector */}
      <div className="flex flex-col sm:flex-row gap-3">
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>Default Department</option>
        </select>
        <select value={selectedEmployee} onChange={e => setSelectedEmployee(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground flex-1 max-w-xs">
          {employees.map(e => <option key={e.id} value={e.id}>{e.name} ({e.employeeId})</option>)}
        </select>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          02-Feb-2026 To 03-Mar-2026
        </div>
      </div>

      {/* Header banner */}
      {selectedEmp && (
        <motion.div initial={{ opacity: 0, y: -8 }} animate={{ opacity: 1, y: 0 }} className="gradient-primary rounded-xl p-5 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
          <div className="flex items-center gap-4">
            <div className="w-14 h-14 rounded-full bg-muted/20 flex items-center justify-center text-lg font-bold text-primary-foreground border-2 border-primary-foreground/30">
              {selectedEmp.avatar}
            </div>
            <h2 className="font-display font-bold text-xl text-primary-foreground">{selectedEmp.name}</h2>
          </div>
          <div className="flex gap-6 text-primary-foreground/90 text-sm">
            <div><span className="text-primary-foreground/60">AI Efficiency Score</span><p className="font-bold text-lg text-primary-foreground">45.62/100</p></div>
            <div><span className="text-primary-foreground/60">Department</span><p className="font-semibold text-primary-foreground">{selectedEmp.department}</p></div>
            <div><span className="text-primary-foreground/60">Shift</span><p className="font-semibold text-primary-foreground">07:00 to 16:00</p></div>
          </div>
        </motion.div>
      )}

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        {[
          { label: 'Productivity Time', value: '3.49 hrs', icon: TrendingUp },
          { label: 'Unproductive Time', value: '4.16 hrs', icon: TrendingDown },
          { label: 'Idle Time', value: '0.7 hrs', icon: Clock },
          { label: 'Focus Efficiency', value: '43.62 %', icon: Target },
        ].map((stat, i) => (
          <motion.div key={stat.label} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card">
            <div className="flex items-center gap-2 mb-2">
              <stat.icon className="w-4 h-4 text-primary" />
              <span className="text-sm text-muted-foreground">{stat.label}</span>
            </div>
            <p className="text-2xl font-display font-bold text-foreground">{stat.value}</p>
          </motion.div>
        ))}
      </div>

      {/* Charts row */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Attendance Rate</h3>
          <div className="h-[250px]">
            <PieChart>
              <Pie data={attendanceData} cx="50%" cy="50%" innerRadius={60} outerRadius={100} paddingAngle={2} dataKey="value">
                {attendanceData.map((entry, i) => <Cell key={i} fill={entry.color} />)}
              </Pie>
              <Tooltip formatter={(value: number) => `${value}%`} />
              <Legend />
            </PieChart>
          </div>
        </div>
        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Executive Summary</h3>
          <div className="space-y-3">
            {summaryPoints.map((point, i) => (
              <div key={i} className="flex items-start gap-3">
                <div className="w-5 h-5 rounded-full bg-success/15 flex items-center justify-center flex-shrink-0 mt-0.5">
                  <div className="w-2 h-2 rounded-full bg-success" />
                </div>
                <p className="text-sm text-foreground leading-relaxed">{point}</p>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
