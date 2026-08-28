'use client';

import { Lightbulb } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function UserInsights() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">Log Insights</h3>
        <EmptyState
          icon={Lightbulb}
          text="AI insights are not generated yet — there is no analysis endpoint. Collected journeys are available in Employee Journey."
        />
      </div>
    </div>
  );
}
