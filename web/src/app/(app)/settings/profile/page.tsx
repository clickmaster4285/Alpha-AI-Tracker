'use client';

import { useEffect, useMemo, useState } from 'react';
import {
  Loader2,
  UserCircle2,
  Eye,
  EyeOff,
  ShieldCheck,
  Briefcase,
  Building2,
  KeyRound,
  ListChecks,
  Lock,
  Mail,
  User as UserIcon,
} from 'lucide-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { authApi, usersApi, employeesApi, type UpdateUserPayload, type ProfileResponse } from '@/lib/api';

export default function ProfilePage() {
  const queryClient = useQueryClient();

  // Single endpoint that returns the full profile picture (user + role +
  // RBAC module breakdown + linked employee). Identity is resolved from the
  // httpOnly cookie on the server — no extra round-trips on the client.
  const profileQuery = useQuery({
    queryKey: ['auth', 'profile'],
    queryFn: () => authApi.profile(),
    staleTime: 60_000,
  });

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);

  // Initialize form fields from the profile payload.
  useEffect(() => {
    if (profileQuery.data && !name && !email) {
      setName(profileQuery.data.user.name ?? '');
      setEmail(profileQuery.data.user.email ?? '');
    }
  }, [profileQuery.data, name, email]);

  const saveMutation = useMutation({
    mutationFn: async () => {
      const me = profileQuery.data?.user;
      if (!me) throw new Error('Not signed in.');
      const data: UpdateUserPayload = {};
      const nameChanged = name.trim() !== me.name;
      const emailChanged = email.trim() !== me.email;
      if (nameChanged) data.name = name.trim();
      if (emailChanged) data.email = email.trim();
      if (password) data.password = password;

      // Update the user record via the standard endpoint.
      const updated = await usersApi.update(me.id, data);

      // Reverse-sync to the attached employee record (same pattern as the
      // admin user-management edit flow). The employeeId is the join key.
      if (me.employeeId && (nameChanged || emailChanged)) {
        try {
          const list = await employeesApi.list({ search: me.employeeId, perPage: 1 });
          const emp = list.data.find(e => e.employeeId === me.employeeId);
          if (emp) {
            await employeesApi.update(emp.id, {
              name: nameChanged ? name.trim() : undefined,
              email: emailChanged ? email.trim() : undefined,
            });
          }
        } catch (e) {
          // Non-fatal: the user record saved; employee sync failed.
          toast.warning('Attached employee profile not synced', {
            description: (e as Error).message,
          });
        }
      }
      return updated;
    },
    onSuccess: () => {
      // Refresh the cached profile (covers user + role + permissions + employee)
      // and the broader employees list (so any UI showing the linked employee
      // sees the new name/email).
      queryClient.invalidateQueries({ queryKey: ['auth', 'profile'] });
      queryClient.invalidateQueries({ queryKey: ['auth', 'me'] });
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Profile updated', { description: 'Your changes have been saved.' });
      setPassword('');
      setConfirmPassword('');
      setShowPassword(false);
      setShowConfirmPassword(false);
    },
    onError: (err: Error) => {
      toast.error('Failed to update profile', { description: err.message });
    },
  });

  const handleSave = () => {
    if (!profileQuery.data?.user) return;
    if (!name.trim()) {
      toast.error('Validation error', { description: 'Name is required.' });
      return;
    }
    if (!email.trim()) {
      toast.error('Validation error', { description: 'Email is required.' });
      return;
    }
    if (password || confirmPassword) {
      if (password !== confirmPassword) {
        toast.error('Validation error', { description: 'Passwords do not match.' });
        return;
      }
      if (password.length < 6) {
        toast.error('Validation error', { description: 'Password must be at least 6 characters.' });
        return;
      }
    }
    saveMutation.mutate();
  };

  const handleReset = (me: ProfileResponse['user']) => {
    setName(me.name);
    setEmail(me.email);
    setPassword('');
    setConfirmPassword('');
    setShowPassword(false);
    setShowConfirmPassword(false);
  };

  if (profileQuery.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-7 h-7 animate-spin text-primary" />
      </div>
    );
  }

  if (profileQuery.error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Failed to load profile</p>
          <p className="text-sm text-muted-foreground">{(profileQuery.error as Error).message}</p>
          <button
            onClick={() => profileQuery.refetch()}
            className="mt-4 text-sm text-primary hover:underline"
          >
            Try again
          </button>
        </div>
      </div>
    );
  }

  const profile = profileQuery.data!;
  const me = profile.user;
  const role = profile.role;
  const emp = profile.employee;
  const perms = profile.permissions;

  return (
    <div className="space-y-4 animate-fade-in max-w-3xl">
      <div className="flex items-center gap-3">
        <div className="w-10 h-10 rounded-full flex items-center justify-center bg-primary/15 text-primary">
          <UserCircle2 className="w-5 h-5" />
        </div>
        <div>
          <h1 className="font-display text-xl font-semibold text-foreground">My Profile</h1>
          <p className="text-xs text-muted-foreground">
            View your account details, role, and access. Update name, email, and password below.
          </p>
        </div>
      </div>

      {/* Identity / account block — read-only */}
      <section className="bg-card rounded-xl border border-border p-5 space-y-4">
        <header className="flex items-center justify-between">
          <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
            <UserIcon className="w-4 h-4 text-muted-foreground" />
            Account
          </h2>
        </header>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium mb-1">User ID</p>
            <p className="text-sm font-mono text-foreground">{me.id}</p>
          </div>
          <div>
            <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium mb-1">Employee Link</p>
            <p className="text-sm font-mono text-foreground">
              {me.employeeId || <span className="text-muted-foreground/50">—</span>}
            </p>
          </div>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <div className="flex items-start gap-2.5 p-3 rounded-lg bg-background border border-border">
            <ShieldCheck className="w-4 h-4 text-primary mt-0.5 shrink-0" />
            <div className="min-w-0">
              <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Role</p>
              <p className="text-sm font-medium text-foreground truncate">{role?.name ?? me.role}</p>
              {role?.isSystem && (
                <span className="inline-flex items-center gap-1 mt-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-success/10 text-success">
                  <Lock className="w-2.5 h-2.5" /> System
                </span>
              )}
            </div>
          </div>
          <div className="flex items-start gap-2.5 p-3 rounded-lg bg-background border border-border">
            <Building2 className="w-4 h-4 text-primary mt-0.5 shrink-0" />
            <div className="min-w-0">
              <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Department</p>
              <p className="text-sm font-medium text-foreground truncate">
                {emp?.department || <span className="text-muted-foreground/50">—</span>}
              </p>
            </div>
          </div>
          <div className="flex items-start gap-2.5 p-3 rounded-lg bg-background border border-border">
            <Briefcase className="w-4 h-4 text-primary mt-0.5 shrink-0" />
            <div className="min-w-0">
              <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Shift</p>
              <p className="text-sm font-medium text-foreground truncate">
                {emp?.shift || me.shift || <span className="text-muted-foreground/50">—</span>}
              </p>
            </div>
          </div>
        </div>
      </section>

      {/* Access block — granted submodules per navigation module */}
      <section className="bg-card rounded-xl border border-border p-5 space-y-4">
        <header>
          <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
            <ListChecks className="w-4 h-4 text-muted-foreground" />
            Modules you can access
          </h2>
          <p className="text-xs text-muted-foreground mt-1">
            {perms.isSystemAdmin
              ? 'You hold the system role and have full access to every module.'
              : `Granted ${perms.submoduleKeys.length} submodule key${perms.submoduleKeys.length === 1 ? '' : 's'} across ${perms.modules.filter(m => m.grantedCount > 0).length} module${perms.modules.filter(m => m.grantedCount > 0).length === 1 ? '' : 's'}.`}
          </p>
        </header>

        {perms.modules.length === 0 ? (
          <p className="text-xs text-muted-foreground">No modules are defined in the RBAC catalog yet.</p>
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {perms.modules.map(m => {
              const granted = perms.isSystemAdmin || m.grantedCount > 0;
              return (
                <div
                  key={m.id}
                  className={`flex items-center justify-between p-3 rounded-lg border ${
                    granted
                      ? 'bg-primary/5 border-primary/30'
                      : 'bg-muted/30 border-border opacity-60'
                  }`}
                >
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-foreground truncate">{m.name}</p>
                    <p className="text-[11px] font-mono text-muted-foreground truncate">{m.key}</p>
                  </div>
                  <span
                    className={`shrink-0 inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium ${
                      perms.isSystemAdmin
                        ? 'bg-success/10 text-success'
                        : granted
                        ? 'bg-primary/10 text-primary'
                        : 'bg-muted text-muted-foreground'
                    }`}
                  >
                    {perms.isSystemAdmin ? 'all' : `${m.grantedCount}/${m.submoduleCount}`}
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </section>

      {/* Editable block — name / email / password */}
      <section className="bg-card rounded-xl border border-border p-5 space-y-4">
        <header>
          <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
            <KeyRound className="w-4 h-4 text-muted-foreground" />
            Update details
          </h2>
          {me.employeeId && (
            <div className="mt-2 flex items-center gap-2 bg-muted/50 border border-border rounded-lg px-3 py-2 text-xs text-muted-foreground">
              <ShieldCheck className="w-3.5 h-3.5 shrink-0" />
              <span>
                This profile is linked to employee <span className="font-mono">{me.employeeId}</span>;
                name and email changes are propagated automatically.
              </span>
            </div>
          )}
        </header>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">
              <Mail className="w-3 h-3 inline mr-1" />Full Name
            </label>
            <input
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="Full Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">
              <Mail className="w-3 h-3 inline mr-1" />Email
            </label>
            <input
              value={email}
              onChange={e => setEmail(e.target.value)}
              type="email"
              placeholder="Email"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
          </div>
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">New Password</label>
          <div className="relative">
            <input
              value={password}
              onChange={e => setPassword(e.target.value)}
              type={showPassword ? 'text' : 'password'}
              placeholder="Leave empty to keep current password"
              className="w-full border border-border rounded-lg pl-3 pr-10 py-2 text-sm bg-background text-foreground"
            />
            <button
              type="button"
              onClick={() => setShowPassword(s => !s)}
              className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 text-muted-foreground hover:text-foreground rounded transition-colors"
              aria-label={showPassword ? 'Hide password' : 'Show password'}
              tabIndex={-1}
            >
              {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
            </button>
          </div>
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">Confirm Password</label>
          <div className="relative">
            <input
              value={confirmPassword}
              onChange={e => setConfirmPassword(e.target.value)}
              type={showConfirmPassword ? 'text' : 'password'}
              placeholder="Re-enter the new password"
              className="w-full border border-border rounded-lg pl-3 pr-10 py-2 text-sm bg-background text-foreground"
            />
            <button
              type="button"
              onClick={() => setShowConfirmPassword(s => !s)}
              className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 text-muted-foreground hover:text-foreground rounded transition-colors"
              aria-label={showConfirmPassword ? 'Hide confirm password' : 'Show confirm password'}
              tabIndex={-1}
            >
              {showConfirmPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
            </button>
          </div>
        </div>

        <div className="flex items-center justify-end gap-2 pt-2">
          <button
            type="button"
            onClick={() => handleReset(me)}
            disabled={saveMutation.isPending}
            className="px-4 py-2 rounded-lg text-sm font-medium border border-border text-foreground hover:bg-muted transition-colors disabled:opacity-50"
          >
            Reset
          </button>
          <button
            type="button"
            onClick={handleSave}
            disabled={saveMutation.isPending}
            className="px-4 py-2 rounded-lg text-sm font-medium gradient-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
          >
            {saveMutation.isPending ? 'Saving...' : 'Save Changes'}
          </button>
        </div>
      </section>
    </div>
  );
}
