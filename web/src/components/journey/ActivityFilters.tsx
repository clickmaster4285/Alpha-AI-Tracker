'use client';

import { useEffect, useRef, useState } from 'react';
import { Search, CalendarRange, Loader2, X } from 'lucide-react';
import { Calendar } from '@/components/ui/calendar';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { endOfDay, format, startOfDay, subDays } from 'date-fns';

export type DatePreset = 'today' | 'yesterday' | '7d' | '30d' | 'all' | 'custom';

export interface ActivityFilter {
  /** Debounced search text — sent to the server as `search`. */
  search: string;
  /** Inclusive start (ISO with local offset) — sent as `dateFrom`. */
  dateFrom?: string;
  /** Inclusive end (ISO with local offset) — sent as `dateTo`. */
  dateTo?: string;
  preset: DatePreset;
}

const PRESETS: { key: DatePreset; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'yesterday', label: 'Yesterday' },
  { key: '7d', label: 'Last 7 days' },
  { key: '30d', label: 'Last 30 days' },
  { key: 'all', label: 'All time' },
];

export const DEFAULT_FILTER: ActivityFilter = { search: '', preset: 'today' };

function presetRange(preset: DatePreset): { dateFrom?: string; dateTo?: string } {
  switch (preset) {
    case 'today':
      return { dateFrom: startOfDay(new Date()).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case 'yesterday': {
      const y = subDays(new Date(), 1);
      return { dateFrom: startOfDay(y).toISOString(), dateTo: endOfDay(y).toISOString() };
    }
    case '7d':
      return { dateFrom: startOfDay(subDays(new Date(), 6)).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case '30d':
      return { dateFrom: startOfDay(subDays(new Date(), 29)).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case 'all':
    case 'custom':
      return {};
  }
}

function presetLabel(preset: DatePreset): string {
  switch (preset) {
    case 'today': return 'Today';
    case 'yesterday': return 'Yesterday';
    case '7d': return 'Last 7 days';
    case '30d': return 'Last 30 days';
    case 'all': return 'All time';
    case 'custom': return 'Custom';
  }
}

interface ActivityFiltersProps {
  value: ActivityFilter;
  onChange: (filter: ActivityFilter) => void;
  loading?: boolean;
}

export default function ActivityFilters({ value, onChange, loading }: ActivityFiltersProps) {
  const [searchInput, setSearchInput] = useState(value.search);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Debounce search: only propagate to the server after 300ms of no typing.
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      if (searchInput !== value.search) onChange({ ...value, search: searchInput });
    }, 300);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchInput]);

  const applyPreset = (preset: DatePreset) => {
    const { dateFrom, dateTo } = presetRange(preset);
    onChange({ ...value, preset, dateFrom, dateTo });
  };

  const applyCustomRange = (range: { from?: Date; to?: Date } | undefined) => {
    if (!range?.from || !range.to) return;
    onChange({
      ...value,
      preset: 'custom',
      dateFrom: startOfDay(range.from).toISOString(),
      dateTo: endOfDay(range.to).toISOString(),
    });
  };

  const clearSearch = () => {
    setSearchInput('');
    onChange({ ...value, search: '' });
  };

  const rangeLabel = value.preset === 'custom' && value.dateFrom && value.dateTo
    ? `${format(new Date(value.dateFrom), 'MMM d')} – ${format(new Date(value.dateTo), 'MMM d')}`
    : presetLabel(value.preset);

  return (
    <div className="flex flex-wrap items-center gap-2">
      {/* Search */}
      <div className="relative flex items-center">
        <Search className="w-4 h-4 text-muted-foreground absolute left-3" />
        <input
          value={searchInput}
          onChange={e => setSearchInput(e.target.value)}
          placeholder="Search apps, pages, URLs…"
          className="bg-card border border-border rounded-lg pl-9 pr-8 py-2 text-sm outline-none focus:ring-2 focus:ring-primary/30 transition-shadow w-56 placeholder:text-muted-foreground"
        />
        {searchInput ? (
          <button
            onClick={clearSearch}
            className="absolute right-2 p-0.5 rounded hover:bg-muted transition-colors text-muted-foreground"
            title="Clear search"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        ) : (
          loading && <Loader2 className="w-3.5 h-3.5 animate-spin text-muted-foreground absolute right-2.5" />
        )}
      </div>

      {/* Date presets */}
      <div className="flex items-center gap-1 flex-wrap">
        {PRESETS.map(p => (
          <button
            key={p.key}
            onClick={() => applyPreset(p.key)}
            className={`px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors whitespace-nowrap ${
              value.preset === p.key
                ? 'bg-primary text-primary-foreground shadow-card-hover'
                : 'text-muted-foreground hover:text-foreground hover:bg-muted'
            }`}
          >
            {p.label}
          </button>
        ))}
      </div>

      {/* Custom date range */}
      <Popover>
        <PopoverTrigger asChild>
          <button
            className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors border whitespace-nowrap ${
              value.preset === 'custom'
                ? 'border-primary text-primary bg-primary/10'
                : 'border-border text-muted-foreground hover:text-foreground hover:bg-muted'
            }`}
          >
            <CalendarRange className="w-3.5 h-3.5" />
            {rangeLabel}
          </button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0 bg-card border-border" align="start">
          <Calendar
            mode="range"
            selected={{
              from: value.dateFrom ? new Date(value.dateFrom) : undefined,
              to: value.dateTo ? new Date(value.dateTo) : undefined,
            }}
            onSelect={applyCustomRange}
            numberOfMonths={2}
          />
        </PopoverContent>
      </Popover>
    </div>
  );
}
