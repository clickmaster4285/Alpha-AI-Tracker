'use client';

import { MapPin } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';

export default function EmployeeJourneyLocation() {
  return (
    <EmployeePage
      title="Location Trail"
      subtitle="Geographic positions reported by the employee's device over time."
      icon={MapPin}
    >
      {() => (
        <EmptyState
          icon={MapPin}
          text="No location data collected yet — the desktop client does not currently report device location."
        />
      )}
    </EmployeePage>
  );
}
