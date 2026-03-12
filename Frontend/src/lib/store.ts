// LocalStorage-based data store for the application
// keys are prefixed via STORAGE_PREFIX to keep them unique

export interface Employee {
  id: string;
  name: string;
  email: string;
  employeeId: string;
  department: string;
  role: string;
  trackingEnabled: boolean;
  trackingStatus: 'tracked' | 'untracked';
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  shift: string;
}

export interface ActivityLog {
  id: string;
  employeeId: string;
  employeeName: string;
  date: string;
  application: string;
  tabs: { name: string; duration: string }[];
}

export interface SystemLog {
  id: string;
  employeeId: string;
  date: string;
  type: 'charging' | 'lock' | 'suspend' | 'status';
  startTime: string;
  endTime: string;
  duration: string;
  status?: string;
}

export interface ProductivityEntry {
  id: string;
  employeeId: string;
  date: string;
  application: string;
  category: 'productive' | 'unproductive' | 'neutral';
  tabs: { name: string; duration: string }[];
  totalDuration: string;
}

export interface Screenshot {
  id: string;
  employeeId: string;
  employeeName: string;
  department: string;
  application: string;
  timestamp: string;
  time: string;
  date: string;
}

export interface DashboardStats {
  totalEmployees: number;
  trackedCount: number;
  untrackedCount: number;
  totalIdleTime: string;
  totalProductiveHours: string;
  totalUnproductiveHours: string;
  idleChange: number;
  productiveChange: number;
  unproductiveChange: number;
}

export interface UserActivityStatus {
  id: string;
  srNo: number;
  userName: string;
  lastClockIn: string;
  lastClockOut: string;
  totalTime: string;
  totalProductiveTime: string;
  totalExtraTime: string;
}

import { STORAGE_PREFIX } from '@/config';

const STORAGE_KEYS = {
  EMPLOYEES: `${STORAGE_PREFIX}employees`,
  ACTIVITY_LOGS: `${STORAGE_PREFIX}activity_logs`,
  SYSTEM_LOGS: `${STORAGE_PREFIX}system_logs`,
  PRODUCTIVITY: `${STORAGE_PREFIX}productivity`,
  SCREENSHOTS: `${STORAGE_PREFIX}screenshots`,
  SETTINGS: `${STORAGE_PREFIX}settings`,
  DEPARTMENTS: `${STORAGE_PREFIX}departments`,
  INITIALIZED: `${STORAGE_PREFIX}initialized`,
};

const AVATAR_COLORS = [
  '#7C3AED', '#EC4899', '#F59E0B', '#10B981', '#3B82F6', '#EF4444', '#8B5CF6', '#06B6D4', '#F97316', '#14B8A6'
];

const DEPARTMENTS = ['Engineering', 'Design', 'Marketing', 'Sales', 'HR', 'Finance', 'QA', 'DevOps'];

const APPLICATIONS = [
  'Visual Studio Code', 'Google Chrome', 'Microsoft Teams', 'Slack', 'Figma', 
  'Postman', 'Terminal', 'GitHub Desktop', 'Notion', 'Calculator', 'Spotify'
];

const PRODUCTIVE_APPS = ['Visual Studio Code', 'Figma', 'Postman', 'Terminal', 'GitHub Desktop', 'Notion'];
const UNPRODUCTIVE_APPS = ['Spotify', 'Calculator'];

function generateId() {
  return Math.random().toString(36).substring(2, 11);
}

function getInitials(name: string) {
  return name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2);
}

function randomTime() {
  const h = Math.floor(Math.random() * 12) + 1;
  const m = Math.floor(Math.random() * 60);
  const s = Math.floor(Math.random() * 60);
  const ampm = Math.random() > 0.5 ? 'AM' : 'PM';
  return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')} ${ampm}`;
}

function randomDuration() {
  const h = Math.floor(Math.random() * 8);
  const m = Math.floor(Math.random() * 60);
  if (h > 0) return `${h} hrs ${m} min`;
  return `${m} min`;
}

const SAMPLE_EMPLOYEES: Employee[] = [
  { id: '1', name: 'Yashodhan Kalia', email: 'yashodhan@company.com', employeeId: '28393', department: 'Engineering', role: 'Employee', trackingEnabled: true, trackingStatus: 'tracked', isOnline: true, avatar: 'YK', avatarColor: AVATAR_COLORS[0], shift: 'Day' },
  { id: '2', name: 'Stuti Srivastava', email: 'stuti@company.com', employeeId: '1549', department: 'Design', role: 'Employee', trackingEnabled: true, trackingStatus: 'untracked', isOnline: false, avatar: 'SS', avatarColor: AVATAR_COLORS[1], shift: 'Day' },
  { id: '3', name: 'Rakesh Pathania', email: 'rakesh@company.com', employeeId: '1597', department: 'Engineering', role: 'Employee', trackingEnabled: true, trackingStatus: 'tracked', isOnline: true, avatar: 'RP', avatarColor: AVATAR_COLORS[2], shift: 'Day' },
  { id: '4', name: 'Kamal Dhami', email: 'kamal@company.com', employeeId: '0007', department: 'QA', role: 'Employee', trackingEnabled: true, trackingStatus: 'untracked', isOnline: false, avatar: 'KD', avatarColor: AVATAR_COLORS[3], shift: 'Night' },
  { id: '5', name: 'Tarun Saini', email: 'tarun@company.com', employeeId: '1490', department: 'DevOps', role: 'Employee', trackingEnabled: true, trackingStatus: 'untracked', isOnline: false, avatar: 'TS', avatarColor: AVATAR_COLORS[4], shift: 'Day' },
  { id: '6', name: 'Arush Sharma', email: 'arush@company.com', employeeId: '1491', department: 'Engineering', role: 'Employee', trackingEnabled: true, trackingStatus: 'tracked', isOnline: true, avatar: 'AS', avatarColor: AVATAR_COLORS[5], shift: 'Day' },
  { id: '7', name: 'Muskaan Makkad', email: 'muskaan@company.com', employeeId: '1405', department: 'Marketing', role: 'Employee', trackingEnabled: true, trackingStatus: 'untracked', isOnline: false, avatar: 'MM', avatarColor: AVATAR_COLORS[6], shift: 'Day' },
  { id: '8', name: 'Savi Chopra', email: 'savi@company.com', employeeId: '1627', department: 'HR', role: 'Employee', trackingEnabled: false, trackingStatus: 'untracked', isOnline: false, avatar: 'SC', avatarColor: AVATAR_COLORS[7], shift: 'Night' },
  { id: '9', name: 'Anisha Jassal', email: 'anisha@company.com', employeeId: '1501', department: 'Finance', role: 'Employee', trackingEnabled: false, trackingStatus: 'untracked', isOnline: false, avatar: 'AJ', avatarColor: AVATAR_COLORS[8], shift: 'Day' },
  { id: '10', name: 'Salman Hussain', email: 'salman@company.com', employeeId: '1272', department: 'Engineering', role: 'Employee', trackingEnabled: true, trackingStatus: 'tracked', isOnline: true, avatar: 'SH', avatarColor: AVATAR_COLORS[9], shift: 'Day' },
  { id: '11', name: 'Priya Mehta', email: 'priya@company.com', employeeId: '1633', department: 'Design', role: 'Manager', trackingEnabled: true, trackingStatus: 'tracked', isOnline: true, avatar: 'PM', avatarColor: AVATAR_COLORS[1], shift: 'Day' },
  { id: '12', name: 'Ravi Kumar', email: 'ravi@company.com', employeeId: '1644', department: 'Sales', role: 'Employee', trackingEnabled: true, trackingStatus: 'untracked', isOnline: false, avatar: 'RK', avatarColor: AVATAR_COLORS[3], shift: 'Day' },
];

function generateActivityLogs(): ActivityLog[] {
  const logs: ActivityLog[] = [];
  SAMPLE_EMPLOYEES.forEach(emp => {
    APPLICATIONS.slice(0, 5).forEach(app => {
      logs.push({
        id: generateId(),
        employeeId: emp.id,
        employeeName: emp.name,
        date: '2026-03-02',
        application: app,
        tabs: [
          { name: `${app} — main.ts`, duration: `${Math.floor(Math.random() * 30) + 1} min ${Math.floor(Math.random() * 60)} sec` },
          { name: `${app} — index.tsx`, duration: `${Math.floor(Math.random() * 15) + 1} min ${Math.floor(Math.random() * 60)} sec` },
        ]
      });
    });
  });
  return logs;
}

function generateSystemLogs(): SystemLog[] {
  const logs: SystemLog[] = [];
  SAMPLE_EMPLOYEES.slice(0, 6).forEach(emp => {
    logs.push(
      { id: generateId(), employeeId: emp.id, date: '2026-03-02', type: 'charging', startTime: randomTime(), endTime: randomTime(), duration: randomDuration() },
      { id: generateId(), employeeId: emp.id, date: '2026-03-02', type: 'lock', startTime: randomTime(), endTime: randomTime(), duration: randomDuration() },
      { id: generateId(), employeeId: emp.id, date: '2026-03-02', type: 'suspend', startTime: randomTime(), endTime: randomTime(), duration: randomDuration() },
      { id: generateId(), employeeId: emp.id, date: '2026-03-02', type: 'status', startTime: '', endTime: '', duration: '', status: Math.random() > 0.5 ? 'Active' : 'Locked' },
    );
  });
  return logs;
}

function generateProductivity(): ProductivityEntry[] {
  const entries: ProductivityEntry[] = [];
  SAMPLE_EMPLOYEES.forEach(emp => {
    APPLICATIONS.forEach(app => {
      const cat = PRODUCTIVE_APPS.includes(app) ? 'productive' : UNPRODUCTIVE_APPS.includes(app) ? 'unproductive' : 'neutral';
      entries.push({
        id: generateId(),
        employeeId: emp.id,
        date: '2026-03-02',
        application: app,
        category: cat as 'productive' | 'unproductive' | 'neutral',
        tabs: [
          { name: `Tab 1 - ${app}`, duration: `${Math.floor(Math.random() * 20) + 1} min` },
        ],
        totalDuration: randomDuration(),
      });
    });
  });
  return entries;
}

function generateScreenshots(): Screenshot[] {
  const screenshots: Screenshot[] = [];
  SAMPLE_EMPLOYEES.slice(0, 6).forEach(emp => {
    for (let i = 0; i < 4; i++) {
      const h = 9 + Math.floor(Math.random() * 8);
      const m = Math.floor(Math.random() * 60);
      screenshots.push({
        id: generateId(),
        employeeId: emp.id,
        employeeName: emp.name,
        department: emp.department,
        application: APPLICATIONS[Math.floor(Math.random() * APPLICATIONS.length)],
        timestamp: `2026-03-02T${h}:${m.toString().padStart(2, '0')}:00`,
        time: `${h}:${m.toString().padStart(2, '0')}`,
        date: '2026-03-02',
      });
    }
  });
  return screenshots;
}

export function initializeData() {
  if (localStorage.getItem(STORAGE_KEYS.INITIALIZED)) return;
  
  localStorage.setItem(STORAGE_KEYS.EMPLOYEES, JSON.stringify(SAMPLE_EMPLOYEES));
  localStorage.setItem(STORAGE_KEYS.ACTIVITY_LOGS, JSON.stringify(generateActivityLogs()));
  localStorage.setItem(STORAGE_KEYS.SYSTEM_LOGS, JSON.stringify(generateSystemLogs()));
  localStorage.setItem(STORAGE_KEYS.PRODUCTIVITY, JSON.stringify(generateProductivity()));
  localStorage.setItem(STORAGE_KEYS.SCREENSHOTS, JSON.stringify(generateScreenshots()));
  localStorage.setItem(STORAGE_KEYS.DEPARTMENTS, JSON.stringify(DEPARTMENTS));
  localStorage.setItem(STORAGE_KEYS.SETTINGS, JSON.stringify({
    screenshotTime: 5,
    appTime: 5,
    geoLocationTime: 5,
    systemStatusTime: 10,
    maxIdleTime: 5,
    offlineTime: 60,
    blurImage: false,
    appVisibility: 'visible',
  }));
  localStorage.setItem(STORAGE_KEYS.INITIALIZED, 'true');
}

export function getEmployees(): Employee[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.EMPLOYEES) || '[]');
}

export function saveEmployees(employees: Employee[]) {
  localStorage.setItem(STORAGE_KEYS.EMPLOYEES, JSON.stringify(employees));
}

export function addEmployee(emp: Omit<Employee, 'id' | 'avatar' | 'avatarColor'>) {
  const employees = getEmployees();
  const newEmp: Employee = {
    ...emp,
    id: generateId(),
    avatar: getInitials(emp.name),
    avatarColor: AVATAR_COLORS[Math.floor(Math.random() * AVATAR_COLORS.length)],
  };
  employees.push(newEmp);
  saveEmployees(employees);
  return newEmp;
}

export function deleteEmployee(id: string) {
  const employees = getEmployees().filter(e => e.id !== id);
  saveEmployees(employees);
}

export function getActivityLogs(): ActivityLog[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.ACTIVITY_LOGS) || '[]');
}

export function getSystemLogs(): SystemLog[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.SYSTEM_LOGS) || '[]');
}

export function getProductivity(): ProductivityEntry[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.PRODUCTIVITY) || '[]');
}

export function getScreenshots(): Screenshot[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.SCREENSHOTS) || '[]');
}

export function getDepartments(): string[] {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.DEPARTMENTS) || '[]');
}

export function getSettings() {
  return JSON.parse(localStorage.getItem(STORAGE_KEYS.SETTINGS) || '{}');
}

export function saveSettings(settings: Record<string, unknown>) {
  localStorage.setItem(STORAGE_KEYS.SETTINGS, JSON.stringify(settings));
}

export function getDashboardStats(): DashboardStats {
  const employees = getEmployees();
  const tracked = employees.filter(e => e.trackingStatus === 'tracked').length;
  return {
    totalEmployees: employees.length,
    trackedCount: tracked,
    untrackedCount: employees.length - tracked,
    totalIdleTime: '1 hrs 25 min',
    totalProductiveHours: '6 hrs 12 min',
    totalUnproductiveHours: '4 hrs 25 min',
    idleChange: 100,
    productiveChange: 100,
    unproductiveChange: 100,
  };
}

export function getUserActivityStatuses(): UserActivityStatus[] {
  const employees = getEmployees();
  return employees.map((emp, i) => ({
    id: emp.id,
    srNo: i + 1,
    userName: emp.name,
    lastClockIn: `0${Math.floor(Math.random() * 3) + 2}-03-2026 ${Math.floor(Math.random() * 12) + 1}:${Math.floor(Math.random() * 60).toString().padStart(2, '0')} ${Math.random() > 0.5 ? 'AM' : 'PM'}`,
    lastClockOut: Math.random() > 0.3 ? `0${Math.floor(Math.random() * 3) + 2}-03-2026 ${Math.floor(Math.random() * 12) + 1}:${Math.floor(Math.random() * 60).toString().padStart(2, '0')} ${Math.random() > 0.5 ? 'AM' : 'PM'}` : 'N/A',
    totalTime: `${Math.floor(Math.random() * 150) + 5} hour ${Math.floor(Math.random() * 60)} min`,
    totalProductiveTime: `${Math.floor(Math.random() * 40) + 1} hours ${Math.floor(Math.random() * 60)} mins`,
    totalExtraTime: `${Math.floor(Math.random() * 70) + 1} hours ${Math.floor(Math.random() * 60)} mins`,
  }));
}
