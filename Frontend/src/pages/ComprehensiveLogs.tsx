import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, ChevronDown, ChevronUp } from 'lucide-react';
import { getActivityLogs, getSystemLogs, getProductivity, getEmployees, getDepartments } from '@/lib/store';

const TABS = ['App Log', 'System Log', 'Productive/Unproductive', 'Geo Location'] as const;
type Tab = typeof TABS[number];

const DAYS = ['Mon 02', 'Tue 03', 'Wed 04', 'Thu 05', 'Fri 06', 'Sat 07', 'Sun 08', 'Mon 09', 'Tue 10', 'Wed 11'];

export default function ComprehensiveLogs() {
  const employees = useMemo(() => getEmployees(), []);
  const departments = useMemo(() => getDepartments(), []);
  const activityLogs = useMemo(() => getActivityLogs(), []);
  const systemLogs = useMemo(() => getSystemLogs(), []);
  const productivity = useMemo(() => getProductivity(), []);

  const [tab, setTab] = useState<Tab>('App Log');
  const [selectedEmployee, setSelectedEmployee] = useState(employees[0]?.id || '');
  const [selectedDay, setSelectedDay] = useState('Mon 02');
  const [expandedApp, setExpandedApp] = useState<string | null>(null);

  const empLogs = activityLogs.filter(l => l.employeeId === selectedEmployee);
  const empSystemLogs = systemLogs.filter(l => l.employeeId === selectedEmployee);
  const empProductivity = productivity.filter(l => l.employeeId === selectedEmployee);

  const productiveEntries = empProductivity.filter(e => e.category === 'productive');
  const unproductiveEntries = empProductivity.filter(e => e.category === 'unproductive');

  const selectedEmp = employees.find(e => e.id === selectedEmployee);

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Filters */}
      <div className="flex flex-col lg:flex-row gap-3">
        <div className="flex flex-col sm:flex-row gap-3 flex-1">
          <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
            <option>Default Department</option>
            {departments.map(d => <option key={d}>{d}</option>)}
          </select>
          <select value={selectedEmployee} onChange={e => setSelectedEmployee(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
            {employees.map(e => <option key={e.id} value={e.id}>{e.name} ({e.employeeId})</option>)}
          </select>
        </div>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          02-Feb-2026 To 03-Mar-2026
        </div>
      </div>

      {/* Day selector */}
      <div className="flex gap-2 overflow-x-auto pb-2">
        {DAYS.map(day => (
          <button
            key={day}
            onClick={() => setSelectedDay(day)}
            className={`flex-shrink-0 w-14 h-14 rounded-full flex flex-col items-center justify-center text-xs font-medium transition-all
              ${selectedDay === day ? 'gradient-primary text-primary-foreground' : 'bg-card border border-border text-foreground hover:bg-accent'}`}
          >
            <span className="text-[10px]">{day.split(' ')[0]}</span>
            <span className="font-bold">{day.split(' ')[1]}</span>
          </button>
        ))}
      </div>

      {/* Tabs */}
      <div className="flex border-b border-border gap-1">
        {TABS.map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2.5 text-sm font-medium transition-colors relative
              ${tab === t ? 'text-primary' : 'text-muted-foreground hover:text-foreground'}`}
          >
            {t}
            {tab === t && <motion.div layoutId="log-tab" className="absolute bottom-0 left-0 right-0 h-0.5 gradient-primary rounded-full" />}
          </button>
        ))}
      </div>

      {/* Content */}
      {tab === 'App Log' && (
        <div className="bg-card rounded-xl border border-border overflow-hidden">
          <table className="w-full">
            <thead>
              <tr className="border-b border-border">
                <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Application</th>
                <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Tab Details</th>
              </tr>
            </thead>
            <tbody>
              {empLogs.slice(0, 8).map(log => (
                <tr key={log.id} className="border-b border-border last:border-0 hover:bg-muted/30">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-6 h-6 rounded bg-accent flex items-center justify-center text-[10px] font-bold text-accent-foreground">{`</>`}</div>
                      <span className="text-sm font-medium text-foreground">{log.application}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    {log.tabs.map((t, i) => (
                      <p key={i} className="text-sm text-muted-foreground">{t.name}, <span className="text-foreground">{t.duration}</span></p>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tab === 'System Log' && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {['Charging On/Off Logs', 'Lock/Unlock Logs', 'System Suspend/Resume Logs', 'System Status Logs'].map((section, si) => {
            const sectionLogs = empSystemLogs.filter(l => 
              si === 0 ? l.type === 'charging' :
              si === 1 ? l.type === 'lock' :
              si === 2 ? l.type === 'suspend' :
              l.type === 'status'
            );
            return (
              <div key={section} className="bg-card rounded-xl border border-border p-4">
                <h4 className="font-display font-semibold text-foreground mb-3">{section}</h4>
                {sectionLogs.map(log => (
                  <div key={log.id} className="flex items-center gap-3 py-2 text-sm">
                    {log.type === 'status' ? (
                      <>
                        <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${log.status === 'Active' ? 'bg-success/15 text-success' : 'bg-warning/15 text-warning'}`}>{log.status}</span>
                        <span className="text-muted-foreground">{log.startTime || '10:18:17 AM'}</span>
                      </>
                    ) : (
                      <>
                        <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-success/15 text-success">{log.startTime}</span>
                        <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-accent text-accent-foreground">{log.endTime}</span>
                        <span className="text-foreground">{log.duration}</span>
                      </>
                    )}
                  </div>
                ))}
              </div>
            );
          })}
        </div>
      )}

      {tab === 'Productive/Unproductive' && (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          <div className="bg-card rounded-xl border border-border p-4">
            <h4 className="font-display font-semibold text-foreground mb-3">Productive</h4>
            {productiveEntries.slice(0, 6).map(entry => (
              <div key={entry.id} className="border-b border-border last:border-0">
                <button
                  onClick={() => setExpandedApp(expandedApp === entry.id ? null : entry.id)}
                  className="w-full flex items-center justify-between py-3 text-sm"
                >
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-foreground">{entry.application}</span>
                    <span className="text-muted-foreground">{entry.totalDuration}</span>
                  </div>
                  {expandedApp === entry.id ? <ChevronUp className="w-4 h-4 text-muted-foreground" /> : <ChevronDown className="w-4 h-4 text-muted-foreground" />}
                </button>
                {expandedApp === entry.id && (
                  <div className="pb-3 pl-4 space-y-1">
                    {entry.tabs.map((t, i) => (
                      <p key={i} className="text-sm text-muted-foreground flex justify-between">
                        <span>{t.name}</span>
                        <span>{t.duration}</span>
                      </p>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
          <div className="bg-card rounded-xl border border-border p-4">
            <h4 className="font-display font-semibold text-foreground mb-3">Non Productive</h4>
            {unproductiveEntries.slice(0, 6).map(entry => (
              <div key={entry.id} className="border-b border-border last:border-0">
                <button
                  onClick={() => setExpandedApp(expandedApp === entry.id ? null : entry.id)}
                  className="w-full flex items-center justify-between py-3 text-sm"
                >
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-foreground">{entry.application}</span>
                    <span className="text-muted-foreground">{entry.totalDuration}</span>
                  </div>
                  {expandedApp === entry.id ? <ChevronUp className="w-4 h-4 text-muted-foreground" /> : <ChevronDown className="w-4 h-4 text-muted-foreground" />}
                </button>
                {expandedApp === entry.id && (
                  <div className="pb-3 pl-4 space-y-1">
                    {entry.tabs.map((t, i) => (
                      <p key={i} className="text-sm text-muted-foreground flex justify-between">
                        <span>{t.name}</span>
                        <span>{t.duration}</span>
                      </p>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {tab === 'Geo Location' && (
        <div className="bg-card rounded-xl border border-border p-8 text-center">
          <p className="text-muted-foreground">Geo Location data will appear here when tracking is active.</p>
        </div>
      )}
    </div>
  );
}
