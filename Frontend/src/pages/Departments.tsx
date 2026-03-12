import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, MoreVertical, Users, Building2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { toast } from 'sonner';

interface Department {
  id: string;
  name: string;
  employeeCount: number;
  manager: string;
}

const initialDepts: Department[] = [
  { id: '1', name: 'Engineering', employeeCount: 5, manager: 'Rakesh Pathania' },
  { id: '2', name: 'Design', employeeCount: 2, manager: 'Priya Mehta' },
  { id: '3', name: 'Marketing', employeeCount: 1, manager: 'Muskaan Makkad' },
  { id: '4', name: 'Sales', employeeCount: 1, manager: 'Ravi Kumar' },
  { id: '5', name: 'HR', employeeCount: 1, manager: 'Savi Chopra' },
  { id: '6', name: 'Finance', employeeCount: 1, manager: 'Anisha Jassal' },
  { id: '7', name: 'QA', employeeCount: 1, manager: 'Kamal Dhami' },
  { id: '8', name: 'DevOps', employeeCount: 1, manager: 'Tarun Saini' },
];

export default function Departments() {
  const [depts, setDepts] = useState(initialDepts);
  const [showAdd, setShowAdd] = useState(false);
  const [newName, setNewName] = useState('');
  const [newManager, setNewManager] = useState('');

  const handleAdd = () => {
    if (!newName) return;
    setDepts(prev => [...prev, { id: String(Date.now()), name: newName, employeeCount: 0, manager: newManager || 'Unassigned' }]);
    setNewName(''); setNewManager('');
    setShowAdd(false);
    toast.success('Department added!');
  };

  const handleDelete = (id: string) => {
    setDepts(prev => prev.filter(d => d.id !== id));
    toast.success('Department removed');
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <p className="text-sm text-muted-foreground">{depts.length} departments</p>
        <button onClick={() => setShowAdd(true)} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90">
          <Plus className="w-4 h-4" /> Add Department
        </button>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {depts.map((dept, i) => (
          <motion.div
            key={dept.id}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.04 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all group"
          >
            <div className="flex items-start justify-between mb-3">
              <div className="w-10 h-10 rounded-lg bg-accent flex items-center justify-center">
                <Building2 className="w-5 h-5 text-accent-foreground" />
              </div>
              <button onClick={() => handleDelete(dept.id)} className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-muted transition-all">
                <MoreVertical className="w-4 h-4 text-muted-foreground" />
              </button>
            </div>
            <h3 className="font-display font-bold text-foreground mb-1">{dept.name}</h3>
            <p className="text-xs text-muted-foreground mb-3">Manager: {dept.manager}</p>
            <div className="flex items-center gap-1 text-xs text-muted-foreground">
              <Users className="w-3.5 h-3.5" />
              <span>{dept.employeeCount} employees</span>
            </div>
          </motion.div>
        ))}
      </div>

      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Add Department</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input value={newName} onChange={e => setNewName(e.target.value)} placeholder="Department Name" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <input value={newManager} onChange={e => setNewManager(e.target.value)} placeholder="Manager Name" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <button onClick={handleAdd} className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90">Add Department</button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
