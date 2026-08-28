'use client';

import { Clock } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function HoursInsights() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">Hours &amp; Insights</h3>
        <EmptyState
          icon={Clock}
          text="Working-hours analytics are not available yet — clock-in/out is not tracked. Foreground/background time per app is visible in Employee Journey → App Usage."
        />
      </div>
    </div>
  );
}
