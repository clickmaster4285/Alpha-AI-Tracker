import { motion } from 'framer-motion';
import { Trophy, Clock, AppWindow, Zap, CalendarCheck, TrendingUp, TrendingDown, Target, FileText, Download } from 'lucide-react';
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import StatsCard from '@/components/ui/StatsCard';
import { Progress } from '@/components/ui/progress';

const weeklyData = [
  { day: 'Mon', score: 78 }, { day: 'Tue', score: 82 }, { day: 'Wed', score: 91 },
  { day: 'Thu', score: 75 }, { day: 'Fri', score: 88 }, { day: 'Sat', score: 40 }, { day: 'Sun', score: 0 },
];

const goals = [
  { name: 'Complete Q1 Sprint Tasks', progress: 72, due: '2026-03-31' },
  { name: 'Code Review Turnaround < 4hrs', progress: 85, due: '2026-03-15' },
  { name: 'Documentation Updates', progress: 40, due: '2026-04-01' },
];

const topApps = [
  { name: 'VS Code', time: '4h 12m', productive: true },
  { name: 'Chrome', time: '2h 45m', productive: true },
  { name: 'Slack', time: '1h 30m', productive: true },
  { name: 'Figma', time: '0h 45m', productive: true },
  { name: 'Spotify', time: '0h 25m', productive: false },
];

export default function EmployeePortal() {
  return (
    <div className="space-y-6 animate-fade-in">
      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-5 gap-4">
        <StatsCard title="My Productivity Score" value="84" icon={Trophy} subtitle="Good" delay={0.05} />
        <StatsCard title="My Focus Time" value="5h 42m" icon={Clock} change={12} delay={0.1} />
        <StatsCard title="Apps Used Today" value="8" icon={AppWindow} subtitle="6 productive" delay={0.15} />
        <StatsCard title="My Peak Hour" value="10:00 AM" icon={Zap} subtitle="30-day avg" delay={0.2} />
        <StatsCard title="Attendance" value="Present" icon={CalendarCheck} subtitle="On time" delay={0.25} />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Weekly Trend */}
        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.3 }} className="bg-card rounded-xl border border-border p-5">
          <div className="flex items-center justify-between mb-4">
            <h3 className="font-display font-bold text-foreground">Weekly Productivity</h3>
            <div className="flex items-center gap-1 text-sm text-success font-medium">
              <TrendingUp className="w-4 h-4" /> +8% vs last week
            </div>
          </div>
          <div className="h-[200px]">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={weeklyData}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} domain={[0, 100]} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
                <Line type="monotone" dataKey="score" stroke="hsl(var(--primary))" strokeWidth={2.5} dot={{ r: 4 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </motion.div>

        {/* My Goals */}
        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.35 }} className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">My Goals</h3>
          <div className="space-y-4">
            {goals.map((goal, i) => (
              <div key={i} className="space-y-2">
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-foreground">{goal.name}</span>
                  <span className="text-xs text-muted-foreground">Due: {goal.due}</span>
                </div>
                <div className="flex items-center gap-3">
                  <Progress value={goal.progress} className="flex-1 h-2" />
                  <span className="text-xs font-semibold text-foreground w-10 text-right">{goal.progress}%</span>
                </div>
              </div>
            ))}
          </div>
        </motion.div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Top Apps */}
        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.4 }} className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Top Apps Today</h3>
          <div className="space-y-2">
            {topApps.map((app, i) => (
              <div key={i} className="flex items-center justify-between py-2 border-b border-border last:border-0">
                <div className="flex items-center gap-3">
                  <div className="w-8 h-8 rounded-lg bg-accent flex items-center justify-center text-xs font-bold text-accent-foreground">{app.name[0]}</div>
                  <span className="text-sm font-medium text-foreground">{app.name}</span>
                </div>
                <div className="flex items-center gap-2">
                  <span className="text-sm text-muted-foreground">{app.time}</span>
                  <span className={`px-2 py-0.5 rounded-full text-[10px] font-medium ${app.productive ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>
                    {app.productive ? 'Productive' : 'Unproductive'}
                  </span>
                </div>
              </div>
            ))}
          </div>
        </motion.div>

        {/* Weekly Summary Report */}
        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.45 }} className="bg-card rounded-xl border border-border p-5">
          <div className="flex items-center justify-between mb-4">
            <h3 className="font-display font-bold text-foreground">Weekly Summary</h3>
            <button className="flex items-center gap-1 text-sm text-primary hover:text-primary/80 font-medium">
              <Download className="w-4 h-4" /> Export PDF
            </button>
          </div>
          <div className="space-y-3">
            <div className="flex items-center justify-between py-2 border-b border-border">
              <span className="text-sm text-muted-foreground">Total Active Time</span>
              <span className="text-sm font-semibold text-foreground">38h 15m</span>
            </div>
            <div className="flex items-center justify-between py-2 border-b border-border">
              <span className="text-sm text-muted-foreground">Total Idle Time</span>
              <span className="text-sm font-semibold text-foreground">3h 45m</span>
            </div>
            <div className="flex items-center justify-between py-2 border-b border-border">
              <span className="text-sm text-muted-foreground">Avg Productivity Score</span>
              <span className="text-sm font-semibold text-foreground">82/100</span>
            </div>
            <div className="flex items-center justify-between py-2">
              <span className="text-sm text-muted-foreground">Report Period</span>
              <span className="text-sm font-semibold text-foreground">Mar 3 – Mar 9, 2026</span>
            </div>
          </div>
        </motion.div>
      </div>
    </div>
  );
}
