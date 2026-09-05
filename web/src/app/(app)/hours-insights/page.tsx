'use client';

import { Fragment, Suspense, useEffect, useMemo, useState } from 'react';
import {
  AppWindow, Globe, Timer, Activity, Loader2, ChevronRight, ChevronDown,
  Monitor,
} from 'lucide-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import {
  AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend,
} from 'recharts';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { hoursInsightsApi, type HoursInsightsResponse } from '@/lib/api';
import { formatSeconds } from '@/lib/format';
import { ChartContainer, ChartTooltipContent, ChartLegendContent } from '@/components/ui/chart';

const PRODUCTIVE_COLOR = '#10b981';
const UNPRODUCTIVE_COLOR = '#ef4444';
const NEUTRAL_COLOR = '#6b7280';

const chartConfig = {
  productive: { label: 'Productive', color: PRODUCTIVE_COLOR },
  unproductive: { label: 'Unproductive', color: UNPRODUCTIVE_COLOR },
  neutral: { label: 'Neutral', color: NEUTRAL_COLOR },
};

export default function HoursInsightsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Hours Insights"
        subtitle="Track how employees spend time across apps and websites."
        icon={Clock}
      >
        {({ employee, filter, setFilter }) => (
          <HoursInsightsBody employeeId={employee.employeeId} filter={filter} setFilter={setFilter} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function HoursInsightsBody({
  employeeId,
  filter,
  setFilter,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
}) {
  const [isFiltering, setIsFiltering] = useState(false);

  const query = useQuery({
    queryKey: ['hours-insights', { employeeId, ...filter }],
    queryFn: () => hoursInsightsApi.get({
      employeeId,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
      preset: filter.preset,
    }),
    placeholderData: keepPreviousData,
  });

  useEffect(() => { setIsFiltering(query.isFetching); }, [query.isFetching]);

  const data = query.data;
  const summary = data?.summary;
  const chart = data?.chart ?? [];
  const topItems = data?.topItems ?? [];

  const totalDuration = summary?.totalSeconds ?? 0;
  const appCount = summary?.appCount ?? 0;
  const siteCount = summary?.siteCount ?? 0;
  const focusScore = summary?.focusScore ?? 0;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  return (
    <div className="space-y-4">
      <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} />

      {query.isLoading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load hours insights</p>
          <p className="text-xs text-muted-foreground">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : !data || totalDuration === 0 ? (
        <EmptyState
          icon={Monitor}
          text={filtered ? 'No usage matches the current filters' : 'No usage data synced yet'}
        />
      ) : (
        <>
          {/* Stat tiles */}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <UsageTile icon={Timer} label="Total Time" value={formatSeconds(totalDuration)} accent="bg-primary/10 text-primary" />
            <UsageTile icon={AppWindow} label="Apps Used" value={appCount} accent="bg-info/15 text-info" />
            <UsageTile icon={Globe} label="Websites Visited" value={siteCount} accent="bg-warning/15 text-warning" />
            <UsageTile icon={Activity} label="Focus Score" value={`${focusScore.toFixed(1)}%`} accent="bg-success/15 text-success" />
          </div>

          {/* Stacked area chart */}
          <div className="bg-card rounded-xl border border-border shadow-card p-4">
            <h4 className="text-sm font-semibold text-foreground mb-3">Time Distribution</h4>
            <ChartContainer config={chartConfig} className="aspect-[2/1] w-full">
              <ResponsiveContainer width="100%" height="100%">
                <AreaChart data={chart} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" />
                  <XAxis
                    dataKey="bucket"
                    stroke="var(--color-muted-foreground)"
                    fontSize={12}
                    tickLine={false}
                    axisLine={false}
                  />
                  <YAxis
                    stroke="var(--color-muted-foreground)"
                    fontSize={12}
                    tickLine={false}
                    axisLine={false}
                    tickFormatter={(v: number) => formatSeconds(v)}
                    width={60}
                  />
                  <Tooltip
                    content={
                      <ChartTooltipContent
                        formatter={(value: number, name: string) => [
                          formatSeconds(value),
                          chartConfig[name as keyof typeof chartConfig]?.label || name,
                        ]}
                      />
                    }
                  />
                  <Legend content={<ChartLegendContent />} />
                  <Area
                    type="monotone"
                    dataKey="productive"
                    stackId="1"
                    stroke={PRODUCTIVE_COLOR}
                    fill={PRODUCTIVE_COLOR}
                  />
                  <Area
                    type="monotone"
                    dataKey="unproductive"
                    stackId="1"
                    stroke={UNPRODUCTIVE_COLOR}
                    fill={UNPRODUCTIVE_COLOR}
                  />
                  <Area
                    type="monotone"
                    dataKey="neutral"
                    stackId="1"
                    stroke={NEUTRAL_COLOR}
                    fill={NEUTRAL_COLOR}
                  />
                </AreaChart>
              </ResponsiveContainer>
            </ChartContainer>
          </div>

          {/* Top items table */}
          <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
            <table className="w-full min-w-[640px]">
              <thead>
                <tr className="border-b border-border">
                  {['App / Site', 'Type', 'Category', 'Duration', 'Focus %'].map(h => (
                    <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {topItems.map((item, idx) => (
                  <tr key={`${item.kind}-${item.name}-${idx}`} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                          {item.kind === 'app'
                            ? <AppWindow className="w-4 h-4 text-primary" />
                            : <Globe className="w-4 h-4 text-primary" />
                          }
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm font-medium text-foreground truncate">{item.name}</p>
                          {item.isBrowser && (
                            <p className="text-xs text-muted-foreground font-mono truncate">Browser</p>
                          )}
                        </div>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-sm text-muted-foreground capitalize whitespace-nowrap">{item.kind}</td>
                    <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{item.category}</td>
                    <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap">{formatSeconds(item.totalSeconds)}</td>
                    <td className="px-4 py-3 text-sm whitespace-nowrap">
                      <span className={`font-medium ${item.focusScore >= 70 ? 'text-success' : item.focusScore >= 40 ? 'text-warning' : 'text-destructive'}`}>
                        {item.focusScore.toFixed(1)}%
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}

function UsageTile({ icon: Icon, label, value, accent }: {
  icon: React.ElementType; label: string; value: number | string; accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center ${accent}`}>
        <Icon className="w-4 h-4" />
      </div>
      <p className="mt-3 text-2xl font-bold text-foreground font-display">{value}</p>
      <p className="text-xs text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}

function Clock(props: React.SVGProps<SVGSVGElement>) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...props}>
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  );
}
