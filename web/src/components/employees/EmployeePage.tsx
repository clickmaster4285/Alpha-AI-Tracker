'use client';

import { Loader2, UserX } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import EmployeeSelector from '@/components/EmployeeSelector';
import { useEmployeeDetail } from '@/hooks/use-employee-detail';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import { employeesApi, type Employee, type EmployeeDetail } from '@/lib/api';

export interface EmployeePageContext {
  /** Full employee record (UUID id + EMP-XXXXX code). */
  employee: Employee;
  /** Aggregate machine picture — only present when fetchDetail is enabled. */
  detail?: EmployeeDetail;
  detailLoading: boolean;
  /**
   * URL setter for the `?employeeId=` key. Bodies normally just read
   * `employee.employeeId` and leave the picker as the sole writer, but
   * this is exposed for bodies that need to round-trip the employee
   * (e.g. a "deep link to a different employee" button).
   */
  setEmployeeId?: (employeeId: string) => void;
  /**
   * URL-synced activity filter (search + date preset + dateFrom/dateTo).
   * The shell owns the underlying `useUrlActivityFilter` and SHARES its
   * state with the body via this render-prop — there is exactly one
   * `useUrlQueryState` instance per page, so writes from the picker
   * (changing `employeeId`) and writes from the body's filter chips
   * (changing `q/preset/from/to`) never race, never erase each other,
   * never produce a "previous filter got reset" surprise. Bodies that
   * also have their own URL state should merge their keys into the
   * same extra schema (see the shell call site for the canonical
   * `{employeeId}` extra).
   */
  filter: import('@/components/journey/ActivityFilters').ActivityFilter;
  setFilter: (next: import('@/components/journey/ActivityFilters').ActivityFilter) => void;
}

interface EmployeePageProps {
  title: string;
  subtitle: string;
  icon: React.ElementType;
  /** Fetch the aggregate GET /employees/:id/detail payload for the selected employee. */
  fetchDetail?: boolean;
  /**
   * Render-prop that builds the page body once an employee is selected.
   * Receives the resolved employee + the shell's filter state + an
   * optional `setEmployeeId` callback for bodies that want to share
   * this shell's URL state.
   */
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
 * The shell owns the SINGLE `useUrlActivityFilter` instance for the
 * page, exposing BOTH the activity filter (search + date) and the
 * `?employeeId=<uuid>` picker key through the same hook — one
 * underlying `useUrlQueryState`, one `lastSerialized` ref, one
 * `latestWrittenSearch` chain slot. There is no second
 * `useUrlQueryState` instance to race against, so picking an employee
 * and then clicking a date preset (or vice versa) never resets the
 * sibling key.
 *
 * Any sibling page (the employees table action menu, an external link)
 * can deep-link to a specific journey or device-specs subpage by adding
 * `?employeeId=<uuid>`; a manual address-bar edit propagates back into
 * the picker on the next render. See the Web URL-State Rule in
 * AGENTS.md §6.
 */
export default function EmployeePage({
  title,
  subtitle,
  icon: Icon,
  fetchDetail = false,
  children,
}: EmployeePageProps) {
  // The single URL hook for the whole page — owns `employeeId` plus the
  // four activity-filter keys (`q/preset/from/to`). Bodies receive
  // `filter`/`setFilter` from the render-prop and use them directly
  // (instead of calling `useUrlActivityFilter` themselves). This is
  // the structural fix for the "pick employee then change filter wipes
  // the picker" bug — there is now exactly one `useUrlQueryState` on
  // the page and no race window between two instances.
  const { filter, setFilter, extra: urlExtra, setExtra } = useUrlActivityFilter(
    { employeeId: {} },
    { employeeId: '' },
  );
  const employeeId = urlExtra.employeeId;

  // Same query key as EmployeeSelector — one shared cache entry.
  const { data: employeesData } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  const employee = employeesData?.data.find(e => e.id === employeeId) ?? null;

  const detailQuery = useEmployeeDetail(fetchDetail ? employeeId : '');

  const handleChange = (emp: Employee | null) => {
    setExtra({ employeeId: emp?.id ?? '' });
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
        children({
          employee,
          detail: detailQuery.data,
          detailLoading: detailQuery.isLoading,
          setEmployeeId: (id: string) => setExtra({ employeeId: id }),
          filter,
          setFilter,
        })
      )}
    </div>
  );
}