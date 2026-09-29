'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Loader2, Monitor, Search, Wifi, WifiOff, ShieldAlert, Circle, X } from 'lucide-react';
import { liveStreamApi, type LiveStreamEmployee } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { useLiveStreamSocket } from '@/lib/useLiveStreamSocket';

const MAX_TILES = 4;

export default function LiveStreamPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <LiveStreamInner />
    </Suspense>
  );
}

function parseIds(raw: string): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .slice(0, MAX_TILES);
}

function LiveStreamInner() {
  const [filters, setFilters] = useUrlQueryState(
    { ids: {}, q: {} },
    { ids: '', q: '' },
    { debounceMs: 0, history: 'replace' },
  );
  const [searchInput, setSearchInput] = useState(filters.q);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.q) setFilters({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.q, setFilters]);
  useEffect(() => {
    setSearchInput(filters.q);
  }, [filters.q]);

  const selectedIds = useMemo(() => parseIds(filters.ids), [filters.ids]);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['live-stream-employees'],
    queryFn: () => liveStreamApi.employees(),
    // Presence WS flips instantly server-side — poll fast so Online/Offline
    // and "WS connected" update within ~1s of client start/stop.
    refetchInterval: 1_000,
    staleTime: 0,
  });

  const employees = useMemo(() => {
    const list = data?.data ?? [];
    const q = filters.q.trim().toLowerCase();
    const filtered = q
      ? list.filter(
          (e) =>
            e.name.toLowerCase().includes(q) ||
            e.employeeId.toLowerCase().includes(q) ||
            (e.department || '').toLowerCase().includes(q),
        )
      : list;
    return [...filtered].sort((a, b) => {
      if (a.online !== b.online) return a.online ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [data?.data, filters.q]);

  const byId = useMemo(() => {
    const m = new Map<string, LiveStreamEmployee>();
    for (const e of data?.data ?? []) m.set(e.employeeId, e);
    return m;
  }, [data?.data]);

  const toggleEmployee = (employeeId: string) => {
    const set = new Set(selectedIds);
    if (set.has(employeeId)) {
      set.delete(employeeId);
    } else {
      if (set.size >= MAX_TILES) return;
      set.add(employeeId);
    }
    setFilters({ ids: [...set].join(',') });
  };

  const removeEmployee = (employeeId: string) => {
    setFilters({ ids: selectedIds.filter((id) => id !== employeeId).join(',') });
  };

  const tileCount = selectedIds.length;
  const gridClass =
    tileCount <= 1
      ? 'grid-cols-1 grid-rows-1'
      : tileCount === 2
        ? 'grid-cols-2 grid-rows-1'
        : 'grid-cols-2 grid-rows-2';

  return (
    <div className="flex gap-4 h-[calc(100vh-8rem)] min-h-[480px] animate-fade-in">
      <aside className="w-80 shrink-0 bg-card rounded-xl border border-border flex flex-col overflow-hidden">
        <div className="p-4 border-b border-border space-y-3">
          <h3 className="font-display font-semibold text-foreground flex items-center gap-2">
            <Monitor className="w-4 h-4 text-primary" />
            Live Stream
          </h3>
          <p className="text-xs text-muted-foreground">
            Select up to {MAX_TILES} employees ({selectedIds.length}/{MAX_TILES})
          </p>
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
            <input
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
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
              const blocked = !selected && selectedIds.length >= MAX_TILES;
              return (
                <EmployeeRow
                  key={emp.employeeId}
                  emp={emp}
                  selected={selected}
                  disabled={blocked}
                  onSelect={() => toggleEmployee(emp.employeeId)}
                />
              );
            })}
          </ul>
        </div>
      </aside>

      <section className="flex-1 bg-card rounded-xl border border-border flex flex-col overflow-hidden min-w-0">
        <div className="px-4 py-3 border-b border-border flex items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground">
            {tileCount === 0
              ? 'Select employees to open live WebRTC previews'
              : `Watching ${tileCount} employee${tileCount === 1 ? '' : 's'}`}
          </p>
        </div>

        <div className={`flex-1 relative bg-muted/30 overflow-hidden grid gap-2 p-2 ${gridClass}`}>
          {tileCount === 0 && (
            <EmptyPane
              icon={Monitor}
              title="Select employees"
              body={`Choose up to ${MAX_TILES} people from the list. Multiple admins can watch the same employee — each PC encodes once.`}
            />
          )}
          {selectedIds.map((id) => (
            <WatchTile
              key={id}
              emp={byId.get(id) ?? null}
              employeeId={id}
              onClose={() => removeEmployee(id)}
            />
          ))}
        </div>
      </section>
    </div>
  );
}

function WatchTile({
  emp,
  employeeId,
  onClose,
}: {
  emp: LiveStreamEmployee | null;
  employeeId: string;
  onClose: () => void;
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  // Keep watch open as soon as we have an id; don't tear down while the
  // employees list is still loading (that caused reconnect storms).
  const canWatch = !emp?.consentMissing;
  const socket = useLiveStreamSocket(employeeId, videoRef, { enabled: canWatch });

  return (
    <div className="relative min-h-0 rounded-lg border border-border bg-black/80 overflow-hidden flex flex-col">
      <div className="absolute top-2 left-2 right-2 z-10 flex items-start justify-between gap-2 pointer-events-none">
        <div className="min-w-0 rounded-md bg-black/60 px-2 py-1 pointer-events-auto">
          <p className="text-xs font-medium text-white truncate">
            {emp?.name ?? employeeId}
          </p>
          <p className="text-[10px] text-white/70 truncate">{employeeId}</p>
        </div>
        <div className="flex items-center gap-1 pointer-events-auto">
          {socket.status === 'live' && (
            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-red-500/90 text-white text-[10px] font-medium">
              <Circle className="w-1.5 h-1.5 fill-current" />
              LIVE
            </span>
          )}
          {emp && socket.clientConnected && socket.monitors.length > 1 && (
            <select
              value={socket.selectedMonitor}
              onChange={(e) => socket.selectMonitor(Number(e.target.value))}
              className="h-6 max-w-[9rem] rounded border border-white/20 bg-black/70 px-1 text-[10px] text-white"
            >
              {socket.monitors.map((m) => (
                <option key={m.index} value={m.index}>
                  {m.name || `Display ${m.index + 1}`}
                </option>
              ))}
            </select>
          )}
          <button
            type="button"
            onClick={onClose}
            className="h-6 w-6 inline-flex items-center justify-center rounded bg-black/60 text-white hover:bg-black/80"
            title="Remove"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      </div>

      <div className="flex-1 relative flex items-center justify-center">
        <video
          ref={videoRef}
          autoPlay
          playsInline
          muted
          className={`max-w-full max-h-full object-contain ${
            socket.status === 'live' ? 'block' : 'hidden'
          }`}
        />
        <WatchOverlay emp={emp} socket={socket} />
      </div>
    </div>
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

function WatchOverlay({
  emp,
  socket,
}: {
  emp: LiveStreamEmployee | null;
  socket: ReturnType<typeof useLiveStreamSocket>;
}) {
  if (!emp) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Unknown employee"
        body="This employee is no longer in the list."
      />
    );
  }
  if (emp.consentMissing) {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Consent required"
        body="This employee has not accepted Live Screen Viewing terms."
      />
    );
  }
  if (socket.status === 'consentMissing') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Consent required"
        body="This employee has not accepted Live Screen Viewing terms."
      />
    );
  }
  if (
    socket.status === 'unavailable' ||
    (socket.clientConnected && !socket.streamAvailable && socket.status !== 'live')
  ) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Stream unavailable"
        body="Screen capture is not available on this OS (Windows-only for WebRTC publish)."
      />
    );
  }
  if (socket.status === 'error') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Connection error"
        body={socket.error || 'Could not open the WebRTC watch session.'}
      />
    );
  }
  if (socket.status === 'live') return null;
  if (!emp.online && !socket.clientConnected) {
    return (
      <EmptyPane
        icon={WifiOff}
        title="Employee offline"
        body="WS connection is down. Preview starts automatically when the desktop client reconnects."
      />
    );
  }
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-muted-foreground px-4 text-center">
      <Loader2 className="w-5 h-5 animate-spin" />
      <p className="text-xs font-medium text-foreground">
        {socket.clientConnected
          ? 'Starting WebRTC stream…'
          : emp.wsConnected
            ? 'Waiting for stream client…'
            : 'Waiting for desktop client…'}
      </p>
    </div>
  );
}

function EmptyPane({
  icon: Icon,
  title,
  body,
}: {
  icon: typeof Monitor;
  title: string;
  body: string;
}) {
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center">
      <Icon className="w-8 h-8 text-muted-foreground/60" />
      <p className="text-sm font-medium text-foreground">{title}</p>
      <p className="text-xs text-muted-foreground max-w-sm">{body}</p>
    </div>
  );
}
