import { useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, Monitor } from 'lucide-react';
import { getEmployees } from '@/lib/store';
import { useState } from 'react';

export default function LiveStream() {
  const employees = useMemo(() => getEmployees(), []);
  const onlineEmployees = employees.filter(e => e.isOnline);
  const offlineEmployees = employees.filter(e => !e.isOnline);
  const [selectedEmployee, setSelectedEmployee] = useState(onlineEmployees[0]?.id || '');
  const [search, setSearch] = useState('');

  const selectedEmp = employees.find(e => e.id === selectedEmployee);

  const filteredOnline = onlineEmployees.filter(e => e.name.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>Global Department</option>
        </select>
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search User" className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        {/* Left panel */}
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <div className="bg-success/10 border-2 border-success rounded-xl p-4 text-center">
              <p className="text-sm font-medium text-success">Online</p>
              <p className="text-3xl font-display font-bold text-success">{onlineEmployees.length}</p>
            </div>
            <div className="bg-destructive/10 border-2 border-destructive/30 rounded-xl p-4 text-center">
              <p className="text-sm font-medium text-destructive">Offline</p>
              <p className="text-3xl font-display font-bold text-destructive">{offlineEmployees.length}</p>
            </div>
          </div>
          <p className="text-sm text-muted-foreground">Streams: {onlineEmployees.length} / {employees.length}</p>

          <div>
            <h3 className="font-display font-semibold text-foreground mb-3">Online Employees</h3>
            <div className="space-y-2">
              {filteredOnline.map(emp => (
                <button
                  key={emp.id}
                  onClick={() => setSelectedEmployee(emp.id)}
                  className={`w-full flex items-center gap-3 p-3 rounded-xl border transition-all
                    ${selectedEmployee === emp.id ? 'border-primary bg-accent' : 'border-border bg-card hover:bg-muted/50'}`}
                >
                  <div className="relative">
                    <div className="w-9 h-9 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground" style={{ backgroundColor: emp.avatarColor }}>
                      {emp.avatar}
                    </div>
                    <div className="absolute -bottom-0.5 -right-0.5 w-3 h-3 rounded-full bg-success border-2 border-card" />
                  </div>
                  <div className="text-left">
                    <p className="text-sm font-medium text-foreground">{emp.name}</p>
                    <p className="text-xs text-success">• Online</p>
                  </div>
                </button>
              ))}
              {filteredOnline.length === 0 && (
                <p className="text-sm text-muted-foreground text-center py-4">No online employees</p>
              )}
            </div>
          </div>
        </div>

        {/* Stream view */}
        <div className="lg:col-span-2">
          {selectedEmp ? (
            <div className="space-y-3">
              <div className="flex items-center gap-3 p-3 bg-accent/50 rounded-xl">
                <div className="w-10 h-10 rounded-full flex items-center justify-center text-sm font-bold text-primary-foreground" style={{ backgroundColor: selectedEmp.avatarColor }}>
                  {selectedEmp.avatar}
                </div>
                <div>
                  <p className="font-semibold text-foreground">{selectedEmp.name}</p>
                  <p className="text-xs text-success">• Connecting...</p>
                </div>
              </div>
              <div className="aspect-video bg-muted rounded-xl flex items-center justify-center border border-border">
                <div className="text-center space-y-2">
                  <Monitor className="w-12 h-12 text-muted-foreground/30 mx-auto" />
                  <p className="text-sm text-muted-foreground animate-pulse-soft">Establishing connection...</p>
                </div>
              </div>
            </div>
          ) : (
            <div className="aspect-video bg-muted rounded-xl flex items-center justify-center border border-border">
              <p className="text-muted-foreground">Select an online employee to view stream</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
