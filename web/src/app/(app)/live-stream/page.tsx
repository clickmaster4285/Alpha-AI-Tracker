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
          text="Live screen media is not available yet. The secure server-side viewing session foundation is now in place; WebRTC capture and SFU playback will be enabled only after the employee consent and platform-capture pilot is complete."
        />
      </div>
    </div>
  );
}
