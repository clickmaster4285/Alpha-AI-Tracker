'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Search, Loader2 } from 'lucide-react';
import { useQuery, useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import { monitoringApi, type MonitoringCategoryKind, type ClassificationPayload } from '@/lib/api';

// Row shape the table renders and classifies. Both the app catalog and the
// website registry implement this contract.
export interface ClassifiedItemRow {
  id: string | number;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface ClassifiedListResponse<T> {
  data: T[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

// The on-disk shape of a TanStack infinite query cache for this table.
interface InfiniteListData<T> {
  pages: ClassifiedListResponse<T>[];
  pageParams: unknown[];
}

interface Props<T extends ClassifiedItemRow> {
  title: string;
  description: string;
  queryKeyPrefix: string;
  // Restricts the assignable categories (application vs website vs both).
  scope: Exclude<MonitoringCategoryKind, 'both'>;
  listFn: (params: {
    search?: string;
    typeId?: number;
    categoryId?: number;
    unclassified?: boolean;
    page: number;
    perPage: number;
  }) => Promise<ClassifiedListResponse<T>>;
  classifyFn: (id: string | number, payload: ClassificationPayload) => Promise<{ message: string }>;
  nameOf: (item: T) => string;
  // Header label for the first (name) column.
  nameHeader: string;
  // Optional badge rendered next to the item name (e.g. the browser chip).
  badge?: (item: T) => React.ReactNode;
}

const PER_PAGE = 30;

export default function ClassifiedItemsTable<T extends ClassifiedItemRow>({
  title,
  description,
  queryKeyPrefix,
  scope,
  listFn,
  classifyFn,
  nameOf,
  nameHeader,
  badge,
}: Props<T>) {
  const queryClient = useQueryClient();

  // ── Filters ──
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput), 400);
    return () => clearTimeout(t);
  }, [searchInput]);
  const [typeFilter, setTypeFilter] = useState('');
  const [categoryFilter, setCategoryFilter] = useState('');
  const [unclassifiedOnly, setUnclassifiedOnly] = useState(false);

  const filters = useMemo(
    () => ({
      search,
      typeId: typeFilter || undefined,
      categoryId: categoryFilter || undefined,
      unclassified: unclassifiedOnly || undefined,
      perPage: PER_PAGE,
    }),
    [search, typeFilter, categoryFilter, unclassifiedOnly],
  );

  // ── Reference data (types + categories) ──
  const { data: typesResponse } = useQuery({
    queryKey: ['monitoring-types'],
    queryFn: () => monitoringApi.types.list(),
  });
  const { data: categoriesResponse } = useQuery({
    queryKey: ['monitoring-categories'],
    queryFn: () => monitoringApi.categories.list(),
  });
  const types = typesResponse?.types ?? [];
  const categories = (categoriesResponse?.categories ?? []).filter(
    c => c.kind === scope || c.kind === 'both',
  );

  const typeMap = useMemo(() => new Map(types.map(t => [t.id, t])), [types]);
  const categoryMap = useMemo(() => new Map(categories.map(c => [c.id, c])), [categories]);

  // ── Infinite-scroll list (Web Infinite-Scroll Rule — AGENTS.md §6) ──
  const {
    data: listData,
    isLoading,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: [queryKeyPrefix, filters],
    queryFn: ({ pageParam }) =>
      listFn({
        search,
        typeId: typeFilter ? Number(typeFilter) : undefined,
        categoryId: categoryFilter ? Number(categoryFilter) : undefined,
        unclassified: unclassifiedOnly || undefined,
        page: pageParam as number,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

  const rows = useMemo(() => (listData?.pages.flatMap(p => p.data) ?? []) as T[], [listData]);
  const total = listData?.pages[0]?.total ?? 0;

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  // ── Classification ──
  const classifyMutation = useMutation({
    mutationFn: ({ id, payload }: { id: string | number; payload: ClassificationPayload }) =>
      classifyFn(id, payload),
    onSuccess: (_data, vars) => {
      updateCache(vars.id, vars.payload);
      toast.success('Classification updated');
    },
    onError: (err: Error) => {
      toast.error('Failed to update classification', { description: err.message });
    },
  });

  const isRowPending = (id: string | number) =>
    classifyMutation.isPending && classifyMutation.variables?.id === id;

  // Apply the classification to the cached row in place so the infinite list
  // keeps its scroll position (no full refetch needed).
  const updateCache = (id: string | number, payload: ClassificationPayload) => {
    queryClient.setQueryData<InfiniteListData<T>>([queryKeyPrefix, filters], (old) => {
      if (!old) return old;
      return {
        ...old,
        pages: old.pages.map(page => ({
          ...page,
          data: page.data.map(row => {
            if (row.id !== id) return row;
            const type = payload.typeId !== undefined && payload.typeId !== null ? typeMap.get(payload.typeId) : undefined;
            const cat = payload.categoryId !== undefined && payload.categoryId !== null ? categoryMap.get(payload.categoryId) : undefined;
            return {
              ...row,
              typeId: payload.typeId ?? undefined,
              typeName: type?.name ?? '',
              typeColor: type?.color ?? '',
              categoryId: payload.categoryId ?? undefined,
              categoryName: cat?.name ?? '',
            } as T;
          }),
        })),
      };
    });
  };

  const handleTypeChange = (item: T, value: string) => {
    const typeId = value === '' ? null : Number(value);
    classifyMutation.mutate({ id: item.id, payload: { typeId, categoryId: item.categoryId ?? null } });
  };

  const handleCategoryChange = (item: T, value: string) => {
    const categoryId = value === '' ? null : Number(value);
    classifyMutation.mutate({ id: item.id, payload: { typeId: item.typeId ?? null, categoryId } });
  };

  if (isLoading && !listData) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <p className="text-destructive">Failed to load {title.toLowerCase()}: {(error as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Header */}
      <div>
        <h1 className="font-display font-bold text-2xl text-foreground">{title}</h1>
        <p className="text-sm text-muted-foreground mt-1">{description}</p>
      </div>

      {/* Filters */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 min-w-[220px] max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search..."
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <select
          value={typeFilter}
          onChange={e => setTypeFilter(e.target.value)}
          className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
        >
          <option value="">All Types</option>
          {types.map(t => (
            <option key={t.id} value={t.id}>{t.name}</option>
          ))}
        </select>
        <select
          value={categoryFilter}
          onChange={e => setCategoryFilter(e.target.value)}
          className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
        >
          <option value="">All Categories</option>
          {categories.map(c => (
            <option key={c.id} value={c.id}>{c.name}</option>
          ))}
        </select>
        <label className="flex items-center gap-2 text-sm text-muted-foreground cursor-pointer select-none">
          <input
            type="checkbox"
            checked={unclassifiedOnly}
            onChange={e => setUnclassifiedOnly(e.target.checked)}
            className="rounded border-border"
          />
          Only unclassified
        </label>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[720px]">
          <thead>
            <tr className="border-b border-border">
              <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{nameHeader}</th>
              <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Type</th>
              <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">Category</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td colSpan={3} className="text-center py-12 text-muted-foreground text-sm">
                  No {title.toLowerCase()} found
                </td>
              </tr>
            ) : (
              rows.map((item, i) => (
                <tr
                  key={item.id}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
                >
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2.5">
                      <span className="text-sm font-medium text-foreground">{nameOf(item)}</span>
                      {badge?.(item)}
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      {item.typeColor && (
                        <span
                          className="w-2.5 h-2.5 rounded-full shrink-0"
                          style={{ backgroundColor: item.typeColor }}
                        />
                      )}
                      <select
                        value={item.typeId ? String(item.typeId) : ''}
                        onChange={e => handleTypeChange(item, e.target.value)}
                        disabled={isRowPending(item.id)}
                        className="bg-background border border-border rounded-lg px-2 py-1.5 text-sm text-foreground disabled:opacity-50"
                        aria-label={`Type for ${nameOf(item)}`}
                      >
                        <option value="">None</option>
                        {types.map(t => (
                          <option key={t.id} value={t.id}>{t.name}</option>
                        ))}
                      </select>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <select
                      value={item.categoryId ? String(item.categoryId) : ''}
                      onChange={e => handleCategoryChange(item, e.target.value)}
                      disabled={isRowPending(item.id)}
                      className="bg-background border border-border rounded-lg px-2 py-1.5 text-sm text-foreground disabled:opacity-50"
                      aria-label={`Category for ${nameOf(item)}`}
                    >
                      <option value="">None</option>
                      {categories.map(c => (
                        <option key={c.id} value={c.id}>{c.name}</option>
                      ))}
                    </select>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Infinite scroll footer (server-side pagination; no Next/Previous buttons) */}
      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        rows.length > 0 && (
          <p className="text-sm text-muted-foreground text-center">
            Showing all {total.toLocaleString()} {title.toLowerCase()}
          </p>
        )
      )}
    </div>
  );
}