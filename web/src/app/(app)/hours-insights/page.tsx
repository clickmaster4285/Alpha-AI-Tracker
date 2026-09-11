'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import {
  AppWindow, Globe, Timer, Activity, Loader2,
  Monitor, ShieldCheck, Layers, Sparkles,
  BarChart3, CheckCircle2, AlertCircle, HelpCircle,
} from 'lucide-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import {
  AreaChart, Area, XAxis, YAxis, CartesianGrid, ResponsiveContainer, Legend, Tooltip,
} from 'recharts';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { hoursInsightsApi } from '@/lib/api';
import { formatSeconds } from '@/lib/format';
import { ChartContainer, ChartTooltipContent, ChartLegendContent } from '@/components/ui/chart';
import { cn } from '@/lib/utils';

const PRODUCTIVE_COLOR = '#10b981';
const UNPRODUCTIVE_COLOR = '#ef4444';
const NEUTRAL_COLOR = '#64748b';

const productivityChartConfig = {
  productive: { label: 'Productive', color: PRODUCTIVE_COLOR },
  neutral: { label: 'Neutral', color: NEUTRAL_COLOR },
  unproductive: { label: 'Unproductive', color: UNPRODUCTIVE_COLOR },
};

export default function HoursInsightsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Hours Insights"
        subtitle="Track and compare employee time across productivity categories and individual applications."
        icon={Clock}
        bodySchema={{
          view: { parse: (raw: string) => (raw === 'applications' ? 'applications' : 'productivity') },
        }}
        bodyInitial={{ view: 'applications' }}
      >
        {({ employee, filter, setFilter, body, setBody }) => (
          <HoursInsightsBody
            employeeId={employee.employeeId}
            filter={filter}
            setFilter={setFilter}
            viewMode={(body.view as 'productivity' | 'applications') || 'productivity'}
            setViewMode={(next) => setBody({ view: next })}
          />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function HoursInsightsBody({
  employeeId,
  filter,
  setFilter,
  viewMode,
  setViewMode,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
  viewMode: 'productivity' | 'applications';
  setViewMode: (next: 'productivity' | 'applications') => void;
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
  const productivityChart = data?.chart ?? [];
  const appChartRaw = data?.appChart ?? [];
  const topApps = data?.topApps ?? [];
  const topItems = data?.topItems ?? [];

  const totalDuration = summary?.totalSeconds ?? 0;
  const productiveSec = summary?.productiveSeconds ?? 0;
  const unproductiveSec = summary?.unproductiveSeconds ?? 0;
  const neutralSec = summary?.neutralSeconds ?? 0;
  const appCount = summary?.appCount ?? 0;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  // Individual application chart config & flattened rows
  const { appChartConfig, appChartData, appKeysSorted } = useMemo(() => {
    const cfg: Record<string, { label: string; color: string }> = {};

    // Keys in ascending order of usage so that when Recharts stacks them,
    // the highest usage app is on TOP of the stack.
    const reversedApps = [...topApps].reverse();
    const keys: string[] = [];

    // Add 'Other' first if present
    cfg['Other'] = { label: 'Other', color: '#94a3b8' };
    keys.push('Other');

    reversedApps.forEach(a => {
      cfg[a.name] = { label: a.name, color: a.color };
      keys.push(a.name);
    });

    const chartData = appChartRaw.map(b => {
      const row: Record<string, string | number> = { bucket: b.bucket };
      keys.forEach(k => {
        row[k] = b.apps[k] || 0;
      });
      return row;
    });

    return {
      appChartConfig: cfg,
      appChartData: chartData,
      appKeysSorted: keys,
    };
  }, [topApps, appChartRaw]);

  // Calculations for stat percentages
  const prodPct = totalDuration > 0 ? Math.round((productiveSec / totalDuration) * 100) : 0;
  const neutPct = totalDuration > 0 ? Math.round((neutralSec / totalDuration) * 100) : 0;
  const unprodPct = totalDuration > 0 ? Math.round((unproductiveSec / totalDuration) * 100) : 0;

  const topAppName = topApps[0]?.name || 'None';
  const topAppDuration = topApps[0]?.totalSeconds ? formatSeconds(topApps[0].totalSeconds) : '0s';
  const totalSessions = topApps.reduce((acc, a) => acc + a.sessionCount, 0);

  return (
    <div className="space-y-4">
      {/* Top Filter Bar: Activity Filters + View Mode Segmented Control */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 bg-card p-3 rounded-xl border border-border shadow-card">
        <div className="flex-1">
          <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} />
        </div>

        {/* View Mode Toggle */}
        <div className="flex items-center gap-1 self-start sm:self-center p-1 bg-muted/70 rounded-xl border border-border/80 flex-shrink-0">
           <button
            type="button"
            onClick={() => setViewMode('applications')}
            className={cn(
              "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-all duration-200",
              viewMode === 'applications'
                ? "bg-card text-foreground shadow-sm border border-border/60"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            <Layers className="w-3.5 h-3.5 text-blue-500" />
            <span>From Application Individually</span>
          </button>
          <button
            type="button"
            onClick={() => setViewMode('productivity')}
            className={cn(
              "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-all duration-200",
              viewMode === 'productivity'
                ? "bg-card text-foreground shadow-sm border border-border/60"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            <ShieldCheck className="w-3.5 h-3.5 text-emerald-500" />
            <span>From Productivity</span>
          </button>
         
        </div>
      </div>

      {query.isLoading ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="w-7 h-7 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-16 gap-3 text-center bg-card rounded-xl border border-border p-6 shadow-card">
          <AlertCircle className="w-8 h-8 text-destructive" />
          <p className="text-sm text-destructive font-semibold">Failed to load hours insights</p>
          <p className="text-xs text-muted-foreground max-w-md">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : !data || totalDuration === 0 ? (
        <EmptyState
          icon={Monitor}
          text={filtered ? 'No usage matches the current filters' : 'No usage data synced yet for this period'}
        />
      ) : (
        <>
          {/* Stat Tiles */}
          {viewMode === 'productivity' ? (
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
              <UsageTile
                icon={Timer}
                label="Total Active Time"
                value={formatSeconds(totalDuration)}
                badge={`${appCount} apps`}
                accent="bg-primary/10 text-primary"
              />
              <UsageTile
                icon={CheckCircle2}
                label="Productive Time"
                value={formatSeconds(productiveSec)}
                badge={`${prodPct}% of total`}
                accent="bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
              />
              <UsageTile
                icon={HelpCircle}
                label="Neutral Time"
                value={formatSeconds(neutralSec)}
                badge={`${neutPct}% of total`}
                accent="bg-slate-500/15 text-slate-600 dark:text-slate-400"
              />
              <UsageTile
                icon={AlertCircle}
                label="Unproductive Time"
                value={formatSeconds(unproductiveSec)}
                badge={`${unprodPct}% of total`}
                accent="bg-rose-500/15 text-rose-600 dark:text-rose-400"
              />
            </div>
          ) : (
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
              <UsageTile
                icon={Timer}
                label="Total App Time"
                value={formatSeconds(totalDuration)}
                accent="bg-primary/10 text-primary"
              />
              <UsageTile
                icon={AppWindow}
                label="Applications Used"
                value={topApps.length}
                accent="bg-info/15 text-info"
              />
              <UsageTile
                icon={Sparkles}
                label="Top Application"
                value={topAppName}
                badge={topAppDuration}
                accent="bg-amber-500/15 text-amber-600 dark:text-amber-400"
              />
              <UsageTile
                icon={Activity}
                label="Total Sessions"
                value={totalSessions}
                accent="bg-violet-500/15 text-violet-600 dark:text-violet-400"
              />
            </div>
          )}

          {/* Chart Section */}
          <div className="bg-card rounded-xl border border-border shadow-card p-5">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-4">
              <div>
                <h4 className="text-sm font-semibold text-foreground flex items-center gap-2">
                  {viewMode === 'productivity' ? (
                    <>
                      <ShieldCheck className="w-4 h-4 text-emerald-500" />
                      <span>Productivity Time Distribution</span>
                    </>
                  ) : (
                    <>
                      <BarChart3 className="w-4 h-4 text-blue-500" />
                      <span>Application Usage by Time / Hour</span>
                    </>
                  )}
                </h4>
                <p className="text-xs text-muted-foreground mt-0.5">
                  {viewMode === 'productivity'
                    ? 'Stacked view showing productive, neutral, and unproductive hours'
                    : 'Individual application usage breakdown with highest usage prioritized on top'
                  }
                </p>
              </div>

              {viewMode === 'productivity' && (
                <div className="flex items-center gap-3 text-xs text-muted-foreground">
                  <span className="flex items-center gap-1.5">
                    <span className="w-2.5 h-2.5 rounded-full bg-[#10b981]" />
                    Productive
                  </span>
                  <span className="flex items-center gap-1.5">
                    <span className="w-2.5 h-2.5 rounded-full bg-[#64748b]" />
                    Neutral
                  </span>
                  <span className="flex items-center gap-1.5">
                    <span className="w-2.5 h-2.5 rounded-full bg-[#ef4444]" />
                    Unproductive
                  </span>
                </div>
              )}
            </div>

            {viewMode === 'productivity' ? (
              <ChartContainer config={productivityChartConfig} className="aspect-[2.2/1] w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={productivityChart} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" opacity={0.6} />
                    <XAxis
                      dataKey="bucket"
                      stroke="var(--color-muted-foreground)"
                      fontSize={11}
                      tickLine={false}
                      axisLine={false}
                    />
                    <YAxis
                      stroke="var(--color-muted-foreground)"
                      fontSize={11}
                      tickLine={false}
                      axisLine={false}
                      tickFormatter={(v: number) => formatSeconds(v)}
                      width={65}
                    />
                    <Tooltip
                      content={
                        <ChartTooltipContent
                          formatter={(value: number, name: string) => [
                            formatSeconds(value),
                            productivityChartConfig[name as keyof typeof productivityChartConfig]?.label || name,
                          ]}
                        />
                      }
                    />
                    <Legend content={<ChartLegendContent />} />
                    <Area
                      type="monotone"
                      dataKey="unproductive"
                      stackId="1"
                      stroke={UNPRODUCTIVE_COLOR}
                      fill={UNPRODUCTIVE_COLOR}
                      fillOpacity={0.8}
                    />
                    <Area
                      type="monotone"
                      dataKey="neutral"
                      stackId="1"
                      stroke={NEUTRAL_COLOR}
                      fill={NEUTRAL_COLOR}
                      fillOpacity={0.8}
                    />
                    <Area
                      type="monotone"
                      dataKey="productive"
                      stackId="1"
                      stroke={PRODUCTIVE_COLOR}
                      fill={PRODUCTIVE_COLOR}
                      fillOpacity={0.8}
                    />
                  </AreaChart>
                </ResponsiveContainer>
              </ChartContainer>
            ) : (
              <ChartContainer config={appChartConfig} className="aspect-[2.2/1] w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <AreaChart data={appChartData} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" opacity={0.6} />
                    <XAxis
                      dataKey="bucket"
                      stroke="var(--color-muted-foreground)"
                      fontSize={11}
                      tickLine={false}
                      axisLine={false}
                    />
                    <YAxis
                      stroke="var(--color-muted-foreground)"
                      fontSize={11}
                      tickLine={false}
                      axisLine={false}
                      tickFormatter={(v: number) => formatSeconds(v)}
                      width={65}
                    />
                    <Tooltip
                      content={
                        <ChartTooltipContent
                          formatter={(value: number, name: string) => [
                            formatSeconds(value),
                            appChartConfig[name]?.label || name,
                          ]}
                        />
                      }
                    />
                    <Legend content={<ChartLegendContent />} />
                    {appKeysSorted.map((key) => {
                      const color = appChartConfig[key]?.color || '#94a3b8';
                      return (
                        <Area
                          key={key}
                          type="monotone"
                          dataKey={key}
                          stackId="1"
                          stroke={color}
                          fill={color}
                          fillOpacity={0.75}
                        />
                      );
                    })}
                  </AreaChart>
                </ResponsiveContainer>
              </ChartContainer>
            )}
          </div>

          {/* Breakdown Section */}
          {viewMode === 'productivity' ? (
            <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
              <div className="p-4 border-b border-border">
                <h4 className="text-sm font-semibold text-foreground">Top Applications & Websites by Duration</h4>
                <p className="text-xs text-muted-foreground mt-0.5">Ranked by duration spent during the selected range</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[640px]">
                  <thead>
                    <tr className="border-b border-border bg-muted/20">
                      {['App / Website', 'Type', 'Category', 'Duration', 'Focus Score'].map(h => (
                        <th key={h} className="text-left px-4 py-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {topItems.map((item, idx) => {
                      const typeBg = item.type === 'Productive'
                        ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/20'
                        : item.type === 'Unproductive'
                        ? 'bg-rose-500/10 text-rose-600 dark:text-rose-400 border-rose-500/20'
                        : 'bg-slate-500/10 text-slate-600 dark:text-slate-400 border-slate-500/20';

                      return (
                        <tr key={`${item.kind}-${item.name}-${idx}`} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                          <td className="px-4 py-3">
                            <div className="flex items-center gap-2.5">
                              <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                                {item.kind === 'app'
                                  ? <AppWindow className="w-4 h-4 text-primary" />
                                  : <Globe className="w-4 h-4 text-primary" />
                                }
                              </div>
                              <div className="min-w-0">
                                <p className="text-sm font-medium text-foreground truncate">{item.name}</p>
                                {item.isBrowser && (
                                  <p className="text-[11px] text-muted-foreground font-mono truncate">Browser</p>
                                )}
                              </div>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-sm text-muted-foreground capitalize whitespace-nowrap">{item.kind}</td>
                          <td className="px-4 py-3 whitespace-nowrap">
                            <span className={cn("px-2 py-0.5 rounded-md text-xs font-medium border", typeBg)}>
                              {item.category}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap font-mono">{formatSeconds(item.totalSeconds)}</td>
                          <td className="px-4 py-3 text-sm whitespace-nowrap">
                            <span className={`font-semibold ${item.focusScore >= 70 ? 'text-emerald-500' : item.focusScore >= 40 ? 'text-amber-500' : 'text-rose-500'}`}>
                              {item.focusScore.toFixed(1)}%
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          ) : (
            <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
              <div className="p-4 border-b border-border">
                <h4 className="text-sm font-semibold text-foreground">Individual Applications Usage Breakdown</h4>
                <p className="text-xs text-muted-foreground mt-0.5">Most active applications sorted by total usage time</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[680px]">
                  <thead>
                    <tr className="border-b border-border bg-muted/20">
                      {['Application', 'Category', 'Share of Time', 'Duration', 'Sessions', 'Status'].map(h => (
                        <th key={h} className="text-left px-4 py-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {topApps.map((app, idx) => {
                      const sharePct = totalDuration > 0 ? Math.min(100, Math.round((app.totalSeconds / totalDuration) * 100)) : 0;
                      return (
                        <tr key={`${app.name}-${idx}`} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                          <td className="px-4 py-3">
                            <div className="flex items-center gap-2.5">
                              <div
                                className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0"
                                style={{ backgroundColor: `${app.color}15`, color: app.color }}
                              >
                                <AppWindow className="w-4 h-4" />
                              </div>
                              <div className="min-w-0">
                                <p className="text-sm font-medium text-foreground truncate">{app.name}</p>
                                <span className="text-[11px] text-muted-foreground">Rank #{idx + 1}</span>
                              </div>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">
                            <span className="px-2 py-0.5 rounded-md text-xs font-medium bg-muted text-muted-foreground">
                              {app.category || 'General'}
                            </span>
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap min-w-[160px]">
                            <div className="flex items-center gap-2">
                              <div className="flex-1 h-2 rounded-full bg-muted overflow-hidden">
                                <div
                                  className="h-full rounded-full transition-all duration-300"
                                  style={{ width: `${sharePct}%`, backgroundColor: app.color }}
                                />
                              </div>
                              <span className="text-xs font-medium text-muted-foreground w-8 text-right font-mono">{sharePct}%</span>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-sm text-foreground font-semibold whitespace-nowrap font-mono">{formatSeconds(app.totalSeconds)}</td>
                          <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap font-mono">{app.sessionCount}</td>
                          <td className="px-4 py-3 whitespace-nowrap">
                            <span className={cn(
                              "px-2 py-0.5 rounded-md text-[11px] font-medium border",
                              app.type === 'Productive'
                                ? "bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/20"
                                : app.type === 'Unproductive'
                                ? "bg-rose-500/10 text-rose-600 dark:text-rose-400 border-rose-500/20"
                                : "bg-slate-500/10 text-slate-600 dark:text-slate-400 border-slate-500/20"
                            )}>
                              {app.type || 'Neutral'}
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function UsageTile({
  icon: Icon,
  label,
  value,
  badge,
  accent,
}: {
  icon: React.ElementType;
  label: string;
  value: number | string;
  badge?: string;
  accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-all">
      <div className="flex items-center justify-between gap-2">
        <div className={`w-8 h-8 rounded-lg flex items-center justify-center ${accent}`}>
          <Icon className="w-4 h-4" />
        </div>
        {badge && (
          <span className="px-2 py-0.5 rounded-full text-[11px] font-semibold bg-muted text-muted-foreground border border-border">
            {badge}
          </span>
        )}
      </div>
      <p className="mt-3 text-xl sm:text-2xl font-bold text-foreground font-display truncate" title={String(value)}>
        {value}
      </p>
      <p className="text-xs text-muted-foreground mt-0.5 font-medium">{label}</p>
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
