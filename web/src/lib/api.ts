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
  role: string;
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
  department?: string;
  role: string;
  shift?: string;
}

export interface UpdateEmployeePayload {
  name?: string;
  email?: string;
  department?: string;
  departmentId?: number;
  role?: string;
  shift?: string;
  trackingEnabled?: boolean;
  trackingStatus?: string;
  isOnline?: boolean;
}

export interface GenerateSecretResponse {
  secret: string;
  expiresIn: number;
}

export const employeesApi = {
  list: (params?: { page?: number; perPage?: number; search?: string; department?: string; role?: string; status?: string }) =>
    request<EmployeeListResponse>('/employees', { params: params as Record<string, string | number | undefined> }),

  get: (id: string) => request<Employee>('/employees/' + id),

  create: (data: CreateEmployeePayload) =>
    request<Employee>('/employees', { method: 'POST', body: data }),

  update: (id: string, data: UpdateEmployeePayload) =>
    request<Employee>('/employees/' + id, { method: 'PUT', body: data }),

  delete: (id: string) =>
    request<{ message: string }>('/employees/' + id, { method: 'DELETE' }),

  generateSecret: (id: string) =>
    request<GenerateSecretResponse>('/employees/' + id + '/generate-secret', { method: 'POST' }),
};

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
  list: (params?: { page?: number; perPage?: number; employeeId?: string; search?: string; platform?: string }) =>
    request<AppSessionListResponse>('/app-sessions', { params: params as Record<string, string | number | undefined> }),
};

// ──────────────────────────
// Health API
// ──────────────────────────

export const healthApi = {
  check: () => request<{ status: string; timestamp: string }>('/health'),
};

export { ApiError };
