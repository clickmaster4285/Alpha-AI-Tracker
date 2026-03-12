import React, { createContext, useContext, useState, useCallback, ReactNode } from 'react';
import { UserRole } from './auth';

export type Permission = 'full' | 'view' | 'self' | 'config' | 'none';

import { STORAGE_PREFIX } from '@/config';

const PERMISSIONS_KEY = `${STORAGE_PREFIX}dynamic_permissions`;

// All available modules with display names
export const ALL_MODULES: { key: string; label: string; group: string }[] = [
  { key: 'dashboard', label: 'Dashboard', group: 'General' },
  { key: 'employee-portal', label: 'Employee Portal', group: 'General' },
  { key: 'users', label: 'List of Users', group: 'HR' },
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
  { key: 'apps', label: 'Apps and Websites', group: 'Monitoring' },
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
  'super_admin', 'org_admin', 'hr_admin', 'manager', 'employee', 'security_analyst', 'it_admin', 'auditor'
];

// Default permissions - the baseline
const DEFAULT_PERMISSIONS: Record<string, Record<UserRole, Permission>> = {
  'dashboard': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'view', it_admin: 'view', auditor: 'view' },
  'users': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'users/activity': { super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'apps': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'self', security_analyst: 'full', it_admin: 'none', auditor: 'none' },
  'screenshots': { super_admin: 'full', org_admin: 'config', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'logs': { super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' },
  'charts': { super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'departments': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'kpis': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'view', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'roles': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'live-stream': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'full', it_admin: 'none', auditor: 'none' },
  'emails': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'view', auditor: 'none' },
  'projects': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'ai-summary': { super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'hours-insights': { super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'view', auditor: 'none' },
  'settings': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'none', auditor: 'none' },
  'settings/tracking': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'view', auditor: 'none' },
  'onboarding': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'config', auditor: 'none' },
  'employee-portal': { super_admin: 'full', org_admin: 'view', hr_admin: 'view', manager: 'view', employee: 'full', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'timesheets': { super_admin: 'full', org_admin: 'view', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'view' },
  'attendance': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'shifts': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'gps-location': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'productivity-scoring': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'goals': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'reports': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' },
  'audit-log': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'view', it_admin: 'none', auditor: 'full' },
  'executive-dashboard': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'view' },
  'dlp-alerts': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'view' },
  'dlp-rules': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' },
  'shadow-it': { super_admin: 'full', org_admin: 'view', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' },
  'settings/billing': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'settings/compliance': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'view', it_admin: 'none', auditor: 'full' },
  'settings/security': { super_admin: 'full', org_admin: 'full', hr_admin: 'none', manager: 'none', employee: 'none', security_analyst: 'full', it_admin: 'full', auditor: 'none' },
  'settings/notifications': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'full', employee: 'self', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
  'settings/user-management': { super_admin: 'full', org_admin: 'full', hr_admin: 'full', manager: 'none', employee: 'none', security_analyst: 'none', it_admin: 'none', auditor: 'none' },
};

function loadPermissions(): Record<string, Record<UserRole, Permission>> {
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
  permissions: Record<string, Record<UserRole, Permission>>;
  hasPermission: (role: UserRole, module: string) => Permission;
  canAccess: (role: UserRole, module: string) => boolean;
  updatePermission: (module: string, role: UserRole, permission: Permission) => void;
  resetToDefaults: () => void;
}

const PermissionsContext = createContext<PermissionsContextType | null>(null);

export function PermissionsProvider({ children }: { children: ReactNode }) {
  const [permissions, setPermissions] = useState<Record<string, Record<UserRole, Permission>>>(loadPermissions);

  const hasPermission = useCallback((role: UserRole, module: string): Permission => {
    // Super admin always has full access
    if (role === 'super_admin') return 'full';
    return permissions[module]?.[role] || 'none';
  }, [permissions]);

  const canAccess = useCallback((role: UserRole, module: string): boolean => {
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
    <PermissionsContext.Provider value={{ permissions, hasPermission, canAccess, updatePermission, resetToDefaults }}>
      {children}
    </PermissionsContext.Provider>
  );
}

export function usePermissions() {
  const ctx = useContext(PermissionsContext);
  if (!ctx) throw new Error('usePermissions must be inside PermissionsProvider');
  return ctx;
}
