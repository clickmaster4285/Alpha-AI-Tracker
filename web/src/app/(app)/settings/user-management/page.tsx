'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams, useRouter } from 'next/navigation';
import { Plus, Loader2, Search, ShieldCheck } from 'lucide-react';
import { useInfiniteQuery, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { usersApi, rolesApi, type Role } from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const PAGE_SIZE = 10;

// ─── Users table (server-side infinite scroll) ───────────────────────────────

function UsersTable({ search }: { search: string }) {
  const sentinelRef = useRef<HTMLDivElement | null>(null);

  const query = useInfiniteQuery({
    queryKey: ['users', { search }],
    initialPageParam: 1,
    queryFn: ({ pageParam }) => usersApi.list({ page: pageParam as number, perPage: PAGE_SIZE, search }),
    getNextPageParam: last => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const users = useMemo(() => query.data?.pages.flatMap(p => p.data) ?? [], [query.data]);

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries[0].isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
          query.fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  if (query.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[300px]">
        <Loader2 className="w-7 h-7 animate-spin text-primary" />
      </div>
    );
  }

  return (
    <>
      <div className="rounded-xl border border-border bg-card overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border bg-muted/30 text-left">
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">User</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Email</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Employee Link</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Role</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Status</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Created</th>
              </tr>
            </thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id} className="border-b border-border/60 last:border-0 hover:bg-muted/20 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <span className={`w-8 h-8 rounded-full flex items-center justify-center text-[11px] font-bold shrink-0 ${u.avatarColor || 'bg-primary/15 text-primary'}`}>
                        {initialsOf(u.name)}
                      </span>
                      <span className="font-medium text-foreground">{u.name}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-muted-foreground">{u.email}</td>
                  <td className="px-4 py-3">
                    {u.employeeId ? (
                      <span className="px-2 py-0.5 rounded-md bg-accent text-accent-foreground text-xs font-medium">{u.employeeId}</span>
                    ) : (
                      <span className="text-muted-foreground/50 text-xs">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${
                        u.role === 'company_admin' ? 'bg-success/10 text-success' : 'bg-primary/10 text-primary'
                      }`}
                    >
                      {u.role === 'company_admin' && <ShieldCheck className="w-3 h-3" />}
                      {roleLabel(u.role)}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-flex items-center gap-1.5 text-xs font-medium ${u.isOnline ? 'text-success' : 'text-muted-foreground'}`}
                    >
                      <span className={`w-1.5 h-1.5 rounded-full ${u.isOnline ? 'bg-success' : 'bg-muted-foreground/40'}`} />
                      {u.isOnline ? 'Online' : 'Offline'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground">{formatDate(u.createdAt)}</td>
                </tr>
              ))}
              {users.length === 0 && (
                <tr>
                  <td colSpan={6} className="px-4 py-12 text-center text-sm text-muted-foreground">
                    No users found{search ? ` for "${search}"` : ''}.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {query.isFetchingNextPage && (
        <p className="text-center text-xs text-muted-foreground py-3">Loading more…</p>
      )}
      {!query.hasNextPage && users.length > 0 && (
        <p className="text-center text-xs text-muted-foreground py-3">Showing all {users.length}</p>
      )}
      <div ref={sentinelRef} />
    </>
  );
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function initialsOf(name: string): string {
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map(w => w[0]!.toUpperCase())
      .join('') || '?'
  );
}

function roleLabel(role: string): string {
  if (role === 'company_admin') return 'Company Admin';
  return role
    .replace(/_/g, ' ')
    .replace(/\b\w/g, c => c.toUpperCase());
}

function formatDate(value?: string): string {
  if (!value) return '—';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

// ─── Page ─────────────────────────────────────────────────────────────────────

function UserManagementInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryClient = useQueryClient();

  const [search, setSearch] = useState('');
  const [showDialog, setShowDialog] = useState(false);
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [employeeId, setEmployeeId] = useState('');
  const [roleId, setRoleId] = useState<string>('');

  const { data: rolesData } = useQuery({
    queryKey: ['roles'],
    queryFn: () => rolesApi.list(),
  });
  const roles: Role[] = rolesData?.roles ?? [];

  // Deep-link prefill: /settings/user-management?create=1&employeeId=&name=&email=
  useEffect(() => {
    if (searchParams.get('create') === '1') {
      setName(searchParams.get('name') ?? '');
      setEmail(searchParams.get('email') ?? '');
      setEmployeeId(searchParams.get('employeeId') ?? '');
      setShowDialog(true);
      router.replace('/settings/user-management');
    }
  }, [searchParams, router]);

  const createMutation = useMutation({
    mutationFn: () =>
      usersApi.create({
        name,
        email,
        password: password || undefined,
        employeeId: employeeId || undefined,
        roleId: Number(roleId),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success('User created', { description: `${name} can now sign in to the dashboard.` });
      closeDialog();
    },
    onError: (err: Error) => {
      toast.error('Failed to create user', { description: err.message });
    },
  });

  const closeDialog = () => {
    setShowDialog(false);
    setName('');
    setEmail('');
    setPassword('');
    setEmployeeId('');
    setRoleId('');
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div className="relative w-full sm:max-w-xs">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search users by name or email..."
            className="w-full border border-border rounded-lg pl-9 pr-3 py-2 text-sm bg-card text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <button
          onClick={() => setShowDialog(true)}
          className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 self-start sm:self-auto"
        >
          <Plus className="w-4 h-4" /> Add User
        </button>
      </div>

      <UsersTable search={search} />

      {/* Create user dialog */}
      <Dialog open={showDialog} onOpenChange={open => { if (!open) closeDialog(); }}>
        <DialogContent className="bg-card max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">Add User</DialogTitle>
          </DialogHeader>

          <div className="space-y-3 mt-1">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <input
                value={name}
                onChange={e => setName(e.target.value)}
                placeholder="Full Name"
                className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
              />
              <input
                value={email}
                onChange={e => setEmail(e.target.value)}
                type="email"
                placeholder="Email"
                className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
              />
            </div>
            <input
              value={password}
              onChange={e => setPassword(e.target.value)}
              type="password"
              placeholder="Password (leave empty for default)"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <input
              value={employeeId}
              onChange={e => setEmployeeId(e.target.value)}
              placeholder="Employee ID link (optional, e.g. EMP-00001)"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={roleId}
              onChange={e => setRoleId(e.target.value)}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">Select role...</option>
              {roles.map(r => (
                <option key={r.id} value={r.id}>
                  {roleLabel(r.name)}
                  {r.isSystem ? ' (full access)' : ''}
                </option>
              ))}
            </select>

            <button
              onClick={() => {
                if (!name.trim() || !email.trim()) {
                  toast.error('Validation error', { description: 'Name and email are required.' });
                  return;
                }
                if (!roleId) {
                  toast.error('Validation error', { description: 'Please select a role.' });
                  return;
                }
                createMutation.mutate();
              }}
              disabled={createMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 disabled:opacity-50"
            >
              {createMutation.isPending ? 'Creating...' : 'Create User'}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function UserManagementPage() {
  return (
    <Suspense fallback={<Loader2 className="w-7 h-7 animate-spin text-primary" />}>
      <UserManagementInner />
    </Suspense>
  );
}
