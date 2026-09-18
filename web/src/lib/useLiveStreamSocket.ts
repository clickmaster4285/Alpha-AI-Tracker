'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { liveStreamApi } from '@/lib/api';

export type LiveStreamSocketStatus =
  | 'idle'
  | 'connecting'
  | 'live'
  | 'offline'
  | 'consentMissing'
  | 'unavailable'
  | 'error';

export interface LiveStreamMonitor {
  index: number;
  name: string;
  width: number;
  height: number;
  isPrimary: boolean;
}

export interface LiveStreamSocketState {
  status: LiveStreamSocketStatus;
  streaming: boolean;
  streamAvailable: boolean;
  clientConnected: boolean;
  consentMissing: boolean;
  fps: number;
  error: string | null;
  monitors: LiveStreamMonitor[];
  selectedMonitor: number;
  selectMonitor: (index: number) => void;
}

function resolveWsBase(): string {
  const fromEnv = process.env.NEXT_PUBLIC_WS_URL?.trim();
  if (fromEnv) return fromEnv.replace(/\/$/, '');
  if (typeof window !== 'undefined') {
    const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    if (window.location.port === '3000') {
      return `${proto}//${window.location.hostname}:8080`;
    }
    return `${proto}//${window.location.host}`;
  }
  return 'ws://localhost:8080';
}

/**
 * Opens the watch WebSocket after minting a short-lived ticket over REST.
 * Cookies work on REST; browsers often omit them on cross-port WS, so we never
 * rely on the auth cookie for the upgrade itself.
 */
export function useLiveStreamSocket(
  employeeId: string | null,
  canvasRef: React.RefObject<HTMLCanvasElement | null>,
  opts?: { enabled?: boolean },
): LiveStreamSocketState {
  const enabled = opts?.enabled !== false;
  const [state, setState] = useState<Omit<LiveStreamSocketState, 'selectMonitor'>>({
    status: 'idle',
    streaming: false,
    streamAvailable: false,
    clientConnected: false,
    consentMissing: false,
    fps: 0,
    error: null,
    monitors: [],
    selectedMonitor: 0,
  });

  const frameTimes = useRef<number[]>([]);
  const wsRef = useRef<WebSocket | null>(null);
  const canvasHolder = useRef(canvasRef);
  canvasHolder.current = canvasRef;

  const selectMonitor = useCallback((index: number) => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ type: 'select_monitor', index }));
    setState((s) => ({ ...s, selectedMonitor: index }));
  }, []);

  useEffect(() => {
    if (!employeeId || !enabled) {
      setState({
        status: 'idle',
        streaming: false,
        streamAvailable: false,
        clientConnected: false,
        consentMissing: false,
        fps: 0,
        error: null,
        monitors: [],
        selectedMonitor: 0,
      });
      return;
    }

    let cancelled = false;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let attempt = 0;

    const connect = async () => {
      if (cancelled) return;
      setState((s) => ({ ...s, status: 'connecting', error: null }));

      let ticket: string;
      try {
        const res = await liveStreamApi.watchTicket(employeeId);
        ticket = res.ticket;
      } catch (err) {
        if (cancelled) return;
        const msg = err instanceof Error ? err.message : 'ticket_failed';
        const consent = /consent/i.test(msg);
        setState((s) => ({
          ...s,
          status: consent ? 'consentMissing' : 'error',
          consentMissing: consent,
          error: msg,
          streaming: false,
        }));
        if (!consent) {
          attempt += 1;
          const delay = Math.min(10_000, 1000 * Math.pow(2, Math.min(attempt, 4)));
          reconnectTimer = setTimeout(() => {
            void connect();
          }, delay);
        }
        return;
      }

      if (cancelled) return;

      const url =
        `${resolveWsBase()}/api/v1/live-stream/watch` +
        `?employeeId=${encodeURIComponent(employeeId)}` +
        `&ticket=${encodeURIComponent(ticket)}`;
      const ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      wsRef.current = ws;

      ws.onopen = () => {
        attempt = 0;
        if (!cancelled) {
          setState((s) => ({ ...s, status: 'connecting', error: null }));
        }
      };

      ws.onmessage = (ev) => {
        if (cancelled) return;
        if (typeof ev.data === 'string') {
          try {
            const msg = JSON.parse(ev.data) as {
              type?: string;
              streaming?: boolean;
              streamAvailable?: boolean;
              clientConnected?: boolean;
              consentMissing?: boolean;
              code?: string;
              monitors?: LiveStreamMonitor[];
              selectedMonitor?: number;
            };
            if (msg.type === 'status') {
              setState((s) => ({
                ...s,
                streaming: !!msg.streaming,
                streamAvailable: !!msg.streamAvailable,
                clientConnected: !!msg.clientConnected,
                consentMissing: !!msg.consentMissing,
                monitors: Array.isArray(msg.monitors) ? msg.monitors : s.monitors,
                selectedMonitor:
                  typeof msg.selectedMonitor === 'number' ? msg.selectedMonitor : s.selectedMonitor,
                status: msg.consentMissing
                  ? 'consentMissing'
                  : msg.streaming
                    ? 'live'
                    : !msg.clientConnected
                      ? 'connecting'
                      : msg.streamAvailable === false
                        ? 'unavailable'
                        : 'connecting',
              }));
            } else if (msg.type === 'error') {
              const code = msg.code || 'error';
              setState((s) => ({
                ...s,
                status: code.includes('consent') ? 'consentMissing' : 'error',
                error: code,
                consentMissing: code.includes('consent'),
              }));
            }
          } catch {
            /* ignore */
          }
          return;
        }

        const buf = ev.data as ArrayBuffer;
        const blob = new Blob([buf], { type: 'image/jpeg' });
        const objUrl = URL.createObjectURL(blob);
        const img = new Image();
        img.onload = () => {
          URL.revokeObjectURL(objUrl);
          const canvas = canvasHolder.current.current;
          if (!canvas || cancelled) return;
          if (canvas.width !== img.naturalWidth || canvas.height !== img.naturalHeight) {
            canvas.width = img.naturalWidth;
            canvas.height = img.naturalHeight;
          }
          const ctx = canvas.getContext('2d');
          if (!ctx) return;
          ctx.drawImage(img, 0, 0);
          const now = performance.now();
          frameTimes.current.push(now);
          frameTimes.current = frameTimes.current.filter((t) => now - t < 1000);
          setState((s) => ({
            ...s,
            status: 'live',
            streaming: true,
            fps: frameTimes.current.length,
          }));
        };
        img.onerror = () => URL.revokeObjectURL(objUrl);
        img.src = objUrl;
      };

      ws.onerror = () => {
        if (!cancelled) {
          setState((s) => ({ ...s, status: 'error', error: 'WebSocket error' }));
        }
      };

      ws.onclose = () => {
        wsRef.current = null;
        if (cancelled) return;
        attempt += 1;
        const delay = Math.min(10_000, 1000 * Math.pow(2, Math.min(attempt, 4)));
        setState((s) => ({
          ...s,
          streaming: false,
          status: s.status === 'consentMissing' ? 'consentMissing' : 'connecting',
        }));
        reconnectTimer = setTimeout(() => {
          void connect();
        }, delay);
      };
    };

    void connect();

    return () => {
      cancelled = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      frameTimes.current = [];
    };
  }, [employeeId, enabled]);

  return { ...state, selectMonitor };
}
