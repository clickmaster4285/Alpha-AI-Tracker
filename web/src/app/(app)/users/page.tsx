'use client';

import { useState } from 'react';
import Link from 'next/link';
import { motion } from 'framer-motion';
import { Search, Plus, MoreVertical, Loader2, Key, Copy, Check, Eye } from 'lucide-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { employeesApi, departmentsApi, type Employee, type CreateEmployeePayload, type UpdateEmployeePayload } from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu';

export default function UsersList() {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [deptFilter, setDeptFilter] = useState('');
  const [page, setPage] = useState(1);
  const [showAdd, setShowAdd] = useState(false);
  const [showEdit, setShowEdit] = useState<string | null>(null);
  const [showSecret, setShowSecret] = useState<string | null>(null);
  const [secretValue, setSecretValue] = useState('');
  const [copied, setCopied] = useState(false);

  // Form state
  const [newName, setNewName] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [newDeptId, setNewDeptId] = useState(1);
  const [newRole, setNewRole] = useState('employee');
  const [editName, setEditName] = useState('');
  const [editEmail, setEditEmail] = useState('');
  const [editDeptId, setEditDeptId] = useState(1);

  // ── Queries ──
  const { data: employeesData, isLoading: employeesLoading, error: employeesError } = useQuery({
    queryKey: ['employees', { page, search, department: deptFilter }],
    queryFn: () => employeesApi.list({ page, perPage: 10, search: search || undefined, department: deptFilter || undefined }),
  });

  const { data: deptResponse } = useQuery({
    queryKey: ['departments'],
    queryFn: () => departmentsApi.list(),
  });

  const departments = deptResponse?.departments || [];

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: CreateEmployeePayload) => employeesApi.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Employee created', { description: 'The employee has been added successfully.' });
      setShowAdd(false);
      resetForm();
    },
    onError: (err: Error) => {
      toast.error('Failed to create employee', { description: err.message });
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateEmployeePayload }) => employeesApi.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Employee updated', { description: 'The employee has been updated successfully.' });
      setShowEdit(null);
    },
    onError: (err: Error) => {
      toast.error('Failed to update employee', { description: err.message });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => employeesApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Employee deleted', { description: 'The employee has been removed.' });
    },
    onError: (err: Error) => {
      toast.error('Failed to delete employee', { description: err.message });
    },
  });

  const secretMutation = useMutation({
    mutationFn: (id: string) => employeesApi.generateSecret(id),
    onSuccess: (data) => {
      setSecretValue(data.secret);
      toast.success('Login secret generated!', {
        description: `Secret expires in ${data.expiresIn} seconds. Copy it now — it won't be shown again.`,
        duration: 8000,
      });
    },
    onError: (err: Error) => {
      toast.error('Failed to generate secret', { description: err.message });
    },
  });

  const resetForm = () => {
    setNewName('');
    setNewEmail('');
    setNewDeptId(1);
    setNewRole('employee');
  };

  const handleAdd = () => {
    if (!newName) {
      toast.error('Validation error', { description: 'Name is required.' });
      return;
    }
    createMutation.mutate({
      name: newName,
      email: newEmail,
      departmentId: newDeptId,
      role: newRole,
      shift: 'Day',
    });
  };

  const handleEdit = (emp: Employee) => {
    setShowEdit(emp.id);
    setEditName(emp.name);
    setEditEmail(emp.email);
    setEditDeptId(emp.departmentId);
  };

  const handleSaveEdit = (id: string) => {
    updateMutation.mutate({
      id,
      data: {
        name: editName,
        email: editEmail,
        departmentId: editDeptId,
      },
    });
  };

  const handleDelete = (id: string) => {
    deleteMutation.mutate(id);
  };

  const handleGenerateSecret = (id: string) => {
    setShowSecret(id);
    setSecretValue('');
    setCopied(false);
    secretMutation.mutate(id);
  };

  const handleCopySecret = () => {
    navigator.clipboard.writeText(secretValue);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const employees = employeesData?.data || [];
  const totalPages = employeesData?.totalPages || 1;

  // ── Loading state ──
  if (employeesLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Loading employees...</p>
        </div>
      </div>
    );
  }

  // ── Error state ──
  if (employeesError) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Failed to load employees</p>
          <p className="text-sm text-muted-foreground">{(employeesError as Error).message}</p>
          <button
            onClick={() => queryClient.invalidateQueries({ queryKey: ['employees'] })}
            className="mt-4 text-sm text-primary hover:underline"
          >
            Try again
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Filters */}
      <div className="flex justify-between">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={search}
            onChange={e => { setSearch(e.target.value); setPage(1); }}
            placeholder="Search by name, email, or ID"
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <div className='flex space-x-3'>
          <select
            value={deptFilter}
            onChange={e => { setDeptFilter(e.target.value); setPage(1); }}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Departments</option>
            {departments.map(d => <option key={d.id} value={d.name}>{d.name}</option>)}
          </select>
          <button
            onClick={() => setShowAdd(true)}
            className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity"
          >
            <Plus className="w-4 h-4" /> Add Employee
          </button>
        </div>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['Name', 'Employee ID', 'Email', 'Department', 'Role', 'Status', 'Action'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {employees.length === 0 ? (
              <tr>
                <td colSpan={7} className="text-center py-12 text-muted-foreground text-sm">
                  No employees found
                </td>
              </tr>
            ) : (
              employees.map((emp, i) => (
                <motion.tr
                  key={emp.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: i * 0.03 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
                >
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <div
                        className="w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground"
                        style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                      >
                        {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                      </div>
                      <Link
                        href={`/users/${emp.id}`}
                        className="text-sm font-medium text-foreground hover:text-primary hover:underline underline-offset-4 transition-colors"
                      >
                        {emp.name}
                      </Link>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{emp.employeeId}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{emp.email}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{emp.department}</td>
                  <td className="px-4 py-3">
                    <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-primary/10 text-primary">
                      {emp.role}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${
                      emp.trackingStatus === 'tracked'
                        ? 'bg-success/15 text-success'
                        : 'bg-warning/15 text-warning'
                    }`}>
                      {emp.trackingStatus === 'tracked' ? 'Tracked' : 'Untracked'}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <button className="p-1.5 rounded hover:bg-muted transition-colors" aria-label="Employee actions">
                          <MoreVertical className="w-4 h-4 text-muted-foreground" />
                        </button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end" className="bg-card border-border rounded-lg min-w-[180px]">
                        <DropdownMenuItem asChild className="cursor-pointer">
                          <Link href={`/users/${emp.id}`} className="flex items-center gap-2">
                            <Eye className="w-3.5 h-3.5" />
                            View Details
                          </Link>
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => handleEdit(emp)} className="cursor-pointer">
                          Edit
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => handleGenerateSecret(emp.id)} className="cursor-pointer flex items-center gap-2">
                          <Key className="w-3.5 h-3.5" />
                          Generate Login Secret
                        </DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem
                          onSelect={() => handleDelete(emp.id)}
                          disabled={deleteMutation.isPending}
                          className="cursor-pointer text-destructive focus:text-destructive focus:bg-destructive/10"
                        >
                          {deleteMutation.isPending ? 'Deleting...' : 'Delete'}
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </td>
                </motion.tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-muted-foreground">
            Showing page {page} of {totalPages} ({employeesData?.total || 0} total employees)
          </p>
          <div className="flex gap-2">
            <button
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={page <= 1}
              className="px-3 py-1.5 rounded-lg border border-border text-sm text-foreground disabled:opacity-50 hover:bg-muted transition-colors"
            >
              Previous
            </button>
            <button
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages}
              className="px-3 py-1.5 rounded-lg border border-border text-sm text-foreground disabled:opacity-50 hover:bg-muted transition-colors"
            >
              Next
            </button>
          </div>
        </div>
      )}

      {/* Add Dialog */}
      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Add New Employee</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="Full Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <input
              value={newEmail}
              onChange={e => setNewEmail(e.target.value)}
              placeholder="Email"
              type="email"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={newDeptId}
              onChange={e => setNewDeptId(Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
            <select
              value={newRole}
              onChange={e => setNewRole(e.target.value)}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="employee">Employee</option>
              <option value="manager">Manager</option>
              <option value="hr_admin">HR Admin</option>
              <option value="org_admin">Org Admin</option>
            </select>
            <button
              onClick={handleAdd}
              disabled={createMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {createMutation.isPending ? 'Creating...' : 'Add Employee'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog open={!!showEdit} onOpenChange={(open) => { if (!open) setShowEdit(null); }}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Edit Employee</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={editName}
              onChange={e => setEditName(e.target.value)}
              placeholder="Full Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <input
              value={editEmail}
              onChange={e => setEditEmail(e.target.value)}
              placeholder="Email"
              type="email"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={editDeptId}
              onChange={e => setEditDeptId(Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
            <button
              onClick={() => showEdit && handleSaveEdit(showEdit)}
              disabled={updateMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Secret Dialog */}
      <Dialog open={showSecret !== null} onOpenChange={(open) => { if (!open) { setShowSecret(null); setSecretValue(''); setCopied(false); } }}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Login Secret Generated</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <p className="text-sm text-muted-foreground">
              Share this secret with the employee. It expires in 5 minutes and can only be used once.
            </p>
            {secretMutation.isPending ? (
              <div className="flex items-center justify-center py-6">
                <Loader2 className="w-6 h-6 animate-spin text-primary" />
              </div>
            ) : secretValue ? (
              <div className="flex items-center gap-2 bg-background border border-border rounded-lg p-3">
                <code className="flex-1 text-sm font-mono font-bold text-foreground select-all">{secretValue}</code>
                <button
                  onClick={handleCopySecret}
                  className="p-2 rounded-lg hover:bg-muted transition-colors"
                  title="Copy secret"
                >
                  {copied ? (
                    <Check className="w-4 h-4 text-success" />
                  ) : (
                    <Copy className="w-4 h-4 text-muted-foreground" />
                  )}
                </button>
              </div>
            ) : (
              <p className="text-sm text-destructive">Failed to generate secret. Try again.</p>
            )}
            <button
              onClick={() => { setShowSecret(null); setSecretValue(''); setCopied(false); }}
              className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
            >
              Close
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
