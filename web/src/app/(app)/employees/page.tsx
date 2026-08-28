'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { motion } from 'framer-motion';
import { Search, Plus, MoreVertical, Loader2, Key, Copy, Check, Eye, Monitor, Upload, Download, Pencil, Trash2, AlertTriangle, UserPlus } from 'lucide-react';
import { useQuery, useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import * as XLSX from 'xlsx';
import { employeesApi, departmentsApi, usersApi, shiftsApi, type Employee, type CreateEmployeePayload, type UpdateEmployeePayload, type ImportEmployeeRow, type ImportEmployeesResponse, type Shift } from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuLabel,
} from '@/components/ui/dropdown-menu';

// Excel header aliases → canonical field. Headers are matched case-insensitively
// and whitespace-normalized, so "User ID", "user_id", "EMPLOYEEID", "employee name"
// and "username" all resolve. Only these columns are extracted from the sheet.
const HEADER_FIELD: Record<string, keyof ImportEmployeeRow> = {
  userid: 'employeeId',
  user_id: 'employeeId',
  'user id': 'employeeId',
  employeeid: 'employeeId',
  employee_id: 'employeeId',
  'employee id': 'employeeId',
  name: 'name',
  'employee name': 'name',
  username: 'name',
  email: 'email',
  department: 'department',
  shift: 'shift',
  schedule: 'shift',
};

const normalizeHeader = (raw: string) =>
  String(raw ?? '').trim().toLowerCase().replace(/\s+/g, ' ');

const PER_PAGE = 10;

export default function UsersList() {
  const queryClient = useQueryClient();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput), 400);
    return () => clearTimeout(t);
  }, [searchInput]);
  const [deptFilter, setDeptFilter] = useState('');
  const [showAdd, setShowAdd] = useState(false);
  const [showEdit, setShowEdit] = useState<string | null>(null);
  const [showSecret, setShowSecret] = useState<Employee | null>(null);
  const [secretValue, setSecretValue] = useState('');
  const [copied, setCopied] = useState(false);
  const [importResult, setImportResult] = useState<ImportEmployeesResponse | null>(null);

  // Form state
  const [newName, setNewName] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [newDeptId, setNewDeptId] = useState(1);
  const [newShiftId, setNewShiftId] = useState<number | null>(null);
  const [editName, setEditName] = useState('');
  const [editEmail, setEditEmail] = useState('');
  const [editDeptId, setEditDeptId] = useState(1);
  const [editShiftId, setEditShiftId] = useState<number | null>(null);

  // ── Queries ──
  // Server-side pagination with infinite scroll (same pattern as the Session
  // Timeline / Web Activity journey pages). The Next/Previous buttons are
  // banned — see AGENTS.md §6 (Web Infinite-Scroll Rule).
  const {
    data: employeesData,
    isLoading: employeesLoading,
    error: employeesError,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['employees', { search, department: deptFilter, perPage: PER_PAGE }],
    queryFn: ({ pageParam }) => employeesApi.list({
      page: pageParam as number,
      perPage: PER_PAGE,
      search: search || undefined,
      department: deptFilter || undefined,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

  const employees = useMemo(() => employeesData?.pages.flatMap(p => p.data) ?? [], [employeesData]);
  const total = employeesData?.pages[0]?.total ?? 0;

  // Sentinel that triggers the next page fetch when scrolled into view.
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const { data: deptResponse } = useQuery({
    queryKey: ['departments'],
    queryFn: () => departmentsApi.list(),
  });

  const departments = deptResponse?.departments || [];

  // Shifts dropdown source — fetched once, used by both Add and Edit dialogs.
  // Mirrors the departments query: listAll returns the full unpaged catalog.
  const { data: shiftsResponse } = useQuery({
    queryKey: ['shifts', 'all'],
    queryFn: () => shiftsApi.listAll(),
    staleTime: 60_000,
  });

  const shifts: Shift[] = shiftsResponse?.shifts || [];

  // hasUserLogin comes from the server (indexed EXISTS() per row) — no
  // client-side map or extra round-trips needed, and it scales with the
  // paginated list instead of growing with total user count.

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
    mutationFn: async ({ id, data }: { id: string; data: UpdateEmployeePayload }) => {
      const updated = await employeesApi.update(id, data);
      // If this employee has a linked login account, propagate name/email
      // changes to the user record so the two surfaces stay in sync. The
      // employeeId is immutable from both sides, so it never needs syncing.
      // The hasUserLogin flag is projected by the server's UPDATE…RETURNING
      // so this check is free (no extra round-trip) and always fresh.
      if (updated.hasUserLogin) {
        try {
          const userList = await usersApi.list({ search: updated.employeeId, perPage: 1 });
          const linkedUser = userList.data.find(u => u.employeeId === updated.employeeId);
          if (linkedUser) {
            const payload: { name?: string; email?: string } = {};
            if (data.name !== undefined) payload.name = updated.name;
            if (data.email !== undefined) payload.email = updated.email;
            if (Object.keys(payload).length > 0) {
              await usersApi.update(linkedUser.id, payload);
            }
          }
        } catch (e) {
          // Non-fatal: the employee update succeeded; the user sync failed.
          toast.warning('Attached login account not synced', {
            description: (e as Error).message,
          });
        }
      }
      return updated;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      queryClient.invalidateQueries({ queryKey: ['users'] });
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

  const importMutation = useMutation({
    mutationFn: (rows: ImportEmployeeRow[]) => employeesApi.import(rows),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      queryClient.invalidateQueries({ queryKey: ['departments'] });
      setImportResult(data);
      toast.success('Import complete', {
        description: `${data.imported} imported, ${data.updated} updated, ${data.skipped} skipped.`,
      });
    },
    onError: (err: Error) => {
      toast.error('Import failed', { description: err.message });
    },
  });

  const exportMutation = useMutation({
    mutationFn: () => employeesApi.export(),
    onSuccess: (rows) => {
      const aoa: (string | number)[][] = [
        ['Employee ID', 'Name', 'Email', 'Department', 'Shift'],
        ...rows.map(r => [r.employeeId, r.name, r.email, r.department, r.shift]),
      ];
      const ws = XLSX.utils.aoa_to_sheet(aoa);
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, ws, 'Employees');
      XLSX.writeFile(wb, `employees-${new Date().toISOString().slice(0, 10)}.xlsx`);
      toast.success('Export complete', { description: `${rows.length} employees exported.` });
    },
    onError: (err: Error) => {
      toast.error('Export failed', { description: err.message });
    },
  });

  const handleImportFile = (file: File) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (e) => {
      try {
        const wb = XLSX.read(e.target?.result, { type: 'array' });
        const ws = wb.Sheets[wb.SheetNames[0]];
        if (!ws) {
          toast.error('Import failed', { description: 'The workbook has no sheets.' });
          return;
        }
        const rawRows = XLSX.utils.sheet_to_json<Record<string, unknown>>(ws, { defval: '' });
        const rows = rawRows
          .map(raw => {
            const row: ImportEmployeeRow = { employeeId: '', name: '', email: '', department: '' };
            for (const [header, value] of Object.entries(raw)) {
              const field = HEADER_FIELD[normalizeHeader(header)];
              if (field) row[field] = String(value ?? '').trim();
            }
            return row;
          })
          .filter(row => row.employeeId || row.name);
        if (rows.length === 0) {
          toast.error('Import failed', {
            description: 'No recognized rows. Expected headers like User ID, Name, Email, Department.',
          });
          return;
        }
        importMutation.mutate(rows);
      } catch (err) {
        toast.error('Import failed', { description: (err as Error).message });
      }
    };
    reader.readAsArrayBuffer(file);
  };

  const resetForm = () => {
    setNewName('');
    setNewEmail('');
    setNewDeptId(1);
    setNewShiftId(null);
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
      shiftId: newShiftId,
    });
  };

  const handleEdit = (emp: Employee) => {
    setShowEdit(emp.id);
    setEditName(emp.name);
    setEditEmail(emp.email);
    setEditDeptId(emp.departmentId);
    setEditShiftId(emp.shiftId);
  };

  const handleSaveEdit = (id: string) => {
    updateMutation.mutate({
      id,
      data: {
        name: editName,
        email: editEmail,
        departmentId: editDeptId,
        shiftId: editShiftId,
      },
    });
  };

  const handleDelete = (id: string) => {
    deleteMutation.mutate(id);
  };

  const handleGenerateSecret = (emp: Employee) => {
    setShowSecret(emp);
    setSecretValue('');
    setCopied(false);
    secretMutation.mutate(emp.id);
  };

  const copyToClipboard = async (text: string): Promise<boolean> => {
    if (navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // fall through to legacy fallback
      }
    }
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.top = '0';
    textarea.style.left = '0';
    textarea.style.opacity = '0';
    textarea.readOnly = true;
    document.body.appendChild(textarea);
    textarea.select();
    let ok = false;
    try {
      ok = document.execCommand('copy');
    } catch {
      ok = false;
    } finally {
      document.body.removeChild(textarea);
    }
    return ok;
  };

  const handleCopySecret = async () => {
    const ok = await copyToClipboard(secretValue);
    if (ok) {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } else {
      toast.error('Copy failed', { description: 'Your browser blocked clipboard access. Copy the secret manually.' });
    }
  };

  // ── Loading state ──
  if (employeesLoading && !employeesData) {
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
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search by name, email, or ID"
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <div className='flex space-x-3'>
          <select
            value={deptFilter}
            onChange={e => setDeptFilter(e.target.value)}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Departments</option>
            {departments.map(d => <option key={d.id} value={d.name}>{d.name}</option>)}
          </select>
          <input
            ref={fileInputRef}
            type="file"
            accept=".xlsx,.xls,.csv"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) handleImportFile(file);
              e.target.value = '';
            }}
          />
          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={importMutation.isPending}
            className="bg-card border border-border text-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:bg-muted transition-colors disabled:opacity-50"
          >
            <Upload className="w-4 h-4" />
            {importMutation.isPending ? 'Importing...' : 'Import'}
          </button>
          <button
            onClick={() => exportMutation.mutate()}
            disabled={exportMutation.isPending}
            className="bg-card border border-border text-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:bg-muted transition-colors disabled:opacity-50"
          >
            <Download className="w-4 h-4" />
            {exportMutation.isPending ? 'Exporting...' : 'Export'}
          </button>
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
              {['Name', 'Employee ID', 'Email', 'Department', 'Shift', 'Status', 'Action'].map(h => (
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
                      <div
                        className="text-sm font-medium text-foreground hover:text-primary transition-colors"
                      >
                        {emp.name}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{emp.employeeId}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{emp.email}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{emp.department}</td>
                  <td className="px-4 py-3 text-sm text-foreground">
                    {emp.shift || <span className="text-muted-foreground/50 italic">Unassigned</span>}
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
                        <button
                          className="p-2 rounded-lg text-muted-foreground hover:text-foreground hover:bg-muted/70 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                          aria-label={`Actions for ${emp.name}`}
                        >
                          <MoreVertical className="w-4 h-4" />
                        </button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end" sideOffset={8} className="w-56 rounded-xl bg-card border-border shadow-xl p-1.5">
                        <DropdownMenuLabel className="flex items-center gap-3 px-2.5 py-2.5">
                          <div
                            className="w-9 h-9 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground shrink-0"
                            style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                          >
                            {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                          </div>
                          <div className="min-w-0">
                            <p className="text-sm font-semibold text-foreground truncate">{emp.name}</p>
                            <p className="text-xs text-muted-foreground truncate">{emp.email}</p>
                          </div>
                        </DropdownMenuLabel>
                        <DropdownMenuSeparator className="my-1.5" />
                        <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Link href={`/employee-journey/timeline?employeeId=${emp.id}`}>
                            <Eye className="w-4 h-4 text-muted-foreground" />
                            View Journey
                          </Link>
                        </DropdownMenuItem>
                        <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Link href={`/device-specs?employeeId=${emp.id}`}>
                            <Monitor className="w-4 h-4 text-muted-foreground" />
                            Device Specs
                          </Link>
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => handleEdit(emp)} className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Pencil className="w-4 h-4 text-muted-foreground" />
                          Edit Details
                        </DropdownMenuItem>
                        {!emp.hasUserLogin && (
                          <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                            <Link href={`/settings/user-management?create=1&employeeId=${encodeURIComponent(emp.employeeId)}&name=${encodeURIComponent(emp.name)}&email=${encodeURIComponent(emp.email)}`}>
                              <UserPlus className="w-4 h-4 text-muted-foreground" />
                              Login Credential
                            </Link>
                          </DropdownMenuItem>
                        )}
                        <DropdownMenuItem onSelect={() => handleGenerateSecret(emp)} className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Key className="w-4 h-4 text-muted-foreground" />
                          Generate Login Secret
                        </DropdownMenuItem>
                        <DropdownMenuSeparator className="my-1.5" />
                        <DropdownMenuItem
                          onSelect={() => handleDelete(emp.id)}
                          disabled={deleteMutation.isPending}
                          className="cursor-pointer gap-2.5 px-2.5 py-2 text-destructive focus:text-destructive focus:bg-destructive/10"
                        >
                          <Trash2 className="w-4 h-4" />
                          {deleteMutation.isPending ? 'Deleting...' : 'Delete Employee'}
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

      {/* Infinite scroll footer (server-side pagination; no Next/Previous buttons) */}
      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        employees.length > 0 && (
          <p className="text-sm text-muted-foreground text-center">
            Showing all {total.toLocaleString()} employee{total === 1 ? '' : 's'}
          </p>
        )
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
              value={newShiftId ?? ''}
              onChange={e => setNewShiftId(e.target.value === '' ? null : Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">No shift assigned</option>
              {shifts.map(s => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.startTime}–{s.endTime})
                </option>
              ))}
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
            <select
              value={editShiftId ?? ''}
              onChange={e => setEditShiftId(e.target.value === '' ? null : Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">No shift assigned</option>
              {shifts.map(s => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.startTime}–{s.endTime})
                </option>
              ))}
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
        <DialogContent className="bg-card sm:max-w-[460px]">
          <DialogHeader>
            <DialogTitle className="font-display">Login Secret Generated</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            {showSecret && (
              <div className="flex items-center gap-3 bg-background border border-border rounded-xl p-3.5">
                <div
                  className="w-11 h-11 rounded-full flex items-center justify-center text-sm font-bold text-primary-foreground shrink-0"
                  style={{ backgroundColor: showSecret.avatarColor || '#7C3AED' }}
                >
                  {showSecret.avatar || showSecret.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-foreground truncate">{showSecret.name}</p>
                  <p className="text-xs text-muted-foreground truncate">{showSecret.email}</p>
                  <p className="text-xs text-muted-foreground mt-0.5">{showSecret.department}</p>
                </div>
                <div className="text-right shrink-0">
                  <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Employee ID</p>
                  <p className="text-sm font-mono font-medium text-foreground">{showSecret.employeeId}</p>
                </div>
              </div>
            )}

            <div className="flex items-start gap-2.5 bg-warning/10 border border-warning/30 rounded-xl p-3">
              <AlertTriangle className="w-4 h-4 text-warning mt-0.5 shrink-0" />
              <p className="text-xs text-foreground">
                This secret is <span className="font-semibold text-warning">valid for 5 minutes</span> and can only be
                used once. It won't be shown again after you close this dialog — copy it now.
              </p>
            </div>

            {secretMutation.isPending ? (
              <div className="flex items-center justify-center py-6">
                <Loader2 className="w-6 h-6 animate-spin text-primary" />
              </div>
            ) : secretValue ? (
              <div className="bg-background border border-border rounded-xl p-1.5 flex items-center gap-1.5">
                <code className="flex-1 text-sm font-mono font-bold text-foreground select-all px-2.5 py-2 truncate">{secretValue}</code>
                <button
                  onClick={handleCopySecret}
                  className={`px-3 py-2 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition-colors shrink-0 ${
                    copied
                      ? 'bg-success/15 text-success'
                      : 'bg-primary text-primary-foreground hover:opacity-90'
                  }`}
                >
                  {copied ? (
                    <><Check className="w-3.5 h-3.5" /> Copied</>
                  ) : (
                    <><Copy className="w-3.5 h-3.5" /> Copy</>
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
      {/* Import Result Dialog */}
      <Dialog open={importResult !== null} onOpenChange={(open) => { if (!open) setImportResult(null); }}>
        <DialogContent className="bg-card sm:max-w-[640px]">
          <DialogHeader>
            <DialogTitle className="font-display">Import Complete</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div className="flex gap-4">
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-success">{importResult?.imported ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Imported</div>
              </div>
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-foreground">{importResult?.updated ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Updated</div>
              </div>
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-warning">{importResult?.skipped ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Skipped</div>
              </div>
            </div>
            {importResult && importResult.skipped > 0 && (
              <div className="max-h-64 overflow-y-auto border border-border rounded-lg">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-border text-left text-muted-foreground text-xs uppercase">
                      <th className="px-3 py-2 font-semibold">Row</th>
                      <th className="px-3 py-2 font-semibold">Employee ID</th>
                      <th className="px-3 py-2 font-semibold">Name</th>
                      <th className="px-3 py-2 font-semibold">Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {importResult.results.filter(r => r.status === 'skipped').map(r => (
                      <tr key={r.rowIndex} className="border-b border-border last:border-0">
                        <td className="px-3 py-2 text-muted-foreground">{r.rowIndex}</td>
                        <td className="px-3 py-2 font-mono">{r.employeeId || '-'}</td>
                        <td className="px-3 py-2">{r.name || '-'}</td>
                        <td className="px-3 py-2 text-destructive">{r.reason}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <button
              onClick={() => setImportResult(null)}
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
