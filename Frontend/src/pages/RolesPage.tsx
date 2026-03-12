import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Shield, Edit2, Trash2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { toast } from 'sonner';

interface Role {
  id: string;
  name: string;
  description: string;
  permissions: string[];
  userCount: number;
}

const allPermissions = ['View Dashboard', 'Manage Users', 'View Screenshots', 'View Logs', 'Manage Departments', 'Manage Settings', 'View Live Stream', 'Manage Roles', 'Export Data', 'Manage Projects'];

const initialRoles: Role[] = [
  { id: '1', name: 'Super Admin', description: 'Full access to all features', permissions: allPermissions, userCount: 1 },
  { id: '2', name: 'Manager', description: 'Can view reports and manage team', permissions: ['View Dashboard', 'Manage Users', 'View Screenshots', 'View Logs', 'View Live Stream', 'Export Data'], userCount: 2 },
  { id: '3', name: 'Employee', description: 'Basic access to own data', permissions: ['View Dashboard'], userCount: 9 },
  { id: '4', name: 'HR', description: 'Access to HR-related features', permissions: ['View Dashboard', 'Manage Users', 'View Logs', 'Manage Departments'], userCount: 1 },
];

export default function RolesPage() {
  const [roles, setRoles] = useState(initialRoles);
  const [showAdd, setShowAdd] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDesc, setNewDesc] = useState('');
  const [newPerms, setNewPerms] = useState<string[]>([]);

  const togglePerm = (p: string) => setNewPerms(prev => prev.includes(p) ? prev.filter(x => x !== p) : [...prev, p]);

  const handleAdd = () => {
    if (!newName) return;
    setRoles(prev => [...prev, { id: String(Date.now()), name: newName, description: newDesc, permissions: newPerms, userCount: 0 }]);
    setNewName(''); setNewDesc(''); setNewPerms([]);
    setShowAdd(false);
    toast.success('Role added!');
  };

  const handleDelete = (id: string) => {
    setRoles(prev => prev.filter(r => r.id !== id));
    toast.success('Role removed');
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <p className="text-sm text-muted-foreground">{roles.length} roles configured</p>
        <button onClick={() => setShowAdd(true)} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90">
          <Plus className="w-4 h-4" /> Add Role
        </button>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {roles.map((role, i) => (
          <motion.div key={role.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all group">
            <div className="flex items-start justify-between mb-3">
              <div className="w-10 h-10 rounded-lg gradient-primary flex items-center justify-center">
                <Shield className="w-5 h-5 text-primary-foreground" />
              </div>
              <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                <button className="p-1 rounded hover:bg-muted"><Edit2 className="w-3.5 h-3.5 text-muted-foreground" /></button>
                <button onClick={() => handleDelete(role.id)} className="p-1 rounded hover:bg-muted"><Trash2 className="w-3.5 h-3.5 text-destructive" /></button>
              </div>
            </div>
            <h3 className="font-display font-bold text-foreground mb-1">{role.name}</h3>
            <p className="text-xs text-muted-foreground mb-3">{role.description}</p>
            <div className="flex flex-wrap gap-1 mb-3">
              {role.permissions.slice(0, 3).map(p => (
                <span key={p} className="px-2 py-0.5 rounded-full text-[10px] bg-accent text-accent-foreground font-medium">{p}</span>
              ))}
              {role.permissions.length > 3 && <span className="px-2 py-0.5 rounded-full text-[10px] bg-muted text-muted-foreground">+{role.permissions.length - 3} more</span>}
            </div>
            <p className="text-xs text-muted-foreground">{role.userCount} users</p>
          </motion.div>
        ))}
      </div>

      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card max-w-md">
          <DialogHeader><DialogTitle className="font-display">Add Role</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input value={newName} onChange={e => setNewName(e.target.value)} placeholder="Role Name" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <input value={newDesc} onChange={e => setNewDesc(e.target.value)} placeholder="Description" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <div>
              <p className="text-sm font-medium text-foreground mb-2">Permissions</p>
              <div className="grid grid-cols-2 gap-2">
                {allPermissions.map(p => (
                  <label key={p} className="flex items-center gap-2 text-xs text-foreground">
                    <input type="checkbox" checked={newPerms.includes(p)} onChange={() => togglePerm(p)} className="rounded accent-primary" />
                    {p}
                  </label>
                ))}
              </div>
            </div>
            <button onClick={handleAdd} className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90">Add Role</button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
