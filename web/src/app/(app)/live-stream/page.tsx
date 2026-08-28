'use client';

import { Monitor } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function LiveStream() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">Live Stream</h3>
        <EmptyState
          icon={Monitor}
          text="Live screen streaming is not available yet — no streaming backend exists. This page will activate once screen broadcast ships."
        />
      </div>
    </div>
  );
}
