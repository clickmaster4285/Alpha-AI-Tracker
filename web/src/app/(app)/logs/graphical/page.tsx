'use client';

import { BarChart3 } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function GraphicalLogs() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">Graphical Logs</h3>
        <EmptyState
          icon={BarChart3}
          text="No aggregate activity charts yet — productivity classification has no server endpoint. Raw journeys are available in Employee Journey."
        />
      </div>
    </div>
  );
}
