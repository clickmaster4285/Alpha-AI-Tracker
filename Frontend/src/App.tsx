import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter, Route, Routes, Navigate } from "react-router-dom";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { Toaster } from "@/components/ui/toaster";
import { TooltipProvider } from "@/components/ui/tooltip";
import { useEffect } from "react";
import { initializeData } from "@/lib/store";
import { AuthProvider, useAuth } from "@/lib/auth";
import { PermissionsProvider } from "@/lib/permissions";

import AppLayout from "@/components/layout/AppLayout";
import ProtectedRoute from "@/components/layout/ProtectedRoute";
import LoginPage from "@/pages/LoginPage";
import ForgotPassword from "@/pages/ForgotPassword";
import ResetPassword from "@/pages/ResetPassword";
import MFAVerification from "@/pages/MFAVerification";
import Dashboard from "@/pages/Dashboard";
import UsersList from "@/pages/UsersList";
import UserActivity from "@/pages/UserActivity";
import AppsAndWebsites from "@/pages/AppsAndWebsites";
import Screenshots from "@/pages/Screenshots";
import UserInsights from "@/pages/UserInsights";
import GraphicalLogs from "@/pages/GraphicalLogs";
import ComprehensiveLogs from "@/pages/ComprehensiveLogs";
import ProductivityChart from "@/pages/ProductivityChart";
import ActivityChart from "@/pages/ActivityChart";
import Departments from "@/pages/Departments";
import KPIsAndKRAs from "@/pages/KPIsAndKRAs";
import RolesPage from "@/pages/RolesPage";
import LiveStream from "@/pages/LiveStream";
import EmailsAndAlerts from "@/pages/EmailsAndAlerts";
import ProjectsPage from "@/pages/ProjectsPage";
import AISummary from "@/pages/AISummary";
import HoursInsights from "@/pages/HoursInsights";
import SettingsPage from "@/pages/SettingsPage";
import TrackingSettings from "@/pages/TrackingSettings";
import OnboardingPage from "@/pages/OnboardingPage";
import EmployeePortal from "@/pages/EmployeePortal";
import TimesheetsPage from "@/pages/TimesheetsPage";
import AttendancePage from "@/pages/AttendancePage";
import ShiftManagement from "@/pages/ShiftManagement";
import GPSLocationPage from "@/pages/GPSLocationPage";
import ProductivityScoringPage from "@/pages/ProductivityScoringPage";
import GoalsPage from "@/pages/GoalsPage";
import ReportsPage from "@/pages/ReportsPage";
import AuditLogPage from "@/pages/AuditLogPage";
import ExecutiveDashboard from "@/pages/ExecutiveDashboard";
import DLPAlertsPage from "@/pages/DLPAlertsPage";
import DLPRulesPage from "@/pages/DLPRulesPage";
import ShadowITPage from "@/pages/ShadowITPage";
import SettingsBillingPage from "@/pages/SettingsBillingPage";
import SettingsCompliancePage from "@/pages/SettingsCompliancePage";
import SettingsSecurityPage from "@/pages/SettingsSecurityPage";
import SettingsNotificationsPage from "@/pages/SettingsNotificationsPage";
import SettingsUserManagementPage from "@/pages/SettingsUserManagementPage";
import PermissionManagement from "@/pages/PermissionManagement";
import NotFound from "./pages/NotFound";

const queryClient = new QueryClient();

function AppRoutes() {
  const { user } = useAuth();

  useEffect(() => {
    initializeData();
  }, []);

  return (
    <Routes>
      <Route path="/login" element={user ? <Navigate to="/" replace /> : <LoginPage />} />
      <Route path="/forgot-password" element={<ForgotPassword />} />
      <Route path="/reset-password" element={<ResetPassword />} />
      <Route path="/mfa" element={<MFAVerification />} />
      <Route element={<ProtectedRoute><AppLayout /></ProtectedRoute>}>
        <Route path="/" element={<ProtectedRoute module="dashboard"><Dashboard /></ProtectedRoute>} />
        <Route path="/users" element={<ProtectedRoute module="users"><UsersList /></ProtectedRoute>} />
        <Route path="/users/activity" element={<ProtectedRoute module="users/activity"><UserActivity /></ProtectedRoute>} />
        <Route path="/apps" element={<ProtectedRoute module="apps"><AppsAndWebsites /></ProtectedRoute>} />
        <Route path="/screenshots" element={<ProtectedRoute module="screenshots"><Screenshots /></ProtectedRoute>} />
        <Route path="/logs/insights" element={<ProtectedRoute module="logs"><UserInsights /></ProtectedRoute>} />
        <Route path="/logs/graphical" element={<ProtectedRoute module="logs"><GraphicalLogs /></ProtectedRoute>} />
        <Route path="/logs/comprehensive" element={<ProtectedRoute module="logs"><ComprehensiveLogs /></ProtectedRoute>} />
        <Route path="/charts/productivity" element={<ProtectedRoute module="charts"><ProductivityChart /></ProtectedRoute>} />
        <Route path="/charts/activity" element={<ProtectedRoute module="charts"><ActivityChart /></ProtectedRoute>} />
        <Route path="/departments" element={<ProtectedRoute module="departments"><Departments /></ProtectedRoute>} />
        <Route path="/kpis" element={<ProtectedRoute module="kpis"><KPIsAndKRAs /></ProtectedRoute>} />
        <Route path="/roles" element={<ProtectedRoute module="roles"><RolesPage /></ProtectedRoute>} />
        <Route path="/live-stream" element={<ProtectedRoute module="live-stream"><LiveStream /></ProtectedRoute>} />
        <Route path="/emails" element={<ProtectedRoute module="emails"><EmailsAndAlerts /></ProtectedRoute>} />
        <Route path="/projects" element={<ProtectedRoute module="projects"><ProjectsPage /></ProtectedRoute>} />
        <Route path="/ai-summary" element={<ProtectedRoute module="ai-summary"><AISummary /></ProtectedRoute>} />
        <Route path="/hours-insights" element={<ProtectedRoute module="hours-insights"><HoursInsights /></ProtectedRoute>} />
        <Route path="/onboarding" element={<ProtectedRoute module="onboarding"><OnboardingPage /></ProtectedRoute>} />
        <Route path="/employee-portal" element={<ProtectedRoute module="employee-portal"><EmployeePortal /></ProtectedRoute>} />
        <Route path="/timesheets" element={<ProtectedRoute module="timesheets"><TimesheetsPage /></ProtectedRoute>} />
        <Route path="/attendance" element={<ProtectedRoute module="attendance"><AttendancePage /></ProtectedRoute>} />
        <Route path="/shifts" element={<ProtectedRoute module="shifts"><ShiftManagement /></ProtectedRoute>} />
        <Route path="/gps-location" element={<ProtectedRoute module="gps-location"><GPSLocationPage /></ProtectedRoute>} />
        <Route path="/productivity-scoring" element={<ProtectedRoute module="productivity-scoring"><ProductivityScoringPage /></ProtectedRoute>} />
        <Route path="/goals" element={<ProtectedRoute module="goals"><GoalsPage /></ProtectedRoute>} />
        <Route path="/reports" element={<ProtectedRoute module="reports"><ReportsPage /></ProtectedRoute>} />
        <Route path="/audit-log" element={<ProtectedRoute module="audit-log"><AuditLogPage /></ProtectedRoute>} />
        <Route path="/executive-dashboard" element={<ProtectedRoute module="executive-dashboard"><ExecutiveDashboard /></ProtectedRoute>} />
        <Route path="/dlp-alerts" element={<ProtectedRoute module="dlp-alerts"><DLPAlertsPage /></ProtectedRoute>} />
        <Route path="/dlp-rules" element={<ProtectedRoute module="dlp-rules"><DLPRulesPage /></ProtectedRoute>} />
        <Route path="/shadow-it" element={<ProtectedRoute module="shadow-it"><ShadowITPage /></ProtectedRoute>} />
        <Route path="/settings" element={<ProtectedRoute module="settings"><SettingsPage /></ProtectedRoute>} />
        <Route path="/settings/tracking" element={<ProtectedRoute module="settings/tracking"><TrackingSettings /></ProtectedRoute>} />
        <Route path="/settings/billing" element={<ProtectedRoute module="settings/billing"><SettingsBillingPage /></ProtectedRoute>} />
        <Route path="/settings/compliance" element={<ProtectedRoute module="settings/compliance"><SettingsCompliancePage /></ProtectedRoute>} />
        <Route path="/settings/security" element={<ProtectedRoute module="settings/security"><SettingsSecurityPage /></ProtectedRoute>} />
        <Route path="/settings/notifications" element={<ProtectedRoute module="settings/notifications"><SettingsNotificationsPage /></ProtectedRoute>} />
        <Route path="/settings/user-management" element={<ProtectedRoute module="settings/user-management"><SettingsUserManagementPage /></ProtectedRoute>} />
        <Route path="/settings/permissions" element={<ProtectedRoute module="settings"><PermissionManagement /></ProtectedRoute>} />
      </Route>
      <Route path="*" element={<NotFound />} />
    </Routes>
  );
}

const App = () => {
  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <Toaster />
        <Sonner />
        <AuthProvider>
          <PermissionsProvider>
            <BrowserRouter>
              <AppRoutes />
            </BrowserRouter>
          </PermissionsProvider>
        </AuthProvider>
      </TooltipProvider>
    </QueryClientProvider>
  );
};

export default App;
