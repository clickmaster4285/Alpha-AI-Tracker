'use client';

/**
 * Shared selection helpers for live-stream pages (main + theater popout).
 */

export const LIVE_STREAM_MAX_TILES = 4;

export function parseLiveStreamIds(raw: string, max = LIVE_STREAM_MAX_TILES): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .slice(0, max);
}

export function toggleLiveStreamId(
  selectedIds: string[],
  employeeId: string,
  max = LIVE_STREAM_MAX_TILES,
): string[] {
  const set = new Set(selectedIds);
  if (set.has(employeeId)) {
    set.delete(employeeId);
  } else {
    if (set.size >= max) return selectedIds;
    set.add(employeeId);
  }
  return [...set];
}
