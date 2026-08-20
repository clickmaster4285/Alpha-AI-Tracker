'use client';

import { useQuery } from '@tanstack/react-query';
import { employeesApi } from '@/lib/api';

/**
 * Fetches the aggregate machine picture for one employee (UUID id).
 * Disabled until an employee is selected.
 */
export function useEmployeeDetail(employeeId: string) {
  return useQuery({
    queryKey: ['employee-detail', employeeId],
    queryFn: () => employeesApi.detail(employeeId),
    enabled: !!employeeId,
  });
}
