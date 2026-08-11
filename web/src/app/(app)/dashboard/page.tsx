'use client';

import { useMemo } from 'react';
import { motion } from 'framer-motion';
import { Users, Clock, TrendingUp, TrendingDown, Award, Eye } from 'lucide-react';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import StatsCard from '@/components/ui/StatsCard';
import { getDashboardStats, getEmployees } from '@/lib/store';
import DownloadAppSection from '@/components/DownloadAppSection';

const productivityData = [
  { day: 'Mon', productive: 8, unproductive: 3, idle: 1 },
  { day: 'Tue', productive: 7, unproductive: 4, idle: 1.5 },
  { day: 'Wed', productive: 9, unproductive: 2, idle: 0.5 },
  { day: 'Thu', productive: 6, unproductive: 5, idle: 2 },
  { day: 'Fri', productive: 10, unproductive: 1, idle: 0.5 },
  { day: 'Sat', productive: 3, unproductive: 1, idle: 0 },
  { day: 'Sun', productive: 0, unproductive: 0, idle: 0 },
];

export default function Dashboard() {
  const stats = useMemo(() => getDashboardStats(), []);
  const employees = useMemo(() => getEmployees(), []);
  const bestPerformer = employees[0];

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Download banner */}
      <DownloadAppSection />

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard
          title="Total Employees"
          value={stats.totalEmployees}
          icon={Users}
          subtitle={`Tracked: ${stats.trackedCount}  Untracked: ${stats.untrackedCount}`}
          delay={0.05}
        />
        <StatsCard title="Total Idle Time" value={stats.totalIdleTime} icon={Clock} change={stats.idleChange} delay={0.1} />
        <StatsCard title="Total Productive Hours" value={stats.totalProductiveHours} icon={TrendingUp} change={stats.productiveChange} delay={0.15} />
        <StatsCard title="Total Unproductive Hours" value={stats.totalUnproductiveHours} icon={TrendingDown} change={stats.unproductiveChange} delay={0.2} />
      </div>

      {/* Best Performance */}
      {bestPerformer && (
        <motion.div
          initial={{ opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 0.25 }}
          className="bg-card rounded-xl border border-border p-5 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4"
        >
          <div className="flex items-center gap-4">
            <div>
              <div className="flex items-center gap-2 mb-1">
                <Award className="w-5 h-5 text-warning" />
                <h3 className="font-display font-bold text-foreground">Best Performance</h3>
              </div>
              <p className="text-sm text-muted-foreground">Feb 24 - Mar 03, 2026</p>
            </div>
            <div className="flex items-center gap-3 ml-4">
              <div className="w-11 h-11 rounded-full flex items-center justify-center text-primary-foreground font-bold" style={{ backgroundColor: bestPerformer.avatarColor }}>
                {bestPerformer.avatar}
              </div>
              <div>
                <p className="font-semibold text-foreground">{bestPerformer.name}</p>
                <p className="text-xs text-muted-foreground">{bestPerformer.department}</p>
              </div>
            </div>
          </div>
          <button className="px-5 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity flex items-center gap-2">
            <Eye className="w-4 h-4" /> View All
          </button>
        </motion.div>
      )}

      {/* Chart */}
      <motion.div
        initial={{ opacity: 0, y: 10 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ delay: 0.3 }}
        className="bg-card rounded-xl border border-border p-5"
      >
        <h3 className="font-display font-bold text-foreground mb-4">Productive / Unproductive</h3>
        <div className="h-[300px]">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={productivityData}>
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
              <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <Tooltip
                contentStyle={{
                  backgroundColor: 'hsl(var(--card))',
                  border: '1px solid hsl(var(--border))',
                  borderRadius: '0.5rem',
                  fontSize: '0.875rem',
                }}
              />
              <Legend />
              <Bar dataKey="productive" fill="hsl(152, 60%, 45%)" radius={[4, 4, 0, 0]} />
              <Bar dataKey="unproductive" fill="hsl(0, 72%, 55%)" radius={[4, 4, 0, 0]} />
              <Bar dataKey="idle" fill="hsl(38, 92%, 55%)" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </motion.div>
    </div>
  );
}


