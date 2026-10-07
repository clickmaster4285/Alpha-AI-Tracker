'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Check, ChevronDown, Loader2, Search, User, Users } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { employeesApi, type Employee } from '@/lib/api';
import { cn } from '@/lib/utils';

interface EmployeeSelectorProps {
  value: string;
  onChange: (employee: Employee | null) => void;
  placeholder?: string;
  className?: string;
  /** Show an "All employees" row that calls onChange(null). */
  allowAll?: boolean;
  allLabel?: string;
  /** Restrict the list to this department name (client-side, on the shared cache). */
  department?: string;
}

function matchesValue(emp: Employee, value: string): boolean {
  return emp.id === value || emp.employeeId === value;
}

/**
 * Searchable employee picker. Fetches the employee list once (shared via
 * React Query cache) and exposes the selected employee object (UUID id +
 * EMP-XXXXX code) so callers can scope sync tables and detail endpoints.
 */
export default function EmployeeSelector({
  value,
  onChange,
  placeholder = 'Select an employee…',
  className = '',
  allowAll = false,
  allLabel = 'All employees',
  department,
}: EmployeeSelectorProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const rootRef = useRef<HTMLDivElement | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
    staleTime: 5 * 60_000,
  });

  const employees = useMemo(() => {
    const rows = data?.data ?? [];
    if (!department) return rows;
    const d = department.toLowerCase();
    return rows.filter(e => (e.department || '').toLowerCase() === d);
  }, [data?.data, department]);

  const selected = value ? employees.find(e => matchesValue(e, value)) ?? null : null;

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return employees;
    return employees.filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.employeeId.toLowerCase().includes(q) ||
      e.email.toLowerCase().includes(q),
    );
  }, [employees, search]);

  const showAllRow = allowAll && (!search.trim() || allLabel.toLowerCase().includes(search.trim().toLowerCase()));

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const onDown = (ev: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(ev.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  const select = (emp: Employee) => {
    onChange(emp);
    setOpen(false);
    setSearch('');
  };

  const clearAll = () => {
    onChange(null);
    setOpen(false);
    setSearch('');
  };

  return (
    <div ref={rootRef} className={cn('relative w-full max-w-sm shrink-0', className)}>
      <button
        type="button"
        aria-label="Employee"
        aria-expanded={open}
        onClick={() => setOpen(o => !o)}
        className="w-full h-9 flex items-center gap-2 bg-card border border-border rounded-lg px-3 text-sm text-foreground hover:bg-muted/40 transition-colors"
      >
        {selected ? (
          <User className="w-4 h-4 text-muted-foreground shrink-0" />
        ) : (
          <Users className="w-4 h-4 text-muted-foreground shrink-0" />
        )}
        <span className="flex-1 text-left truncate min-w-0">
          {selected ? (
            <>
              <span className="font-medium">{selected.name}</span>
              <span className="text-muted-foreground ml-1.5 font-mono text-xs">{selected.employeeId}</span>
            </>
          ) : (
            <span className={allowAll ? 'text-foreground' : 'text-muted-foreground'}>
              {allowAll ? allLabel : placeholder}
            </span>
          )}
        </span>
        <ChevronDown className={`w-4 h-4 text-muted-foreground transition-transform shrink-0 ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && (
        <div className="absolute z-50 mt-1.5 left-0 right-0 min-w-full bg-popover border border-border rounded-xl shadow-card-hover">
          <div className="flex items-center gap-2 px-3 py-2.5 border-b border-border">
            <Search className="w-4 h-4 text-muted-foreground shrink-0" />
            <input
              autoFocus
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Search by name, ID or email…"
              className="bg-transparent border-none outline-none text-sm flex-1 min-w-0 text-foreground placeholder:text-muted-foreground"
            />
          </div>
          <div className="max-h-72 overflow-y-auto overscroll-contain p-1.5">
            {isLoading ? (
              <div className="flex items-center justify-center gap-2 py-6 text-sm text-muted-foreground">
                <Loader2 className="w-4 h-4 animate-spin" /> Loading employees…
              </div>
            ) : filtered.length === 0 && !showAllRow ? (
              <p className="text-center py-6 text-sm text-muted-foreground">No employees found</p>
            ) : (
              <>
              {showAllRow && (
                <button
                  type="button"
                  onClick={clearAll}
                  className={`w-full flex items-center gap-3 rounded-lg px-2.5 py-2 text-left text-sm hover:bg-muted/40 transition-colors ${!selected ? 'bg-sidebar-accent/40' : ''}`}
                >
                  <div className="w-7 h-7 rounded-full flex items-center justify-center bg-muted shrink-0">
                    <Users className="w-3.5 h-3.5 text-muted-foreground" />
                  </div>
                  <span className="flex-1 min-w-0 font-medium text-foreground">{allLabel}</span>
                  {!selected && <Check className="w-4 h-4 text-primary shrink-0" />}
                </button>
              )}
              {filtered.map(emp => {
                const active = matchesValue(emp, value);
                return (
                  <button
                    key={emp.id}
                    type="button"
                    onClick={() => select(emp)}
                    className={`w-full flex items-center gap-3 rounded-lg px-2.5 py-2 text-left text-sm hover:bg-muted/40 transition-colors ${active ? 'bg-sidebar-accent/40' : ''}`}
                  >
                    <div
                      className="w-7 h-7 rounded-full flex items-center justify-center text-[10px] font-bold text-primary-foreground shrink-0"
                      style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                    >
                      {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                    </div>
                    <span className="flex-1 min-w-0">
                      <span className="block font-medium text-foreground truncate">{emp.name}</span>
                      <span className="block text-xs text-muted-foreground truncate">
                        {emp.employeeId} · {emp.department || '—'}
                      </span>
                    </span>
                    {active && <Check className="w-4 h-4 text-primary shrink-0" />}
                  </button>
                );
              })}
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
