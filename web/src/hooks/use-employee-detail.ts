'use client';

import { useQuery } from '@tanstack/react-query';
import { employeesApi } from '@/lib/api';

/**
 * Fetches the aggregate machine picture for one employee (UUID id).
 * Disabled until an employee is selected. `deviceId` (a machine id) scopes every
 * device-level surface to that machine; empty means the employee-wide aggregate.
 */
export function useEmployeeDetail(employeeId: string, deviceId = '') {
  return useQuery({
    queryKey: ['employee-detail', employeeId, deviceId],
    queryFn: () => employeesApi.detail(employeeId, deviceId || undefined),
    enabled: !!employeeId,
  });
}
