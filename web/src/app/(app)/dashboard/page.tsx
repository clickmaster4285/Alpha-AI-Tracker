'use client';

import { useQuery } from '@tanstack/react-query';
import { Users, Building2, MonitorSmartphone, Globe, Loader2, BarChart3 } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import Link from 'next/link';
import StatsCard from '@/components/ui/StatsCard';
import EmptyState from '@/components/employees/EmptyState';
import { employeesApi, departmentsApi, appSessionsApi, appItemsApi } from '@/lib/api';
import DownloadAppSection from '@/components/DownloadAppSection';

export default function Dashboard() {
  const queryClient = useQueryClient();

  // All dashboard figures come from live APIs — no mock data anywhere.
  const employeesQuery = useQuery({
    queryKey: ['dashboard', 'employees'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 1 }),
  });
  const trackedQuery = useQuery({
    queryKey: ['dashboard', 'employees-tracked'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 1, status: 'tracked' }),
  });
  const departmentsQuery = useQuery({
    queryKey: ['dashboard', 'departments'],
    queryFn: () => departmentsApi.list(),
  });

  const dayAgo = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
  const sessionsQuery = useQuery({
    queryKey: ['dashboard', 'sessions-24h'],
    queryFn: () => appSessionsApi.list({ page: 1, perPage: 1, dateFrom: dayAgo }),
  });
  const webPagesQuery = useQuery({
    queryKey: ['dashboard', 'webpages-24h'],
    queryFn: () => appItemsApi.list({ page: 1, perPage: 1, itemType: 'browser_tab', dateFrom: dayAgo }),
  });

  const totalEmployees = employeesQuery.data?.total ?? 0;
  const tracked = trackedQuery.data?.total ?? 0;
  const untracked = Math.max(0, totalEmployees - tracked);
  const departmentCount = departmentsQuery.data?.departments?.length ?? 0;
  const sessions24h = sessionsQuery.data?.total ?? 0;
  const webPages24h = webPagesQuery.data?.total ?? 0;

  const isLoading =
    employeesQuery.isLoading || trackedQuery.isLoading || departmentsQuery.isLoading ||
    sessionsQuery.isLoading || webPagesQuery.isLoading;

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Download banner */}
      <DownloadAppSection />

      {/* Stats — live from the server */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard
          title="Total Employees"
          value={totalEmployees}
          icon={Users}
          subtitle={`Tracked: ${tracked}  Untracked: ${untracked}`}
          delay={0.05}
        />
        <StatsCard
          title="Departments"
          value={departmentCount}
          icon={Building2}
          delay={0.1}
        />
        <StatsCard
          title="App Sessions · 24h"
          value={sessions24h}
          icon={MonitorSmartphone}
          delay={0.15}
        />
        <StatsCard
          title="Web Pages · 24h"
          value={webPages24h}
          icon={Globe}
          delay={0.2}
        />
      </div>

      {/* Productivity analytics placeholder — no aggregate endpoint exists yet */}
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-bold text-foreground mb-4 flex items-center gap-2">
          <BarChart3 className="w-4 h-4 text-primary" /> Productive / Unproductive
        </h3>
        <EmptyState
          icon={BarChart3}
          text="No productivity analytics yet — explore collected activity in Employee Journey."
        />
        <div className="flex justify-center gap-3 mt-4">
          <Link
            href="/employee-journey/timeline"
            onClick={() => queryClient.invalidateQueries({ queryKey: ['app-sessions'] })}
            className="text-sm text-primary hover:underline"
          >
            Session Timeline
          </Link>
          <span className="text-border">·</span>
          <Link href="/employee-journey/apps" className="text-sm text-primary hover:underline">
            App Usage
          </Link>
          <span className="text-border">·</span>
          <Link href="/employee-journey/web" className="text-sm text-primary hover:underline">
            Web Activity
          </Link>
        </div>
      </div>
    </div>
  );
}
