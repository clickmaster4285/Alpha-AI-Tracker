'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Loader2, Monitor, Search, Wifi, WifiOff, ShieldAlert, Circle } from 'lucide-react';
import { liveStreamApi, type LiveStreamEmployee } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { useLiveStreamSocket } from '@/lib/useLiveStreamSocket';

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

function LiveStreamInner() {
  const [filters, setFilters] = useUrlQueryState(
    { employeeId: {}, q: {} },
    { employeeId: '', q: '' },
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

  const { data, isLoading, isError } = useQuery({
    queryKey: ['live-stream-employees'],
    queryFn: () => liveStreamApi.employees(),
    refetchInterval: 15_000,
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

  const selected = employees.find((e) => e.employeeId === filters.employeeId) ?? null;
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const canWatch =
    !!selected &&
    selected.online &&
    !selected.consentMissing;
  const socket = useLiveStreamSocket(filters.employeeId || null, canvasRef, videoRef, {
    enabled: canWatch,
  });
  const showVideo = socket.mediaTransport === 'webrtc' && socket.status === 'live';
  const showCanvas = socket.mediaTransport === 'jpeg' && socket.status === 'live';

  return (
    <div className="flex gap-4 h-[calc(100vh-8rem)] min-h-[480px] animate-fade-in">
      {/* Left rail */}
      <aside className="w-80 shrink-0 bg-card rounded-xl border border-border flex flex-col overflow-hidden">
        <div className="p-4 border-b border-border space-y-3">
          <h3 className="font-display font-semibold text-foreground flex items-center gap-2">
            <Monitor className="w-4 h-4 text-primary" />
            Live Stream
          </h3>
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
            {employees.map((emp) => (
              <EmployeeRow
                key={emp.employeeId}
                emp={emp}
                selected={emp.employeeId === filters.employeeId}
                onSelect={() => setFilters({ employeeId: emp.employeeId })}
              />
            ))}
          </ul>
        </div>
      </aside>

      {/* Right pane */}
      <section className="flex-1 bg-card rounded-xl border border-border flex flex-col overflow-hidden min-w-0">
        <div className="px-4 py-3 border-b border-border flex items-center justify-between gap-3">
          <div className="min-w-0">
            {selected ? (
              <>
                <p className="font-medium text-foreground truncate">{selected.name}</p>
                <p className="text-xs text-muted-foreground truncate">
                  {selected.employeeId}
                  {selected.department ? ` · ${selected.department}` : ''}
                </p>
              </>
            ) : (
              <p className="text-sm text-muted-foreground">Select an employee to watch</p>
            )}
          </div>
          <div className="flex items-center gap-3 shrink-0">
            {selected && socket.clientConnected && socket.monitors.length > 0 && (
              <label className="flex items-center gap-2 text-xs text-muted-foreground">
                <span className="sr-only">Monitor</span>
                <select
                  value={socket.selectedMonitor}
                  onChange={(e) => socket.selectMonitor(Number(e.target.value))}
                  disabled={socket.monitors.length < 2}
                  className="h-8 max-w-[14rem] rounded-md border border-border bg-background px-2 text-sm text-foreground disabled:opacity-70"
                  title={
                    socket.monitors.length < 2
                      ? 'Only one display reported by this PC'
                      : 'Select display to preview'
                  }
                >
                  {socket.monitors.map((m) => (
                    <option key={m.index} value={m.index}>
                      {m.name || `Display ${m.index + 1}`}
                      {m.width && m.height ? ` (${m.width}×${m.height})` : ''}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {selected && socket.status === 'live' && (
              <div className="flex items-center gap-2 text-xs">
                <span className="inline-flex items-center gap-1.5 px-2 py-1 rounded-md bg-red-500/10 text-red-600 font-medium">
                  <Circle className="w-2 h-2 fill-current" />
                  LIVE
                </span>
                <span className="text-muted-foreground">
                  {socket.mediaTransport === 'webrtc' ? 'WebRTC' : `${socket.fps} fps`}
                </span>
              </div>
            )}
          </div>
        </div>

        <div className="flex-1 relative bg-muted/30 flex items-center justify-center overflow-hidden">
          <video
            ref={videoRef}
            autoPlay
            playsInline
            muted
            className={`max-w-full max-h-full object-contain ${showVideo ? 'block' : 'hidden'}`}
          />
          <canvas
            ref={canvasRef}
            className={`max-w-full max-h-full object-contain ${showCanvas ? 'block' : 'hidden'}`}
          />
          <WatchOverlay selected={selected} socket={socket} />
        </div>
      </section>
    </div>
  );
}

function EmployeeRow({
  emp,
  selected,
  onSelect,
}: {
  emp: LiveStreamEmployee;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <li>
      <button
        type="button"
        onClick={onSelect}
        className={`w-full text-left px-4 py-3 hover:bg-muted/50 transition-colors ${
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
          {emp.consentMissing ? ' · Consent missing' : ''}
          {!emp.consentMissing && emp.online && !emp.streamAvailable ? ' · Unavailable' : ''}
        </p>
      </button>
    </li>
  );
}

function WatchOverlay({
  selected,
  socket,
}: {
  selected: LiveStreamEmployee | null;
  socket: ReturnType<typeof useLiveStreamSocket>;
}) {
  if (!selected) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Select an employee"
        body="Choose someone from the list to open a live preview of their screen."
      />
    );
  }
  if (selected.consentMissing || socket.status === 'consentMissing') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Consent required"
        body="This employee has not accepted Live Screen Viewing terms. Streaming is blocked until they accept on the desktop client."
      />
    );
  }
  if (!selected.online) {
    return (
      <EmptyPane
        icon={WifiOff}
        title="Employee offline"
        body="No recent heartbeat from this machine. Live preview is unavailable while offline."
      />
    );
  }
  if (
    socket.status === 'unavailable' ||
    (selected.clientConnected && !selected.streamAvailable && socket.status !== 'live')
  ) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Stream unavailable"
        body="This client is online but screen capture is not available on its OS (Windows-only in Phase 1)."
      />
    );
  }
  if (socket.status === 'error') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Connection error"
        body={socket.error || 'Could not open the watch socket. Check NEXT_PUBLIC_WS_URL and CORS.'}
      />
    );
  }
  if (socket.status === 'live') return null;
  // Online + consent OK, but desktop push socket has not connected / hello'd yet.
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-muted-foreground px-8 text-center">
      <Loader2 className="w-6 h-6 animate-spin" />
      <p className="text-sm font-medium text-foreground">Waiting for desktop client…</p>
      <p className="text-xs max-w-md">
        Keep the tracker running with <code className="text-foreground">ALPHA_STREAM_ENABLED=true</code>.
        Server logs should show <code className="text-foreground">push connected</code> then{' '}
        <code className="text-foreground">hello</code>.
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
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-8 text-center">
      <Icon className="w-10 h-10 text-muted-foreground/60" />
      <p className="font-medium text-foreground">{title}</p>
      <p className="text-sm text-muted-foreground max-w-md">{body}</p>
    </div>
  );
}
