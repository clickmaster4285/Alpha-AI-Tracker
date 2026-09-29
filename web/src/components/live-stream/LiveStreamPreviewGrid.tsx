'use client';

import { Monitor } from 'lucide-react';
import type { LiveStreamEmployee } from '@/lib/api';
import { LiveStreamWatchTile, LiveStreamEmptyPane } from './LiveStreamWatchTile';

export function liveStreamGridClass(tileCount: number): string {
  if (tileCount <= 1) return 'grid-cols-1 grid-rows-1';
  if (tileCount === 2) return 'grid-cols-2 grid-rows-1';
  return 'grid-cols-2 grid-rows-2';
}

export function LiveStreamPreviewGrid({
  selectedIds,
  byId,
  maxTiles,
  onRemove,
  emptyTitle = 'Select employees',
  emptyBody,
}: {
  selectedIds: string[];
  byId: Map<string, LiveStreamEmployee>;
  maxTiles: number;
  onRemove: (employeeId: string) => void;
  emptyTitle?: string;
  emptyBody?: string;
}) {
  const tileCount = selectedIds.length;
  const gridClass = liveStreamGridClass(tileCount);

  return (
    <div className={`flex-1 relative bg-muted/30 overflow-hidden grid gap-2 p-2 ${gridClass}`}>
      {tileCount === 0 && (
        <LiveStreamEmptyPane
          icon={Monitor}
          title={emptyTitle}
          body={
            emptyBody ??
            `Choose up to ${maxTiles} people from the list. Multiple admins can watch the same employee — each PC encodes once.`
          }
        />
      )}
      {selectedIds.map((id) => (
        <LiveStreamWatchTile
          key={id}
          emp={byId.get(id) ?? null}
          employeeId={id}
          onClose={() => onRemove(id)}
        />
      ))}
    </div>
  );
}
