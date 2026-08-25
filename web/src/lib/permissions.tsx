import React, { createContext, useContext, useState, useCallback, ReactNode } from 'react';
import { UserRole } from './auth';
import { useAppSelector } from './store/hooks';

export type Permission = 'full' | 'view' | 'self' | 'config' | 'none';

import { STORAGE_PREFIX } from '@/config';

const PERMISSIONS_KEY = `${STORAGE_PREFIX}dynamic_permissions`;

// Legacy static module list (kept for the old settings/permissions matrix page).
// Live access control now comes from the SERVER role → submodule grants.
export const ALL_MODULES: { key: string; label: string; group: string }[] = [
  { key: 'dashboard', label: 'Dashboard', group: 'General' },
  { key: 'employee-portal', label: 'Employee Portal', group: 'General' },
  { key: 'users', label: 'Employees', group: 'HR' },
  { key: 'users/activity', label: 'User Activity Status', group: 'HR' },
  { key: 'departments', label: 'Departments', group: 'HR' },
  { key: 'roles', label: 'Roles', group: 'HR' },
  { key: 'kpis', label: 'KPIs & KRAs', group: 'HR' },
  { key: 'onboarding', label: 'Onboarding', group: 'HR' },
  { key: 'shifts', label: 'Shift Management', group: 'HR' },
  { key: 'timesheets', label: 'Timesheets', group: 'Time & Attendance' },
  { key: 'attendance', label: 'Attendance Log', group: 'Time & Attendance' },
  { key: 'gps-location', label: 'GPS & Location', group: 'Time & Attendance' },
  { key: 'hours-insights', label: 'Hours Insights', group: 'Time & Attendance' },
  { key: 'productivity-scoring', label: 'Score Card', group: 'Productivity' },
  { key: 'goals', label: 'Goals & OKRs', group: 'Productivity' },
  { key: 'employee-journey', label: 'Employee Journey', group: 'Monitoring' },
  { key: 'device-specs', label: 'Device Specs', group: 'Monitoring' },
  { key: 'screenshots', label: 'Screenshots', group: 'Monitoring' },
  { key: 'logs', label: 'Logs', group: 'Monitoring' },
  { key: 'charts', label: 'Charts', group: 'Monitoring' },
  { key: 'live-stream', label: 'Live Stream', group: 'Monitoring' },
  { key: 'reports', label: 'Reports', group: 'Reports & Analytics' },
  { key: 'audit-log', label: 'Audit Log', group: 'Reports & Analytics' },
  { key: 'executive-dashboard', label: 'Executive Dashboard', group: 'Reports & Analytics' },
  { key: 'dlp-alerts', label: 'DLP Alerts', group: 'Security & DLP' },
  { key: 'dlp-rules', label: 'DLP Rules', group: 'Security & DLP' },
  { key: 'shadow-it', label: 'Shadow IT', group: 'Security & DLP' },
  { key: 'configuration/apps', label: 'Applications Classification', group: 'Configuration' },
  { key: 'configuration/websites', label: 'Websites Classification', group: 'Configuration' },
  { key: 'configuration/categories', label: 'Categories & Types', group: 'Configuration' },
  { key: 'configuration/productivity-rules', label: 'Productivity Rules', group: 'Configuration' },
  { key: 'emails', label: 'Emails & Alerts', group: 'Communication' },
  { key: 'projects', label: 'Projects', group: 'Communication' },
  { key: 'ai-summary', label: 'AI Summary', group: 'Communication' },
  { key: 'settings', label: 'General Settings', group: 'Settings' },
  { key: 'settings/tracking', label: 'Tracking Settings', group: 'Settings' },
  { key: 'settings/user-management', label: 'User Management', group: 'Settings' },
  { key: 'settings/notifications', label: 'Notification Config', group: 'Settings' },
  { key: 'settings/billing', label: 'Billing & Subscription', group: 'Settings' },
  { key: 'settings/compliance', label: 'GDPR & Compliance', group: 'Settings' },
  { key: 'settings/security', label: 'Security Settings', group: 'Settings' },
];

export const ALL_ROLES: UserRole[] = [
  'company_admin', 'super_admin', 'org_admin', 'hr_admin', 'manager', 'employee', 'security_analyst', 'it_admin', 'auditor'
];

// Helper to add company_admin: 'full' to any role-permission map
function withCompanyAdmin(
  base: Record<Exclude<UserRole, 'company_admin'>, Permission>
): Record<UserRole, Permission> {
  return { ...base, company_admin: 'full' } as Record<UserRole, Permission>;
}

// Legacy default permissions matrix (fallback when no server permissions are
// available yet — e.g. a stale session created before this deploy).
export const DEFAULT_PERMISSIONS: Record<string, Record<UserRole, Permission>> = {
  'dashboard': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'view', it_admin: 'view', auditor: 'view' }),
  'users': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'users/activity': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'configuration/apps': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'view', it_admin: 'view', auditor: 'view' }),
  'configuration/websites': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'view', it_admin: 'view', auditor: 'view' }),
  'configuration/categories': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'config', employee: 'none', security_analyst: 'view', it_admin: 'view', auditor: 'none' }),
  'configuration/productivity-rules': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'view', auditor: 'none' }),
  'employee-journey': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'view' }),
  'device-specs': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'full', auditor: 'view' }),
  'screenshots': withCompanyAdmin({ super_admin: 'full', org_admin: 'config', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'logs': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' }),
  'charts': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'departments': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'kpis': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'roles': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'live-stream': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'full', it_admin: 'none', auditor: 'none' }),
  'emails': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'view', auditor: 'none' }),
  'projects': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'ai-summary': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'hours-insights': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'view', auditor: 'none' }),
  'settings': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'none', auditor: 'none' }),
  'settings/tracking': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'view', auditor: 'none' }),
  'onboarding': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'config', auditor: 'none' }),
  'employee-portal': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'view', employee: 'full', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'timesheets': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'view' }),
  'attendance': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'shifts': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'gps-location': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'productivity-scoring': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'goals': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'reports': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' }),
  'audit-log': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'view', it_admin: 'none', auditor: 'full' }),
  'executive-dashboard': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' }),
  'dlp-alerts': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'view' }),
  'dlp-rules': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' }),
  'shadow-it': withCompanyAdmin({ super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' }),
  'settings/billing': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'settings/compliance': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'view', it_admin: 'none', auditor: 'full' }),
  'settings/security': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' }),
  'settings/notifications': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
  'settings/user-management': withCompanyAdmin({ super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' }),
};

function loadPermissions(): Record<string, Record<UserRole, Permission>> {
  if (typeof window === 'undefined') return { ...DEFAULT_PERMISSIONS };
  try {
    const stored = localStorage.getItem(PERMISSIONS_KEY);
    if (stored) {
      return JSON.parse(stored);
    }
  } catch {}
  return { ...DEFAULT_PERMISSIONS };
}

function savePermissions(perms: Record<string, Record<UserRole, Permission>>) {
  localStorage.setItem(PERMISSIONS_KEY, JSON.stringify(perms));
}

interface PermissionsContextType {
  /** Submodule keys granted by the logged-in user's role (server-driven RBAC). */
  allowedModules: Set<string> | null;
  /** Legacy per-module/per-role matrix editor API (old settings/permissions page). */
  permissions: Record<string, Record<UserRole, Permission>>;
  hasPermission: (role: UserRole | string, module: string) => Permission;
  canAccess: (role: UserRole | string, module: string) => boolean;
  updatePermission: (module: string, role: UserRole, permission: Permission) => void;
  resetToDefaults: () => void;
}

const PermissionsContext = createContext<PermissionsContextType | null>(null);

export function PermissionsProvider({ children }: { children: ReactNode }) {
  const [permissions, setPermissions] = useState<Record<string, Record<UserRole, Permission>>>(loadPermissions);
  // Server-driven grant set for the CURRENT user (null until auth state arrives).
  const authUser = useAppSelector((state) => state.auth.user);
  const allowedModules =
    authUser?.permissions && authUser.permissions.length > 0
      ? new Set(authUser.permissions)
      : null;

  const legacyHasPermission = useCallback((role: UserRole, module: string): Permission => {
    if (role === 'super_admin' || role === 'company_admin') return 'full';
    return permissions[module]?.[role] || 'none';
  }, [permissions]);

  const hasPermission = useCallback((role: UserRole | string, module: string): Permission => {
    // Server-driven first; fall back to the legacy matrix when unavailable.
    if (allowedModules) {
      return allowedModules.has(module) || role === 'company_admin' ? 'full' : 'none';
    }
    return legacyHasPermission(role as UserRole, module);
  }, [allowedModules, legacyHasPermission]);

  const canAccess = useCallback((role: UserRole | string, module: string): boolean => {
    return hasPermission(role, module) !== 'none';
  }, [hasPermission]);

  const updatePermission = useCallback((module: string, role: UserRole, permission: Permission) => {
    setPermissions(prev => {
      const updated = {
        ...prev,
        [module]: { ...prev[module], [role]: permission }
      };
      savePermissions(updated);
      return updated;
    });
  }, []);

  const resetToDefaults = useCallback(() => {
    setPermissions({ ...DEFAULT_PERMISSIONS });
    savePermissions(DEFAULT_PERMISSIONS);
  }, []);

  return (
    <PermissionsContext.Provider value={{ allowedModules, permissions, hasPermission, canAccess, updatePermission, resetToDefaults }}>
      {children}
    </PermissionsContext.Provider>
  );
}

export function usePermissions() {
  const ctx = useContext(PermissionsContext);
  if (!ctx) throw new Error('usePermissions must be inside PermissionsProvider');
  return ctx;
}
