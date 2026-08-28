'use client';

import { Activity } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function UserActivity() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">User Activity Status</h3>
        <EmptyState
          icon={Activity}
          text="Clock-in/clock-out is not tracked yet, so there is no activity status to show. Real per-app usage lives in Employee Journey → App Usage."
        />
      </div>
    </div>
  );
}
