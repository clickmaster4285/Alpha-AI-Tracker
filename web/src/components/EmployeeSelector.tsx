'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Check, ChevronDown, Loader2, Search, User } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { employeesApi, type Employee } from '@/lib/api';

interface EmployeeSelectorProps {
  value: string;
  onChange: (employee: Employee | null) => void;
  placeholder?: string;
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
}: EmployeeSelectorProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const rootRef = useRef<HTMLDivElement | null>(null);

  const { data, isLoading } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  const employees = data?.data ?? [];
  const selected = employees.find(e => e.id === value) ?? null;

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return employees;
    return employees.filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.employeeId.toLowerCase().includes(q) ||
      e.email.toLowerCase().includes(q),
    );
  }, [employees, search]);

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

  return (
    <div ref={rootRef} className="relative w-full max-w-sm">
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        className="w-full flex items-center gap-2 bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground hover:bg-muted/40 transition-colors"
      >
        <User className="w-4 h-4 text-muted-foreground flex-shrink-0" />
        <span className="flex-1 text-left truncate">
          {selected ? (
            <>
              <span className="font-medium">{selected.name}</span>
              <span className="text-muted-foreground ml-1.5 font-mono text-xs">{selected.employeeId}</span>
            </>
          ) : (
            <span className="text-muted-foreground">{placeholder}</span>
          )}
        </span>
        <ChevronDown className={`w-4 h-4 text-muted-foreground transition-transform flex-shrink-0 ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && (
        <div className="absolute z-50 mt-1 w-full bg-card border border-border rounded-xl shadow-card-hover overflow-hidden">
          <div className="flex items-center gap-2 px-3 py-2 border-b border-border">
            <Search className="w-4 h-4 text-muted-foreground flex-shrink-0" />
            <input
              autoFocus
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Search by name, ID or email…"
              className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
            />
          </div>
          <div className="max-h-72 overflow-y-auto py-1">
            {isLoading ? (
              <div className="flex items-center justify-center gap-2 py-6 text-sm text-muted-foreground">
                <Loader2 className="w-4 h-4 animate-spin" /> Loading employees…
              </div>
            ) : filtered.length === 0 ? (
              <p className="text-center py-6 text-sm text-muted-foreground">No employees found</p>
            ) : (
              filtered.map(emp => {
                const active = emp.id === value;
                return (
                  <button
                    key={emp.id}
                    type="button"
                    onClick={() => select(emp)}
                    className={`w-full flex items-center gap-3 px-3 py-2 text-left text-sm hover:bg-muted/40 transition-colors ${active ? 'bg-sidebar-accent/40' : ''}`}
                  >
                    <div
                      className="w-7 h-7 rounded-full flex items-center justify-center text-[10px] font-bold text-primary-foreground flex-shrink-0"
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
                    {active && <Check className="w-4 h-4 text-primary flex-shrink-0" />}
                  </button>
                );
              })
            )}
          </div>
        </div>
      )}
    </div>
  );
}
