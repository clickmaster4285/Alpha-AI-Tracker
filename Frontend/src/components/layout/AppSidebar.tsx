import { useState, useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { APP_SHORT_NAME } from '@/config';
import { motion, AnimatePresence } from 'framer-motion';
import {
  LayoutDashboard, Users, AppWindow, Camera, FileText, BarChart3,
  Building2, Target, Shield, Radio, Mail, FolderKanban, Sparkles,
  Clock, Settings, ChevronLeft, ChevronDown, ChevronRight, X,
  ClipboardList, UserCheck, MapPin, Trophy, FileBarChart, AlertTriangle,
  Eye, Briefcase, CalendarDays, Navigation
} from 'lucide-react';
import { useAuth } from '@/lib/auth';
import { usePermissions } from '@/lib/permissions';

interface NavItem {
  label: string;
  icon: React.ElementType;
  path?: string;
  module?: string;
  children?: { label: string; path: string; module?: string }[];
}

const navItems: NavItem[] = [
  { label: 'Dashboard', icon: LayoutDashboard, path: '/', module: 'dashboard' },
  { label: 'Employee Portal', icon: Briefcase, path: '/employee-portal', module: 'employee-portal' },
  { label: 'HR', icon: Users, children: [
    { label: 'List of Users', path: '/users', module: 'users' },
    { label: 'User Activity Status', path: '/users/activity', module: 'users/activity' },
    { label: 'Departments', path: '/departments', module: 'departments' },
    { label: 'Roles', path: '/roles', module: 'roles' },
    { label: 'KPIs & KRAs', path: '/kpis', module: 'kpis' },
    { label: 'Onboarding', path: '/onboarding', module: 'onboarding' },
    { label: 'Shift Management', path: '/shifts', module: 'shifts' },
  ]},
  { label: 'Time & Attendance', icon: CalendarDays, children: [
    { label: 'Timesheets', path: '/timesheets', module: 'timesheets' },
    { label: 'Attendance Log', path: '/attendance', module: 'attendance' },
    { label: 'GPS & Location', path: '/gps-location', module: 'gps-location' },
    { label: 'Hours Insights', path: '/hours-insights', module: 'hours-insights' },
  ]},
  { label: 'Productivity', icon: Trophy, children: [
    { label: 'Score Card', path: '/productivity-scoring', module: 'productivity-scoring' },
    { label: 'Goals & OKRs', path: '/goals', module: 'goals' },
  ]},
  { label: 'Apps and Websites', icon: AppWindow, path: '/apps', module: 'apps' },
  { label: 'Screenshots', icon: Camera, path: '/screenshots', module: 'screenshots' },
  { label: 'Logs', icon: FileText, children: [
    { label: 'User Insights', path: '/logs/insights', module: 'logs' },
    { label: 'Graphical Logs', path: '/logs/graphical', module: 'logs' },
    { label: 'Comprehensive Logs', path: '/logs/comprehensive', module: 'logs' },
  ]},
  { label: 'Charts', icon: BarChart3, children: [
    { label: 'Productivity Chart', path: '/charts/productivity', module: 'charts' },
    { label: 'Activity Chart', path: '/charts/activity', module: 'charts' },
  ]},
  { label: 'Reports & Analytics', icon: FileBarChart, children: [
    { label: 'Reports', path: '/reports', module: 'reports' },
    { label: 'Audit Log', path: '/audit-log', module: 'audit-log' },
    { label: 'Executive Dashboard', path: '/executive-dashboard', module: 'executive-dashboard' },
  ]},
  { label: 'Security & DLP', icon: AlertTriangle, children: [
    { label: 'DLP Alerts', path: '/dlp-alerts', module: 'dlp-alerts' },
    { label: 'DLP Rules', path: '/dlp-rules', module: 'dlp-rules' },
    { label: 'Shadow IT', path: '/shadow-it', module: 'shadow-it' },
  ]},
  { label: 'Live Stream', icon: Radio, path: '/live-stream', module: 'live-stream' },
  { label: 'Emails & Alerts', icon: Mail, path: '/emails', module: 'emails' },
  { label: 'Projects', icon: FolderKanban, path: '/projects', module: 'projects' },
  { label: 'AI Summary', icon: Sparkles, path: '/ai-summary', module: 'ai-summary' },
  { label: 'Settings', icon: Settings, children: [
    { label: 'General', path: '/settings', module: 'settings' },
    { label: 'Permissions', path: '/settings/permissions', module: 'settings' },
    { label: 'Tracking', path: '/settings/tracking', module: 'settings/tracking' },
    { label: 'User Management', path: '/settings/user-management', module: 'settings/user-management' },
    { label: 'Notifications', path: '/settings/notifications', module: 'settings/notifications' },
    { label: 'Billing', path: '/settings/billing', module: 'settings/billing' },
    { label: 'Compliance', path: '/settings/compliance', module: 'settings/compliance' },
    { label: 'Security', path: '/settings/security', module: 'settings/security' },
  ]},
];

interface AppSidebarProps {
  collapsed: boolean;
  onToggle: () => void;
  mobileOpen: boolean;
  onMobileClose: () => void;
}

export default function AppSidebar({ collapsed, onToggle, mobileOpen, onMobileClose }: AppSidebarProps) {
  const location = useLocation();
  // keep submenus closed by default; on login we'll reset explicitly
  const [openMenus, setOpenMenus] = useState<string[]>([]);
  const { user } = useAuth();
  const { canAccess } = usePermissions();

  // when a user logs in (or changes) collapse all menus
  useEffect(() => {
    if (user) {
      setOpenMenus([]);
    }
  }, [user]);

  const toggleMenu = (label: string) => {
    setOpenMenus(prev => prev.includes(label) ? prev.filter(l => l !== label) : [...prev, label]);
  };

  const isActive = (path?: string, children?: { path: string }[]) => {
    if (path) return location.pathname === path;
    return children?.some(c => location.pathname === c.path);
  };

  const filteredItems = user ? navItems.filter(item => {
    if (item.module) return canAccess(user.role, item.module);
    if (item.children) return item.children.some(c => c.module ? canAccess(user.role, c.module) : true);
    return true;
  }) : [];

  const sidebarContent = (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-3 px-5 py-5 border-b border-sidebar-border">
        {/* logo image; put your logo in public/app-logo.png or change the src to wherever you store it */}
        <div className="w-9 h-9 rounded-lg overflow-hidden flex items-center justify-center flex-shrink-0 bg-white">
          <img src="/app-logo.png" alt="Alpha AI Tracking logo" className="app-logo" />
        </div>
        {!collapsed && (
          <motion.div initial={{ opacity: 0 }} animate={{ opacity: 1 }} className="flex flex-col">
            <span className="font-display font-bold text-sm text-sidebar-foreground tracking-tight leading-tight">{APP_SHORT_NAME}</span>
            <span className="font-display font-bold text-[10px] text-sidebar-foreground/70 tracking-tight leading-tight">Monitoring & Productivity System</span>
          </motion.div>
        )}
      </div>

      <nav className="flex-1 overflow-y-auto py-3 px-3 space-y-0.5">
        {filteredItems.map(item => {
          const Icon = item.icon;
          const active = isActive(item.path, item.children);
          const isOpen = openMenus.includes(item.label);

          if (item.children) {
            const visibleChildren = user ? item.children.filter(c => c.module ? canAccess(user.role, c.module) : true) : [];
            if (visibleChildren.length === 0) return null;

            return (
              <div key={item.label}>
                <button
                  onClick={() => toggleMenu(item.label)}
                  className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm transition-all duration-200
                    ${active ? 'bg-sidebar-accent text-sidebar-primary' : 'text-sidebar-foreground hover:bg-sidebar-accent/50'}`}
                >
                  <Icon className="w-[18px] h-[18px] flex-shrink-0" />
                  {!collapsed && (
                    <>
                      <span className="flex-1 text-left font-medium">{item.label}</span>
                      <ChevronDown className={`w-4 h-4 transition-transform ${isOpen ? 'rotate-180' : ''}`} />
                    </>
                  )}
                </button>
                <AnimatePresence>
                  {isOpen && !collapsed && (
                    <motion.div
                      initial={{ height: 0, opacity: 0 }}
                      animate={{ height: 'auto', opacity: 1 }}
                      exit={{ height: 0, opacity: 0 }}
                      className="overflow-hidden ml-5 mt-0.5 space-y-0.5 border-l-2 border-sidebar-border pl-3"
                    >
                      {visibleChildren.map(child => (
                        <Link
                          key={child.path}
                          to={child.path}
                          onClick={onMobileClose}
                          className={`block px-3 py-2 rounded-md text-sm transition-colors
                            ${location.pathname === child.path ? 'text-sidebar-primary font-semibold bg-sidebar-accent/60' : 'text-sidebar-foreground/70 hover:text-sidebar-foreground hover:bg-sidebar-accent/30'}`}
                        >
                          {child.label}
                        </Link>
                      ))}
                    </motion.div>
                  )}
                </AnimatePresence>
              </div>
            );
          }

          return (
            <Link
              key={item.label}
              to={item.path!}
              onClick={onMobileClose}
              className={`flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm transition-all duration-200
                ${active ? 'bg-sidebar-accent text-sidebar-primary font-semibold' : 'text-sidebar-foreground hover:bg-sidebar-accent/50'}`}
            >
              <Icon className="w-[18px] h-[18px] flex-shrink-0" />
              {!collapsed && <span className="font-medium">{item.label}</span>}
            </Link>
          );
        })}
      </nav>

      <div className="hidden lg:block border-t border-sidebar-border p-3">
        <button onClick={onToggle} className="w-full flex items-center justify-center py-2 rounded-lg hover:bg-sidebar-accent/50 text-sidebar-foreground transition-colors">
          {collapsed ? <ChevronRight className="w-5 h-5" /> : <ChevronLeft className="w-5 h-5" />}
        </button>
      </div>
    </div>
  );

  return (
    <>
      <aside className={`hidden lg:flex flex-col bg-sidebar shadow-sidebar transition-all duration-300 ${collapsed ? 'w-[70px]' : 'w-[260px]'} h-screen sticky top-0 z-30`}>
        {sidebarContent}
      </aside>
      <AnimatePresence>
        {mobileOpen && (
          <>
            <motion.div initial={{ opacity: 0 }} animate={{ opacity: 0.5 }} exit={{ opacity: 0 }} className="fixed inset-0 bg-foreground/50 z-40 lg:hidden" onClick={onMobileClose} />
            <motion.aside initial={{ x: -280 }} animate={{ x: 0 }} exit={{ x: -280 }} transition={{ type: 'spring', damping: 25 }} className="fixed left-0 top-0 w-[260px] h-screen bg-sidebar z-50 lg:hidden">
              <button onClick={onMobileClose} className="absolute top-4 right-4 text-sidebar-foreground">
                <X className="w-5 h-5" />
              </button>
              {sidebarContent}
            </motion.aside>
          </>
        )}
      </AnimatePresence>
    </>
  );
}
