import React, { createContext, useContext, useState, useEffect, ReactNode } from 'react';
import { STORAGE_PREFIX } from '@/config';

export type UserRole = 'super_admin' | 'org_admin' | 'hr_admin' | 'manager' | 'employee' | 'security_analyst' | 'it_admin' | 'auditor';

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  role: UserRole;
  avatar: string;
  avatarColor: string;
  department?: string;
  jobTitle?: string;
  hireDate?: string;
}

const FIXED_USERS: AuthUser[] = [
  { id: 'sa1', name: 'Super Admin', email: 'superadmin@alphai.com', role: 'super_admin', avatar: 'SA', avatarColor: '#7C3AED', department: 'Executive', jobTitle: 'System Administrator' },
  { id: 'oa1', name: 'Org Admin', email: 'orgadmin@alphai.com', role: 'org_admin', avatar: 'OA', avatarColor: '#3B82F6', department: 'Executive', jobTitle: 'Organization Admin' },
  { id: 'ha1', name: 'HR Admin', email: 'hradmin@alphai.com', role: 'hr_admin', avatar: 'HA', avatarColor: '#EC4899', department: 'HR', jobTitle: 'HR Manager' },
  { id: 'mg1', name: 'Manager User', email: 'manager@alphai.com', role: 'manager', avatar: 'MU', avatarColor: '#F59E0B', department: 'Engineering', jobTitle: 'Engineering Manager' },
  { id: 'em1', name: 'Employee User', email: 'employee@alphai.com', role: 'employee', avatar: 'EU', avatarColor: '#10B981', department: 'Engineering', jobTitle: 'Software Engineer' },
  { id: 'sc1', name: 'Security Analyst', email: 'security@alphai.com', role: 'security_analyst', avatar: 'SC', avatarColor: '#EF4444', department: 'Security', jobTitle: 'Security Analyst' },
  { id: 'it1', name: 'IT Admin', email: 'itadmin@alphai.com', role: 'it_admin', avatar: 'IT', avatarColor: '#06B6D4', department: 'IT', jobTitle: 'IT Administrator' },
  { id: 'au1', name: 'Auditor', email: 'auditor@alphai.com', role: 'auditor', avatar: 'AU', avatarColor: '#8B5CF6', department: 'Compliance', jobTitle: 'Compliance Auditor' },
];

const DEFAULT_PASSWORD = 'alphai123';
const AUTH_KEY = `${STORAGE_PREFIX}current_user`;

interface AuthContextType {
  user: AuthUser | null;
  login: (email: string, password: string) => { success: boolean; error?: string };
  logout: () => void;
  allUsers: AuthUser[];
}

const AuthContext = createContext<AuthContextType | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [hydrated, setHydrated] = useState(false);

  useEffect(() => {
    const stored = localStorage.getItem(AUTH_KEY);
    setUser(stored ? JSON.parse(stored) : null);
    setHydrated(true);
  }, []);

  const login = (email: string, password: string) => {
    if (password !== DEFAULT_PASSWORD) {
      return { success: false, error: 'Invalid password' };
    }
    const found = FIXED_USERS.find(u => u.email.toLowerCase() === email.toLowerCase());
    if (!found) {
      return { success: false, error: 'User not found' };
    }
    setUser(found);
    localStorage.setItem(AUTH_KEY, JSON.stringify(found));
    return { success: true };
  };

  const logout = () => {
    setUser(null);
    localStorage.removeItem(AUTH_KEY);
  };

  return (
    <AuthContext.Provider value={{ user, login, logout, allUsers: FIXED_USERS }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be inside AuthProvider');
  return ctx;
}

export function getRoleName(role: UserRole): string {
  const names: Record<UserRole, string> = {
    super_admin: 'Super Admin',
    org_admin: 'Org Admin',
    hr_admin: 'HR Admin',
    manager: 'Manager',
    employee: 'Employee',
    security_analyst: 'Security Analyst',
    it_admin: 'IT Admin',
    auditor: 'Auditor',
  };
  return names[role];
}
