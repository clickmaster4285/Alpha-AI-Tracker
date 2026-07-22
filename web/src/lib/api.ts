// API client for communicating with the Go backend server.
// Uses httpOnly cookies for JWT authentication (set by server).

// Use relative URL through Next.js rewrites proxy. Set NEXT_PUBLIC_API_URL to override.
const API_BASE = process.env.NEXT_PUBLIC_API_URL || '/api/v1';

interface RequestOptions {
  method?: string;
  body?: unknown;
  params?: Record<string, string | number | undefined>;
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
      if (value !== undefined && value !== '') {
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
// Users API
// ──────────────────────────

export interface User {
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
  department: string;
  role: string;
  shift?: string;
  trackingEnabled?: boolean;
}

export interface UpdateUserPayload {
  name?: string;
  email?: string;
  department?: string;
  role?: string;
  shift?: string;
  trackingEnabled?: boolean;
  trackingStatus?: string;
  isOnline?: boolean;
}

export const usersApi = {
  list: (params?: { page?: number; perPage?: number; search?: string; department?: string; role?: string; status?: string }) =>
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
// Departments API
// ──────────────────────────

export const departmentsApi = {
  list: () => request<{ departments: string[] }>('/departments'),
};

// ──────────────────────────
// Health API
// ──────────────────────────

export const healthApi = {
  check: () => request<{ status: string; timestamp: string }>('/health'),
};

export { ApiError };
