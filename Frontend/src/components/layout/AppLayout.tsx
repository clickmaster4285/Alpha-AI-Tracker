import { useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { APP_NAME } from '@/config';
import AppSidebar from './AppSidebar';
import TopBar from './TopBar';

const pageTitles: Record<string, string> = {
  '/': 'Dashboard',
  '/users': 'Users',
  '/users/activity': 'User Activity Status',
  '/apps': 'Apps and Websites',
  '/screenshots': 'Screenshots',
  '/logs/insights': 'User Insights',
  '/logs/graphical': 'Graphical Logs',
  '/logs/comprehensive': 'Comprehensive Logs',
  '/charts/productivity': 'Productivity Chart',
  '/charts/activity': 'Activity Chart',
  '/departments': 'Departments',
  '/kpis': 'KPIs & KRAs',
  '/roles': 'Roles',
  '/live-stream': 'Live Stream',
  '/emails': 'Emails & Alerts',
  '/projects': 'Projects',
  '/ai-summary': 'AI Summary',
  '/hours-insights': 'Hours Insights',
  '/settings': 'Settings',
  '/settings/tracking': 'Tracking Settings',
  '/onboarding': 'Onboarding',
  '/employee-portal': 'Employee Portal',
  '/timesheets': 'Timesheets',
  '/attendance': 'Attendance Log',
  '/shifts': 'Shift Management',
  '/gps-location': 'GPS & Location',
  '/productivity-scoring': 'Productivity Scoring',
  '/goals': 'Goals & OKRs',
  '/reports': 'Reports & Analytics',
  '/audit-log': 'Audit Log',
  '/executive-dashboard': 'Executive Dashboard',
  '/dlp-alerts': 'DLP Alerts',
  '/dlp-rules': 'DLP Rules',
  '/shadow-it': 'Shadow IT',
  '/settings/billing': 'Billing & Subscription',
  '/settings/compliance': 'GDPR & Compliance',
  '/settings/security': 'Security Settings',
  '/settings/notifications': 'Notification Config',
  '/settings/user-management': 'User Management',
  '/settings/permissions': 'Permission Management',
};

export default function AppLayout() {
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();

  const title = pageTitles[location.pathname] || APP_NAME;

  return (
    <div className="flex h-screen overflow-hidden bg-background">
      <AppSidebar
        collapsed={collapsed}
        onToggle={() => setCollapsed(!collapsed)}
        mobileOpen={mobileOpen}
        onMobileClose={() => setMobileOpen(false)}
      />
      <div className="flex-1 flex flex-col overflow-hidden">
        <TopBar title={title} onMenuClick={() => setMobileOpen(true)} />
        <main className="flex-1 overflow-y-auto p-4 lg:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
