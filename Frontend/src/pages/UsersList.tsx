import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, Plus, MoreVertical, Filter } from 'lucide-react';
import { getEmployees, getDepartments, addEmployee, deleteEmployee, type Employee } from '@/lib/store';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

export default function UsersList() {
  const [employees, setEmployees] = useState(() => getEmployees());
  const departments = useMemo(() => getDepartments(), []);
  const [search, setSearch] = useState('');
  const [deptFilter, setDeptFilter] = useState('');
  const [showAdd, setShowAdd] = useState(false);
  const [newName, setNewName] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [newDept, setNewDept] = useState('Engineering');
  const [newEmpId, setNewEmpId] = useState('');
  const [openAction, setOpenAction] = useState<string | null>(null);

  const filtered = employees.filter(e => {
    const matchSearch = e.name.toLowerCase().includes(search.toLowerCase()) || e.email.toLowerCase().includes(search.toLowerCase());
    const matchDept = !deptFilter || e.department === deptFilter;
    return matchSearch && matchDept;
  });

  const handleAdd = () => {
    if (!newName || !newEmail) return;
    const emp = addEmployee({
      name: newName,
      email: newEmail,
      employeeId: newEmpId || String(Math.floor(Math.random() * 9000) + 1000),
      department: newDept,
      role: 'Employee',
      trackingEnabled: true,
      trackingStatus: 'untracked',
      isOnline: false,
      shift: 'Day',
    });
    setEmployees(prev => [...prev, emp]);
    setNewName(''); setNewEmail(''); setNewEmpId('');
    setShowAdd(false);
  };

  const handleDelete = (id: string) => {
    deleteEmployee(id);
    setEmployees(prev => prev.filter(e => e.id !== id));
    setOpenAction(null);
  };

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search Users" className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
        </div>
        <select value={deptFilter} onChange={e => setDeptFilter(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option value="">All Departments</option>
          {departments.map(d => <option key={d} value={d}>{d}</option>)}
        </select>
        <button onClick={() => setShowAdd(true)} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity">
          <Plus className="w-4 h-4" /> Add Users
        </button>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['Name', 'Email', 'Employee ID', 'Tracking Enabled', 'Tracking Status', 'Role', 'Action'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filtered.map((emp, i) => (
              <motion.tr
                key={emp.id}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: i * 0.03 }}
                className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
              >
                <td className="px-4 py-3">
                  <div className="flex items-center gap-3">
                    <div className="w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground" style={{ backgroundColor: emp.avatarColor }}>
                      {emp.avatar}
                    </div>
                    <span className="text-sm font-medium text-foreground">{emp.name}</span>
                  </div>
                </td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{emp.email}</td>
                <td className="px-4 py-3 text-sm text-foreground">{emp.employeeId}</td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${emp.trackingEnabled ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>
                    {emp.trackingEnabled ? 'Yes' : 'No'}
                  </span>
                </td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${emp.trackingStatus === 'tracked' ? 'bg-success/15 text-success' : 'bg-warning/15 text-warning'}`}>
                    {emp.trackingStatus === 'tracked' ? 'Tracked' : 'Untracked'}
                  </span>
                </td>
                <td className="px-4 py-3 text-sm text-foreground">{emp.role}</td>
                <td className="px-4 py-3 relative">
                  <button onClick={() => setOpenAction(openAction === emp.id ? null : emp.id)} className="p-1.5 rounded hover:bg-muted transition-colors">
                    <MoreVertical className="w-4 h-4 text-muted-foreground" />
                  </button>
                  {openAction === emp.id && (
                    <div className="absolute right-4 top-12 bg-card border border-border rounded-lg shadow-lg z-10 py-1 min-w-[120px]">
                      <button className="w-full text-left px-4 py-2 text-sm hover:bg-muted transition-colors text-foreground">Edit</button>
                      <button onClick={() => handleDelete(emp.id)} className="w-full text-left px-4 py-2 text-sm hover:bg-muted transition-colors text-destructive">Delete</button>
                    </div>
                  )}
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Add Dialog */}
      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Add New User</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input value={newName} onChange={e => setNewName(e.target.value)} placeholder="Full Name" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <input value={newEmail} onChange={e => setNewEmail(e.target.value)} placeholder="Email" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <input value={newEmpId} onChange={e => setNewEmpId(e.target.value)} placeholder="Employee ID" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <select value={newDept} onChange={e => setNewDept(e.target.value)} className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground">
              {departments.map(d => <option key={d} value={d}>{d}</option>)}
            </select>
            <button onClick={handleAdd} className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity">Add User</button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
