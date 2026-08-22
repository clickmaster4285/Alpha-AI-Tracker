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

  const response = await fetch(url, fetchOptions);

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
  department: string;
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  createdAt: string;
  updatedAt: string;
}

export interface LoginResponse {
  user: AuthUser;
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

  check: () => request<AuthCheckResponse>('/auth/check'),
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
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
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
  shift?: string;
}

export interface UpdateEmployeePayload {
  name?: string;
  email?: string;
  departmentId?: number;
  shift?: string;
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
// Health API
// ──────────────────────────

export const healthApi = {
  check: () => request<{ status: string; timestamp: string }>('/health'),
};

export { ApiError };
