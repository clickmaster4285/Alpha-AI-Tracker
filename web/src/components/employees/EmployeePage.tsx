'use client';

import { Loader2, UserX } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import EmployeeSelector from '@/components/EmployeeSelector';
import { useEmployeeDetail } from '@/hooks/use-employee-detail';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { employeesApi, type Employee, type EmployeeDetail } from '@/lib/api';

export interface EmployeePageContext {
  /** Full employee record (UUID id + EMP-XXXXX code). */
  employee: Employee;
  /** Aggregate machine picture — only present when fetchDetail is enabled. */
  detail?: EmployeeDetail;
  detailLoading: boolean;
}

interface EmployeePageProps {
  title: string;
  subtitle: string;
  icon: React.ElementType;
  /** Fetch the aggregate GET /employees/:id/detail payload for the selected employee. */
  fetchDetail?: boolean;
  children: (ctx: EmployeePageContext) => React.ReactNode;
}

/**
 * Shared page shell for the Device Specs and Employee Journey modules:
 * page header, employee picker (deep-linkable via ?employeeId=), and
 * loading/error/no-selection states. The body is rendered by the caller
 * once an employee (and optionally the detail payload) is available.
 *
 * URL state
 * ---------
 * The selected employee is mirrored to `?employeeId=<uuid>`. Any sibling
 * page (the employees table action menu, an external link) can deep-link
 * to a specific journey or device-specs subpage by adding that param; a
 * manual address-bar edit propagates back into the picker on the next
 * render. See the Web URL-State Rule in AGENTS.md §6.
 */
export default function EmployeePage({
  title,
  subtitle,
  icon: Icon,
  fetchDetail = false,
  children,
}: EmployeePageProps) {
  const [filters, setFilters] = useUrlQueryState(
    { employeeId: {} },
    { employeeId: '' },
  );
  const employeeId = filters.employeeId;

  // Same query key as EmployeeSelector — one shared cache entry.
  const { data: employeesData } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  const employee = employeesData?.data.find(e => e.id === employeeId) ?? null;

  const detailQuery = useEmployeeDetail(fetchDetail ? employeeId : '');

  const handleChange = (emp: Employee | null) => {
    setFilters({ employeeId: emp?.id ?? '' });
  };

  return (
    <div className="space-y-5 animate-fade-in">
      {/* Header */}
      <div className="flex flex-col lg:flex-row lg:items-end gap-4 justify-between">
        <div className="flex items-start gap-3">
          <div className="w-11 h-11 rounded-xl gradient-primary flex items-center justify-center flex-shrink-0">
            <Icon className="w-5 h-5 text-primary-foreground" />
          </div>
          <div>
            <h1 className="text-xl font-bold text-foreground font-display">{title}</h1>
            <p className="text-sm text-muted-foreground mt-0.5">{subtitle}</p>
          </div>
        </div>
        <EmployeeSelector value={employeeId} onChange={handleChange} />
      </div>

      {/* Body */}
      {!employee ? (
        <div className="bg-card rounded-xl border border-border py-16 flex flex-col items-center gap-3 text-center px-6">
          <div className="w-12 h-12 rounded-xl bg-muted flex items-center justify-center">
            <UserX className="w-6 h-6 text-muted-foreground" />
          </div>
          <p className="text-sm text-muted-foreground">
            Select an employee to view their {fetchDetail ? 'device information' : 'activity'}.
          </p>
        </div>
      ) : fetchDetail && detailQuery.isLoading ? (
        <div className="flex items-center justify-center min-h-[320px]">
          <div className="flex flex-col items-center gap-3">
            <Loader2 className="w-8 h-8 animate-spin text-primary" />
            <p className="text-sm text-muted-foreground">Loading device information…</p>
          </div>
        </div>
      ) : fetchDetail && (detailQuery.error || !detailQuery.data) ? (
        <div className="bg-card rounded-xl border border-border py-16 flex flex-col items-center gap-3 text-center px-6">
          <p className="text-sm text-destructive font-medium">Failed to load employee details</p>
          <p className="text-xs text-muted-foreground">
            {detailQuery.error ? (detailQuery.error as Error).message : 'No data found for this employee'}
          </p>
        </div>
      ) : (
        children({ employee, detail: detailQuery.data, detailLoading: detailQuery.isLoading })
      )}
    </div>
  );
}
