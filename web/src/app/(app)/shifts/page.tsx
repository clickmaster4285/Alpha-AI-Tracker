'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Edit2, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const initialShifts = [
  { id: '1', name: 'Morning Shift', start: '09:00', end: '17:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'], grace: 5, overtimeThreshold: 8 },
  { id: '2', name: 'Night Shift', start: '22:00', end: '06:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'], grace: 10, overtimeThreshold: 8 },
  { id: '3', name: 'Flexible Shift', start: '08:00', end: '20:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'], grace: 15, overtimeThreshold: 10 },
];

const allDays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

export default function ShiftManagement() {
  const [shifts, setShifts] = useState(initialShifts);
  const [showDialog, setShowDialog] = useState(false);
  const [editing, setEditing] = useState<typeof initialShifts[0] | null>(null);
  const [form, setForm] = useState({ name: '', start: '09:00', end: '17:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'], grace: 5, overtimeThreshold: 8 });

  const openNew = () => { setEditing(null); setForm({ name: '', start: '09:00', end: '17:00', days: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'], grace: 5, overtimeThreshold: 8 }); setShowDialog(true); };
  const openEdit = (s: typeof initialShifts[0]) => { setEditing(s); setForm({ name: s.name, start: s.start, end: s.end, days: s.days, grace: s.grace, overtimeThreshold: s.overtimeThreshold }); setShowDialog(true); };

  const save = () => {
    if (!form.name) return;
    if (editing) {
      setShifts(shifts.map(s => s.id === editing.id ? { ...s, ...form } : s));
    } else {
      setShifts([...shifts, { id: String(Date.now()), ...form }]);
    }
    setShowDialog(false);
  };

  const toggleDay = (day: string) => setForm({ ...form, days: form.days.includes(day) ? form.days.filter(d => d !== day) : [...form.days, day] });

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <h3 className="font-display font-bold text-lg text-foreground">Shift Management</h3>
        <Button onClick={openNew} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> Add Shift</Button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {shifts.map((shift, i) => (
          <motion.div key={shift.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all">
            <div className="flex items-center justify-between mb-3">
              <h4 className="font-display font-bold text-foreground">{shift.name}</h4>
              <div className="flex gap-1">
                <button onClick={() => openEdit(shift)} className="p-1.5 rounded hover:bg-muted"><Edit2 className="w-3.5 h-3.5 text-muted-foreground" /></button>
                <button onClick={() => setShifts(shifts.filter(s => s.id !== shift.id))} className="p-1.5 rounded hover:bg-destructive/10"><Trash2 className="w-3.5 h-3.5 text-destructive" /></button>
              </div>
            </div>
            <div className="space-y-2 text-sm">
              <div className="flex justify-between"><span className="text-muted-foreground">Hours</span><span className="text-foreground font-medium">{shift.start} – {shift.end}</span></div>
              <div className="flex justify-between"><span className="text-muted-foreground">Grace Period</span><span className="text-foreground">{shift.grace} min</span></div>
              <div className="flex justify-between"><span className="text-muted-foreground">Overtime After</span><span className="text-foreground">{shift.overtimeThreshold}h</span></div>
              <div className="flex gap-1 mt-2">
                {allDays.map(d => (
                  <span key={d} className={`px-2 py-0.5 rounded text-[10px] font-medium ${shift.days.includes(d) ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'}`}>{d}</span>
                ))}
              </div>
            </div>
          </motion.div>
        ))}
      </div>

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">{editing ? 'Edit Shift' : 'New Shift'}</DialogTitle></DialogHeader>
          <div className="space-y-4 mt-2">
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Shift Name *</label><Input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Morning Shift" /></div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-sm font-semibold text-foreground mb-1 block">Start Time</label><Input type="time" value={form.start} onChange={e => setForm({ ...form, start: e.target.value })} /></div>
              <div><label className="text-sm font-semibold text-foreground mb-1 block">End Time</label><Input type="time" value={form.end} onChange={e => setForm({ ...form, end: e.target.value })} /></div>
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-2 block">Working Days</label>
              <div className="flex gap-2">{allDays.map(d => (
                <button key={d} onClick={() => toggleDay(d)} className={`px-3 py-1.5 rounded-lg text-xs font-medium border transition-all ${form.days.includes(d) ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground'}`}>{d}</button>
              ))}</div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-sm font-semibold text-foreground mb-1 block">Grace Period (min)</label><Input type="number" value={form.grace} onChange={e => setForm({ ...form, grace: Number(e.target.value) })} /></div>
              <div><label className="text-sm font-semibold text-foreground mb-1 block">Overtime Threshold (hrs)</label><Input type="number" value={form.overtimeThreshold} onChange={e => setForm({ ...form, overtimeThreshold: Number(e.target.value) })} /></div>
            </div>
            <Button onClick={save} className="w-full gradient-primary text-primary-foreground">{editing ? 'Update' : 'Create'} Shift</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
