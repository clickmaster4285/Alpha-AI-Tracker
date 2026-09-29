'use client';

import { Loader2, Monitor, Search, Wifi, WifiOff, Circle } from 'lucide-react';
import type { LiveStreamEmployee } from '@/lib/api';

export function LiveStreamEmployeeSidebar({
  employees,
  selectedIds,
  maxTiles,
  searchInput,
  onSearchChange,
  isLoading,
  isError,
  onToggle,
  title = 'Live Stream',
  subtitle,
}: {
  employees: LiveStreamEmployee[];
  selectedIds: string[];
  maxTiles: number;
  searchInput: string;
  onSearchChange: (value: string) => void;
  isLoading: boolean;
  isError: boolean;
  onToggle: (employeeId: string) => void;
  title?: string;
  subtitle?: string;
}) {
  return (
    <aside className="w-80 shrink-0 bg-card rounded-xl border border-border flex flex-col overflow-hidden h-full">
      <div className="p-4 border-b border-border space-y-3">
        <h3 className="font-display font-semibold text-foreground flex items-center gap-2">
          <Monitor className="w-4 h-4 text-primary" />
          {title}
        </h3>
        <p className="text-xs text-muted-foreground">
          {subtitle ??
            `Select up to ${maxTiles} employees (${selectedIds.length}/${maxTiles})`}
        </p>
        <div className="relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
          <input
            value={searchInput}
            onChange={(e) => onSearchChange(e.target.value)}
            placeholder="Search employees…"
            className="w-full pl-9 pr-3 py-2 text-sm rounded-lg border border-border bg-background"
          />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto">
        {isLoading && (
          <div className="flex justify-center py-10">
            <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
          </div>
        )}
        {isError && (
          <p className="p-4 text-sm text-destructive">Failed to load employees.</p>
        )}
        {!isLoading && employees.length === 0 && (
          <p className="p-4 text-sm text-muted-foreground">No employees match.</p>
        )}
        <ul className="divide-y divide-border">
          {employees.map((emp) => {
            const selected = selectedIds.includes(emp.employeeId);
            const blocked = !selected && selectedIds.length >= maxTiles;
            return (
              <EmployeeRow
                key={emp.employeeId}
                emp={emp}
                selected={selected}
                disabled={blocked}
                onSelect={() => onToggle(emp.employeeId)}
              />
            );
          })}
        </ul>
      </div>
    </aside>
  );
}

function EmployeeRow({
  emp,
  selected,
  disabled,
  onSelect,
}: {
  emp: LiveStreamEmployee;
  selected: boolean;
  disabled: boolean;
  onSelect: () => void;
}) {
  return (
    <li>
      <button
        type="button"
        onClick={onSelect}
        disabled={disabled}
        className={`w-full text-left px-4 py-3 transition-colors ${
          disabled ? 'opacity-40 cursor-not-allowed' : 'hover:bg-muted/50'
        } ${
          selected ? 'bg-primary/5 border-l-2 border-l-primary' : 'border-l-2 border-l-transparent'
        }`}
      >
        <div className="flex items-center gap-2">
          {emp.online ? (
            <Wifi className="w-3.5 h-3.5 text-emerald-500 shrink-0" />
          ) : (
            <WifiOff className="w-3.5 h-3.5 text-muted-foreground shrink-0" />
          )}
          <span className="text-sm font-medium text-foreground truncate flex-1">{emp.name}</span>
          {emp.streaming && (
            <Circle className="w-2 h-2 fill-red-500 text-red-500 shrink-0" />
          )}
        </div>
        <p className="text-xs text-muted-foreground mt-0.5 pl-5 truncate">
          {emp.online ? 'Online' : 'Offline'}
          {' · '}
          {emp.wsConnected ? 'WS connected' : 'WS off'}
          {emp.clientConnected ? ' · Stream ready' : ''}
          {emp.consentMissing ? ' · Consent missing' : ''}
          {!emp.consentMissing && emp.online && !emp.streamAvailable && emp.clientConnected
            ? ' · Capture unavailable'
            : ''}
        </p>
      </button>
    </li>
  );
}
