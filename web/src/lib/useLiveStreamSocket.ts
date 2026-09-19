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
  mediaMode: 'jpeg' | 'webrtc' | 'both';
  mediaTransport: 'none' | 'jpeg' | 'webrtc';
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

type IcePayload = {
  candidate?: string;
  sdpMid?: string | null;
  sdpMLineIndex?: number | null;
  usernameFragment?: string | null;
};

/**
 * Opens the watch WebSocket after minting a short-lived ticket over REST.
 * Supports Phase-1 JPEG binary frames and Phase-2 WebRTC signaling (offer/answer/ice).
 */
export function useLiveStreamSocket(
  employeeId: string | null,
  canvasRef: React.RefObject<HTMLCanvasElement | null>,
  videoRef: React.RefObject<HTMLVideoElement | null>,
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
    mediaMode: 'jpeg',
    mediaTransport: 'none',
  });

  const frameTimes = useRef<number[]>([]);
  const wsRef = useRef<WebSocket | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const canvasHolder = useRef(canvasRef);
  const videoHolder = useRef(videoRef);
  canvasHolder.current = canvasRef;
  videoHolder.current = videoRef;

  const teardownPc = useCallback(() => {
    const pc = pcRef.current;
    pcRef.current = null;
    if (pc) {
      try {
        pc.close();
      } catch {
        /* ignore */
      }
    }
    const video = videoHolder.current.current;
    if (video) {
      video.srcObject = null;
    }
  }, []);

  const selectMonitor = useCallback((index: number) => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ type: 'select_monitor', index }));
    setState((s) => ({ ...s, selectedMonitor: index }));
  }, []);

  useEffect(() => {
    if (!employeeId || !enabled) {
      teardownPc();
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
        mediaMode: 'jpeg',
        mediaTransport: 'none',
      });
      return;
    }

    let cancelled = false;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let attempt = 0;

    const ensurePc = () => {
      if (pcRef.current) return pcRef.current;
      const pc = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
      });
      pc.onicecandidate = (ev) => {
        const ws = wsRef.current;
        if (!ws || ws.readyState !== WebSocket.OPEN || !ev.candidate) return;
        const c = ev.candidate;
        ws.send(
          JSON.stringify({
            type: 'ice',
            role: 'subscriber',
            candidate: {
              candidate: c.candidate,
              sdpMid: c.sdpMid,
              sdpMLineIndex: c.sdpMLineIndex,
              usernameFragment: c.usernameFragment,
            } satisfies IcePayload,
          }),
        );
      };
      pc.ontrack = (ev) => {
        const video = videoHolder.current.current;
        if (video && ev.streams[0]) {
          video.srcObject = ev.streams[0];
          void video.play().catch(() => undefined);
        }
        setState((s) => ({
          ...s,
          status: 'live',
          streaming: true,
          mediaTransport: 'webrtc',
        }));
      };
      pcRef.current = pc;
      return pc;
    };

    const handleSignal = async (msg: {
      type?: string;
      sdp?: string;
      role?: string;
      candidate?: IcePayload;
    }) => {
      if (msg.type === 'offer' && msg.sdp) {
        const pc = ensurePc();
        await pc.setRemoteDescription({ type: 'offer', sdp: msg.sdp });
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        const ws = wsRef.current;
        if (ws && ws.readyState === WebSocket.OPEN && pc.localDescription) {
          ws.send(
            JSON.stringify({
              type: 'answer',
              role: 'subscriber',
              sdp: pc.localDescription.sdp,
            }),
          );
        }
        return;
      }
      if (msg.type === 'ice' && msg.candidate?.candidate) {
        const pc = pcRef.current ?? ensurePc();
        try {
          await pc.addIceCandidate({
            candidate: msg.candidate.candidate,
            sdpMid: msg.candidate.sdpMid ?? undefined,
            sdpMLineIndex: msg.candidate.sdpMLineIndex ?? undefined,
            usernameFragment: msg.candidate.usernameFragment ?? undefined,
          });
        } catch {
          /* ignore late candidates */
        }
      }
    };

    const connect = async () => {
      if (cancelled) return;
      teardownPc();
      setState((s) => ({ ...s, status: 'connecting', error: null, mediaTransport: 'none' }));

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
              mediaMode?: 'jpeg' | 'webrtc' | 'both';
              sdp?: string;
              role?: string;
              candidate?: IcePayload;
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
                mediaMode: msg.mediaMode ?? s.mediaMode,
                status: msg.consentMissing
                  ? 'consentMissing'
                  : msg.streaming || s.mediaTransport === 'webrtc'
                    ? 'live'
                    : !msg.clientConnected
                      ? 'connecting'
                      : msg.streamAvailable === false
                        ? 'unavailable'
                        : 'connecting',
              }));
            } else if (msg.type === 'offer' || msg.type === 'ice' || msg.type === 'answer') {
              void handleSignal(msg);
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
            mediaTransport: s.mediaTransport === 'webrtc' ? 'webrtc' : 'jpeg',
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
        teardownPc();
        if (cancelled) return;
        attempt += 1;
        const delay = Math.min(10_000, 1000 * Math.pow(2, Math.min(attempt, 4)));
        setState((s) => ({
          ...s,
          streaming: false,
          mediaTransport: 'none',
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
      teardownPc();
      frameTimes.current = [];
    };
  }, [employeeId, enabled, teardownPc]);

  return { ...state, selectMonitor };
}
