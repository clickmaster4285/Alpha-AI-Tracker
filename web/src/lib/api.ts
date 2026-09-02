// API client for communicating with the Go backend server.
// Uses httpOnly cookies for JWT authentication (set by server).

// Use relative URL through Next.js rewrites proxy. Set NEXT_PUBLIC_API_URL to override.
const API_BASE = process.env.NEXT_PUBLIC_API_URL || '/api/v1';

interface RequestOptions {
  method?: string;
  body?: unknown;
  params?: Record<string, string | number | boolean | undefined>;
  headers?: Record<string, string>;
  /** Skip JSON parse for blob/download responses */
  raw?: boolean;
  /** Internal: this call already went through one refresh-retry cycle */
  _retried?: boolean;
}

class ApiError extends Error {
  status: number;
  detail?: string;

  constructor(message: string, status: number, detail?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
  }
}

// ──────────────────────────
// Access-token refresh (single-flight).
// The auth_token cookie lives ~15 minutes; POST /auth/refresh validates and
// rotates the 30-day refresh_token cookie and re-mints both. Concurrent 401s
// share one refresh; when it fails the session is dead → force back to /login.
// ──────────────────────────
let refreshInFlight: Promise<boolean> | null = null;

async function performRefresh(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE}/auth/refresh`, {
      method: 'POST',
      credentials: 'include',
    });
    return res.ok;
  } catch {
    return false;
  }
}

function forceLoginRedirect() {
  if (typeof window !== 'undefined' && !window.location.pathname.startsWith('/login')) {
    window.location.replace('/login');
  }
}

async function request<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, params, headers = {}, raw = false } = options;

  // Build URL with query params
  let url = `${API_BASE}${endpoint}`;
  if (params) {
    const searchParams = new URLSearchParams();
    Object.entries(params).forEach(([key, value]) => {
      if (value !== undefined && value !== '' && value !== null) {
        searchParams.set(key, String(value));
      }
    });
    const qs = searchParams.toString();
    if (qs) url += `?${qs}`;
  }

  // Build fetch options
  const fetchOptions: RequestInit = {
    method,
    credentials: 'include', // Send cookies (httpOnly JWT cookie)
    headers: {
      'Content-Type': 'application/json',
      ...headers,
    },
  };

  if (body && method !== 'GET') {
    fetchOptions.body = JSON.stringify(body);
  }

  let response = await fetch(url, fetchOptions);

  // Access token expired → try one silent refresh, then replay the request once
  if (
    response.status === 401 &&
    !options._retried &&
    endpoint !== '/auth/login' &&
    endpoint !== '/auth/refresh'
  ) {
    if (!refreshInFlight) {
      refreshInFlight = performRefresh().finally(() => {
        refreshInFlight = null;
      });
    }
    const refreshed = await refreshInFlight;
    if (refreshed) {
      return request<T>(endpoint, { ...options, _retried: true });
    }
    forceLoginRedirect();
  }

  if (!response.ok) {
    let errorData: { message?: string; detail?: string } = {};
    try {
      errorData = await response.json();
    } catch {
      // ignore parse error
    }
    throw new ApiError(
      errorData.message || `Request failed with status ${response.status}`,
      response.status,
      errorData.detail,
    );
  }

  if (raw) return response as unknown as T;

  return response.json();
}

// ──────────────────────────
// Auth API
// ──────────────────────────

export interface AuthUser {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  role: string;
  roleId?: number;
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  /** Granted submodule keys for the user's role (server-driven RBAC). */
  permissions?: string[];
  createdAt: string;
  updatedAt: string;
}

export interface LoginResponse {
  user: AuthUser;
}

// ── Self-service profile (GET /api/v1/auth/profile) ────────────────────────

/** One navigation module surfaced on the profile page, with the count of
 *  granted submodules under it. No hardcoded module names — derived from
 *  the RBAC catalog joined with the user's granted permission keys. */
export interface ProfileModule {
  id: number;
  key: string;
  name: string;
  grantedCount: number;
  submoduleCount: number;
}

/** RBAC view attached to the profile. */
export interface ProfilePermissions {
  submoduleKeys: string[];
  modules: ProfileModule[];
  /** True when the user holds the system `company_admin` role. Drives the
   *  lock messaging in the profile UI. */
  isSystemAdmin: boolean;
}

/** Aggregate profile payload returned by GET /api/v1/auth/profile. The
 *  /settings/profile page renders User, Role, Permissions and Employee
 *  directly from this shape. */
export interface ProfileResponse {
  user: AuthUser;
  role?: {
    id: number;
    name: string;
    description: string;
    isSystem: boolean;
    userCount: number;
    submoduleIds: number[];
    permissions: string[];
  };
  permissions: ProfilePermissions;
  employee?: Employee;
}

export interface AuthCheckResponse {
  authenticated: boolean;
  user?: AuthUser;
}

export const authApi = {
  login: (email: string, password: string) =>
    request<LoginResponse>('/auth/login', {
      method: 'POST',
      body: { email, password },
    }),

  logout: () =>
    request<{ message: string }>('/auth/logout', {
      method: 'POST',
    }),

  me: () => request<AuthUser>('/auth/me'),

  /** Aggregate self-service profile: user + role + RBAC view + linked
   *  employee. Identity is resolved from the httpOnly cookie on the
   *  server. */
  profile: () => request<ProfileResponse>('/auth/profile'),

  check: () => request<AuthCheckResponse>('/auth/check'),

  /** Rotate the refresh_token cookie into a fresh access_token cookie.
   * Resolves true when the session was revived; false when it is unrecoverable. */
  refresh: (): Promise<boolean> => performRefresh(),
};

// ──────────────────────────
// Departments API (dynamic CRUD)
// ──────────────────────────

export interface Department {
  id: number;
  name: string;
  employeeCount: number;
}

export interface DepartmentListResponse {
  departments: Department[];
  total: number;
}

export const departmentsApi = {
  list: () => request<DepartmentListResponse>('/departments'),

  create: (name: string) =>
    request<Department>('/departments', { method: 'POST', body: { name } }),

  update: (id: number, name: string) =>
    request<Department>('/departments/' + id, { method: 'PUT', body: { name } }),

  delete: (id: number) =>
    request<{ message: string }>('/departments/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Employees API
// ──────────────────────────

export interface Employee {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  department: string;
  departmentId: number;
  /** FK to the shifts catalog (null when the employee has no shift). */
  shiftId: number | null;
  /** Resolved shift name (joined from shifts). Empty when shiftId is null. */
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  /** True when a row in the users table exists for this employee's employee_id.
   *  Projected server-side via an indexed EXISTS() so it costs O(1) per row. */
  hasUserLogin: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface EmployeeListResponse {
  data: Employee[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface CreateEmployeePayload {
  name: string;
  email: string;
  departmentId: number;
  /** FK to the shifts catalog. null/undefined → "no shift". */
  shiftId?: number | null;
}

export interface UpdateEmployeePayload {
  name?: string;
  email?: string;
  departmentId?: number;
  /** FK to the shifts catalog. 0 / null / undefined → no change; pass `0`
   *  explicitly to clear an existing assignment (the service maps it to
   *  NULL on the DB). */
  shiftId?: number | null;
  trackingEnabled?: boolean;
  trackingStatus?: string;
  isOnline?: boolean;
}

export interface GenerateSecretResponse {
  secret: string;
  expiresIn: number;
}

export interface ImportEmployeeRow {
  employeeId: string;
  name: string;
  email: string;
  department: string;
  /** Spreadsheet column header. The server resolves the name to a
   *  shifts.id at import time; a blank cell falls back to "Day Shift". */
  shift?: string;
}

export interface ImportRowResult {
  rowIndex: number;
  employeeId: string;
  name: string;
  status: 'imported' | 'updated' | 'skipped';
  reason?: string;
}

export interface ImportEmployeesResponse {
  imported: number;
  updated: number;
  skipped: number;
  results: ImportRowResult[];
}

export interface EmployeeExportRow {
  employeeId: string;
  name: string;
  email: string;
  department: string;
  shift: string;
}

export const employeesApi = {
  list: (params?: { page?: number; perPage?: number; search?: string; department?: string; role?: string; status?: string }) =>
    request<EmployeeListResponse>('/employees', { params: params as Record<string, string | number | undefined> }),

  get: (id: string) => request<Employee>('/employees/' + id),

  // Aggregate machine picture: latest hardware, storage, network, installed apps/packages,
  // peripherals, permission checks, app status and activity stats.
  detail: (id: string) => request<EmployeeDetail>('/employees/' + id + '/detail'),

  create: (data: CreateEmployeePayload) =>
    request<Employee>('/employees', { method: 'POST', body: data }),

  update: (id: string, data: UpdateEmployeePayload) =>
    request<Employee>('/employees/' + id, { method: 'PUT', body: data }),

  delete: (id: string) =>
    request<{ message: string }>('/employees/' + id, { method: 'DELETE' }),

  generateSecret: (id: string) =>
    request<GenerateSecretResponse>('/employees/' + id + '/generate-secret', { method: 'POST' }),

  // Bulk Excel import — the server get-or-creates departments by name and upserts
  // each row by its exact employee_id from the spreadsheet.
  import: (rows: ImportEmployeeRow[]) =>
    request<ImportEmployeesResponse>('/employees/import', { method: 'POST', body: { employees: rows } }),

  // All non-deleted employees as flat rows for the Excel download.
  export: () => request<EmployeeExportRow[]>('/employees/export'),
};

// ──────────────────────────
// Shifts API (relational CRUD)
// ──────────────────────────

export interface Shift {
  id: number;
  name: string;
  /** "HH:MM" 24-hour time. */
  startTime: string;
  endTime: string;
  /** Comma-separated weekday short names (e.g. "Mon,Tue,Wed,Thu,Fri"). */
  workingDays: string;
  /** IANA timezone used to interpret shift hours (e.g. "Asia/Karachi"). */
  timezone: string;
  graceMinutes: number;
  overtimeHours: number;
  description: string;
  employeeCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface ShiftListResponse {
  data: Shift[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface ShiftAllResponse {
  shifts: Shift[];
  total: number;
}

export interface CreateShiftPayload {
  name: string;
  startTime: string;
  endTime: string;
  workingDays: string;
  timezone: string;
  graceMinutes: number;
  overtimeHours: number;
  description?: string;
}

export const shiftsApi = {
  /** Paginated, searchable list (admin /shifts page). */
  list: (params?: { page?: number; perPage?: number; search?: string }) =>
    request<ShiftListResponse>('/shifts', { params: params as Record<string, string | number | undefined> }),

  /** Unpaged list of every non-deleted shift — used by dropdowns. */
  listAll: () => request<ShiftAllResponse>('/shifts/all'),

  create: (data: CreateShiftPayload) =>
    request<Shift>('/shifts', { method: 'POST', body: data }),

  update: (id: number, data: CreateShiftPayload) =>
    request<Shift>('/shifts/' + id, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>('/shifts/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Employee Detail API (machine picture for the user detail page)
// ──────────────────────────

export interface DeviceHardwareDetail {
  id: string;
  macAddress: string;
  hostname: string;
  osName: string;
  osVersion: string;
  cpuModel: string;
  cpuCores: number;
  ramTotalMb: number;
  storageDevices: string;
  gpuModel: string;
  gpuVramMb: number;
  collectedAt: string;
  syncedAt?: string;
}

export interface StorageDeviceDetail {
  id: string;
  deviceType: string;
  model: string;
  capacityMb: number;
  createdAt: string;
}

export interface NetworkInfoDetail {
  id: string;
  employeeId: string;
  publicIp: string;
  privateIp: string;
  macAddress: string;
  networkInterfaceName: string;
  collectedAt: string;
  syncedAt?: string;
}

export interface InstalledApplicationDetail {
  id: string;
  appName: string;
  binaryName?: string;
  version: string;
  publisher: string;
  installPath: string;
  installDate?: string;
  isBrowser: boolean;
  categories?: string;
  desktopId?: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface InstalledPackageDetail {
  id: string;
  packageName: string;
  version: string;
  category: string;
  sourceManager: string;
  installPath: string;
  publisher: string;
  description: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface HardwareDeviceDetail {
  id: string;
  deviceClass: string;
  vendor: string;
  product: string;
  serial: string;
  busPath?: string;
  pluggedAt: string;
  unpluggedAt?: string;
}

export interface PermissionStatusDetail {
  checkId: string;
  sessionId: string;
  sessionType: string;
  platform: string;
  checkedAt: string;
  method: string;
  works: boolean;
  details: string;
}

export interface EmployeeActivityStats {
  totalSessions: number;
  openSessions: number;
  totalItems: number;
  lastActivityAt?: string;
}

export interface EmployeeDetail {
  employee: Employee;
  deviceHardware?: DeviceHardwareDetail;
  storageDevices: StorageDeviceDetail[];
  networkInfo?: NetworkInfoDetail;
  applications: InstalledApplicationDetail[];
  packages: InstalledPackageDetail[];
  hardwareDevices: HardwareDeviceDetail[];
  appStatus: Record<string, string>;
  permissions: PermissionStatusDetail[];
  stats: EmployeeActivityStats;
}

// ──────────────────────────
// Employee Auth API (for desktop client)
// ──────────────────────────

export interface EmployeeLoginRequest {
  employeeId: string;
  secretKey: string;
}

export interface EmployeeLoginResponse {
  employee: Employee;
  token: string;
}

export const employeeAuthApi = {
  login: (data: EmployeeLoginRequest) =>
    request<EmployeeLoginResponse>('/auth/employee-login', { method: 'POST', body: data }),
};

// ──────────────────────────
// App Sessions API (replaces Activity Logs)
// ──────────────────────────

export interface AppSession {
  id: string;
  employeeId: string;
  processName: string;
  appDisplayName: string;
  startedAt: string;
  endedAt?: string;
  machineId: string;
  sessionId: string;
  platform: string;
  processId?: number;
  parentProcessId?: number;
  groupedBy?: string;
  contextLabel?: string;
  foregroundSeconds?: number;
  backgroundSeconds?: number;
  syncedAt?: string;
  // 3-state lifecycle (2026-09-02). The server sweeper transitions
  // ACTIVE → STALE → CLOSED based on lastSyncAt. Only CLOSED is
  // terminal — a live client re-uploading activity with no endedAt
  // promotes the row back to ACTIVE. Status defaults to ACTIVE when
  // the field is absent (pre-031 rows).
  status?: 'ACTIVE' | 'STALE' | 'CLOSED' | string;
  lastActivityAt?: string;
  lastSyncAt?: string;
}

export interface AppSessionListResponse {
  data: AppSession[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export const appSessionsApi = {
  list: (params?: {
    page?: number; perPage?: number; employeeId?: string; search?: string; platform?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<AppSessionListResponse>('/app-sessions', { params: params as Record<string, string | number | undefined> }),
};

export interface AppItem {
  id: string;
  employeeId: string;
  appSessionId: string;
  parentItemId?: string;
  itemType: string;
  title: string;
  identifier: string;
  url?: string;
  domain?: string;
  openedAt: string;
  closedAt?: string;
  processId?: number;
  objectType?: string;
  action?: string;
  journeyId?: string;
  sequence?: number;
  previousPath?: string;
  currentPath?: string;
  windowId?: number;
  tabId?: number;
  metadataJson?: string;
  browserName?: string;
  syncedAt?: string;
}

export interface AppItemListResponse {
  data: AppItem[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export const appItemsApi = {
  list: (params?: {
    page?: number; perPage?: number; employeeId?: string; appSessionId?: string; itemType?: string; search?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<AppItemListResponse>('/app-items', { params: params as Record<string, string | number | undefined> }),
};

// ──────────────────────────
// Monitoring Configuration API
// (types, categories, and app/site classification)
// ──────────────────────────

export type MonitoringCategoryKind = 'application' | 'website' | 'both';

export interface MonitoringType {
  id: number;
  name: string;
  color: string;
  description: string;
}

export interface MonitoringCategory {
  id: number;
  name: string;
  kind: MonitoringCategoryKind;
}

export interface MonitoredApp {
  id: string;
  appName: string;
  binaryName: string;
  categories: string;
  isBrowser: boolean;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface MonitoredSite {
  id: number;
  domain: string;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface MonitoredAppListResponse {
  data: MonitoredApp[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface MonitoredSiteListResponse {
  data: MonitoredSite[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface MonitoringTypePayload {
  name: string;
  color?: string;
  description?: string;
}

export interface MonitoringCategoryPayload {
  name: string;
  kind: MonitoringCategoryKind;
}

export interface ClassificationPayload {
  typeId?: number | null;
  categoryId?: number | null;
}

export const monitoringApi = {
  types: {
    list: () =>
      request<{ types: MonitoringType[]; total: number }>('/monitoring/types'),

    create: (data: MonitoringTypePayload) =>
      request<MonitoringType>('/monitoring/types', { method: 'POST', body: data }),

    update: (id: number, data: MonitoringTypePayload) =>
      request<MonitoringType>('/monitoring/types/' + id, { method: 'PUT', body: data }),

    delete: (id: number) =>
      request<{ message: string }>('/monitoring/types/' + id, { method: 'DELETE' }),
  },

  categories: {
    list: (kind?: MonitoringCategoryKind) =>
      request<{ categories: MonitoringCategory[]; total: number }>('/monitoring/categories', { params: { kind } }),

    create: (data: MonitoringCategoryPayload) =>
      request<MonitoringCategory>('/monitoring/categories', { method: 'POST', body: data }),

    update: (id: number, data: MonitoringCategoryPayload) =>
      request<MonitoringCategory>('/monitoring/categories/' + id, { method: 'PUT', body: data }),

    delete: (id: number) =>
      request<{ message: string }>('/monitoring/categories/' + id, { method: 'DELETE' }),
  },

  apps: {
    list: (params?: {
      search?: string; typeId?: number; categoryId?: number; unclassified?: boolean;
      page?: number; perPage?: number;
    }) =>
      request<MonitoredAppListResponse>('/monitoring/apps', { params: params as Record<string, string | number | boolean | undefined> }),

    classify: (id: string, data: ClassificationPayload) =>
      request<{ message: string }>('/monitoring/apps/' + id, { method: 'PATCH', body: data }),
  },

  websites: {
    list: (params?: {
      search?: string; typeId?: number; categoryId?: number; unclassified?: boolean;
      page?: number; perPage?: number;
    }) =>
      request<MonitoredSiteListResponse>('/monitoring/websites', { params: params as Record<string, string | number | boolean | undefined> }),

    classify: (id: number, data: ClassificationPayload) =>
      request<{ message: string }>('/monitoring/websites/' + id, { method: 'PATCH', body: data }),

    create: (data: { domain: string; typeId?: number | null; categoryId?: number | null }) =>
      request<MonitoredSite>('/monitoring/websites', { method: 'POST', body: data }),
  },
};

// ──────────────────────────
// RBAC API (roles + module/submodule catalog)
// ──────────────────────────

export interface SubmoduleNode {
  id: number;
  moduleId: number;
  key: string;
  name: string;
  routePath: string;
}

export interface ModuleNode {
  id: number;
  key: string;
  name: string;
  sortOrder: number;
  submodules: SubmoduleNode[];
}

export interface ModuleTreeResponse {
  modules: ModuleNode[];
  total: number;
}

export interface Role {
  id: number;
  name: string;
  description: string;
  isSystem: boolean;
  userCount: number;
  submoduleIds: number[];
  permissions: string[];
}

export interface RoleListResponse {
  roles: Role[];
  total: number;
}

export interface CreateRolePayload {
  name: string;
  description?: string;
  submoduleIds?: number[];
}

export interface UpdateRolePayload {
  name?: string;
  description?: string;
  submoduleIds?: number[];
}

export const modulesApi = {
  tree: () => request<ModuleTreeResponse>('/modules'),
};

export const rolesApi = {
  list: () => request<RoleListResponse>('/roles'),

  create: (data: CreateRolePayload) =>
    request<Role>('/roles', { method: 'POST', body: data }),

  update: (id: number, data: UpdateRolePayload) =>
    request<Role>('/roles/' + id, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>('/roles/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Users API (web-dashboard login accounts)
// ──────────────────────────

export interface User {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  roleId: number;
  role: string;
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  createdAt: string;
  updatedAt: string;
}

export interface UserListResponse {
  data: User[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface CreateUserPayload {
  name: string;
  email: string;
  password?: string;
  employeeId?: string;
  roleId: number;
  shift?: string;
}

export interface UpdateUserPayload {
  name?: string;
  email?: string;
  password?: string;
  roleId?: number;
  shift?: string;
}

export const usersApi = {
  list: (params?: { page?: number; perPage?: number; search?: string; roleId?: number; status?: string }) =>
    request<UserListResponse>('/users', { params: params as Record<string, string | number | undefined> }),

  get: (id: string) => request<User>('/users/' + id),

  create: (data: CreateUserPayload) =>
    request<User>('/users', { method: 'POST', body: data }),

  update: (id: string, data: UpdateUserPayload) =>
    request<User>('/users/' + id, { method: 'PUT', body: data }),

  delete: (id: string) =>
    request<{ message: string }>('/users/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Time & Attendance API
// ──────────────────────────

export type AttendanceStatus =
  | 'present'
  | 'late'
  | 'absent'
  | 'half_day'
  | 'off_shift'
  | 'unknown';

export interface AttendanceRecord {
  employeeId: string;
  workDate: string;
  timezone?: string;
  firstActiveAt?: string | null;
  lastActiveAt?: string | null;
  activeSeconds: number;
  idleSeconds: number;
  offShiftSeconds: number;
  status: AttendanceStatus;
  lateMinutes: number;
}

export interface AttendanceRangeResponse {
  data: AttendanceRecord[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface Holiday {
  id: number;
  date: string;
  label: string;
}

export interface HolidayListResponse {
  data: Holiday[];
  total: number;
}

export interface HolidayInput {
  date: string;
  label: string;
}

export const attendanceApi = {
  today: (employeeId: string) =>
    request<AttendanceRecord>('/attendance/today', { params: { employeeId } }),

  range: (params: {
    employeeId: string;
    from: string;
    to: string;
    page?: number;
    perPage?: number;
  }) =>
    request<AttendanceRangeResponse>('/attendance/range', {
      params: params as Record<string, string | number | undefined>,
    }),

  /** Convenience wrapper: one employee, one calendar day. */
  day: (employeeId: string, date: string) =>
    request<AttendanceRangeResponse>('/attendance/range', {
      params: { employeeId, from: date, to: date, page: 1, perPage: 1 },
    }).then(r => r.data[0] ?? null),
};

export const holidaysApi = {
  list: () => request<HolidayListResponse>('/holidays'),

  create: (data: HolidayInput) =>
    request<Holiday>('/holidays', { method: 'POST', body: data }),

  update: (id: number, data: HolidayInput) =>
    request<Holiday>(`/holidays/${id}`, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>(`/holidays/${id}`, { method: 'DELETE' }),
};

// ──────────────────────────
// Location Samples API (Phase 3 GPS)
// ──────────────────────────

export type LocationSource = 'gps' | 'wifi' | 'ip' | 'manual';

export interface LocationSample {
  id: string;
  employeeId: string;
  employeeName?: string;
  latitude: number;
  longitude: number;
  accuracyM?: number;
  altitudeM?: number;
  source: LocationSource | string;
  address?: string;
  capturedAt: string;
  syncedAt?: string;
  geofenceStatus?: string;
}

export interface LocationSampleListResponse {
  data: LocationSample[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export const locationSamplesApi = {
  list: (params?: {
    page?: number;
    perPage?: number;
    employeeId?: string;
    dateFrom?: string;
    dateTo?: string;
  }) =>
    request<LocationSampleListResponse>('/location-samples', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ──────────────────────────
// Geofence Zones API (Phase 3 GPS B.8)
// ──────────────────────────

export interface GeofenceZone {
  id: number;
  name: string;
  latitude: number;
  longitude: number;
  radiusM: number;
  alertOnExit: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface GeofenceZoneListResponse {
  data: GeofenceZone[];
}

export const geofenceApi = {
  list: () => request<GeofenceZoneListResponse>('/geofence-zones'),
  create: (body: {
    name: string;
    latitude: number;
    longitude: number;
    radiusM: number;
    alertOnExit?: boolean;
  }) =>
    request<GeofenceZone>('/geofence-zones', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  update: (
    id: number,
    body: Partial<{
      name: string;
      latitude: number;
      longitude: number;
      radiusM: number;
      alertOnExit: boolean;
    }>,
  ) =>
    request<GeofenceZone>(`/geofence-zones/${id}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  delete: (id: number) =>
    request<{ message: string }>(`/geofence-zones/${id}`, { method: 'DELETE' }),
};

// ──────────────────────────
// Health API
// ──────────────────────────

export const healthApi = {
  check: () => request<{ status: string; timestamp: string }>('/health'),
};

export { ApiError };
