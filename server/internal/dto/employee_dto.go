package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────// CreateEmployeeRequest is the payload for creating a new employee.
type CreateEmployeeRequest struct {
	Name         string `json:"name"`
	Email        string `json:"email"`
	Department   string `json:"department,omitempty"`
	DepartmentID int    `json:"departmentId"`
	Role         string `json:"role"`
	Shift        string `json:"shift,omitempty"`
}

// UpdateEmployeeRequest is the payload for updating an employee.
type UpdateEmployeeRequest struct {
	Name            *string `json:"name,omitempty"`
	Email           *string `json:"email,omitempty"`
	Department      *string `json:"department,omitempty"`
	DepartmentID    *int    `json:"departmentId,omitempty"`
	Role            *string `json:"role,omitempty"`
	Shift           *string `json:"shift,omitempty"`
	TrackingEnabled *bool   `json:"trackingEnabled,omitempty"`
	TrackingStatus  *string `json:"trackingStatus,omitempty"`
	IsOnline        *bool   `json:"isOnline,omitempty"`
}

// EmployeeLoginRequest is the payload for employee login via desktop client.
type EmployeeLoginRequest struct {
	EmployeeID string `json:"employeeId"`
	SecretKey  string `json:"secretKey"`
}

// GenerateSecretResponse is returned when generating a login secret.
type GenerateSecretResponse struct {
	Secret    string `json:"secret"`
	ExpiresIn int    `json:"expiresIn"` // seconds
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// EmployeeResponse is the public API response for an employee.
type EmployeeResponse struct {
	ID              string    `json:"id"`
	EmployeeID      string    `json:"employeeId"`
	Name            string    `json:"name"`
	Email           string    `json:"email"`
	Role            string    `json:"role"`
	Department      string    `json:"department"`
	DepartmentID    int       `json:"departmentId"`
	Shift           string    `json:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled"`
	TrackingStatus  string    `json:"trackingStatus"`
	IsOnline        bool      `json:"isOnline"`
	Avatar          string    `json:"avatar"`
	AvatarColor     string    `json:"avatarColor"`
	CreatedAt       time.Time    `json:"createdAt"`
	UpdatedAt       time.Time    `json:"updatedAt"`
	DeletedAt       *time.Time   `json:"deletedAt"`
}

// EmployeeListResponse is a paginated list response.
type EmployeeListResponse struct {
	Data       []EmployeeResponse `json:"data"`
	Total      int                `json:"total"`
	Page       int                `json:"page"`
	PerPage    int                `json:"perPage"`
	TotalPages int                `json:"totalPages"`
}

// EmployeeLoginResponse is returned on successful employee login.
type EmployeeLoginResponse struct {
	Employee EmployeeResponse `json:"employee"`
	Token    string           `json:"token,omitempty"`
}

// ────────────────────────────────
// Employee Detail (web dashboard — GET /employees/:id/detail)
// Aggregate view of an employee plus every synced machine data surface.
// ────────────────────────────────

// DeviceHardwareDetail is the latest device_hardware_info snapshot for the employee's machine.
type DeviceHardwareDetail struct {
	ID             string     `json:"id"`
	MacAddress     string     `json:"macAddress"`
	Hostname       string     `json:"hostname"`
	OsName         string     `json:"osName"`
	OsVersion      string     `json:"osVersion"`
	CpuModel       string     `json:"cpuModel"`
	CpuCores       int        `json:"cpuCores"`
	RamTotalMb     int64      `json:"ramTotalMb"`
	StorageDevices string     `json:"storageDevices"`
	GpuModel       string     `json:"gpuModel"`
	GpuVramMb      int64      `json:"gpuVramMb"`
	CollectedAt    time.Time  `json:"collectedAt"`
	SyncedAt       *time.Time `json:"syncedAt,omitempty"`
}

// StorageDeviceDetail is a storage_devices child row (drive/model/capacity).
type StorageDeviceDetail struct {
	ID          string    `json:"id"`
	DeviceType  string    `json:"deviceType"`
	Model       string    `json:"model"`
	CapacityMb  int64     `json:"capacityMb"`
	CreatedAt   time.Time `json:"createdAt"`
}

// InstalledApplicationDetail is the joined catalog+junction view of one app on this employee's machine.
type InstalledApplicationDetail struct {
	ID          string     `json:"id"`
	AppName     string     `json:"appName"`
	BinaryName  string     `json:"binaryName,omitempty"`
	Version     string     `json:"version"`
	Publisher   string     `json:"publisher"`
	InstallPath string     `json:"installPath"`
	InstallDate *time.Time `json:"installDate,omitempty"`
	IsBrowser   bool       `json:"isBrowser"`
	Categories  string     `json:"categories,omitempty"`
	DesktopID   string     `json:"desktopId,omitempty"`
	FirstSeenAt time.Time  `json:"firstSeenAt"`
	LastSeenAt  time.Time  `json:"lastSeenAt"`
}

// InstalledPackageDetail is the joined catalog+junction view of one package on this employee's machine.
type InstalledPackageDetail struct {
	ID            string    `json:"id"`
	PackageName   string    `json:"packageName"`
	Version       string    `json:"version"`
	Category      string    `json:"category"`
	SourceManager string    `json:"sourceManager"`
	InstallPath   string    `json:"installPath"`
	Publisher     string    `json:"publisher"`
	Description   string    `json:"description"`
	FirstSeenAt   time.Time `json:"firstSeenAt"`
	LastSeenAt    time.Time `json:"lastSeenAt"`
}

// HardwareDeviceDetail is one peripheral (USB hotplug) row.
type HardwareDeviceDetail struct {
	ID          string     `json:"id"`
	DeviceClass string     `json:"deviceClass"`
	Vendor      string     `json:"vendor"`
	Product     string     `json:"product"`
	Serial      string     `json:"serial"`
	BusPath     string     `json:"busPath,omitempty"`
	PluggedAt   time.Time  `json:"pluggedAt"`
	UnpluggedAt *time.Time `json:"unpluggedAt,omitempty"`
}

// PermissionStatusDetail is one permission-method check row.
type PermissionStatusDetail struct {
	CheckID     string    `json:"checkId"`
	SessionID   string    `json:"sessionId"`
	SessionType string    `json:"sessionType"`
	Platform    string    `json:"platform"`
	CheckedAt   time.Time `json:"checkedAt"`
	Method      string    `json:"method"`
	Works       bool      `json:"works"`
	Details     string    `json:"details"`
}

// EmployeeActivityStats are derived counts over app_sessions / app_items.
type EmployeeActivityStats struct {
	TotalSessions  int        `json:"totalSessions"`
	OpenSessions   int        `json:"openSessions"`
	TotalItems     int        `json:"totalItems"`
	LastActivityAt *time.Time `json:"lastActivityAt,omitempty"`
}

// EmployeeDetailResponse is the full machine picture for one employee.
type EmployeeDetailResponse struct {
	Employee        EmployeeResponse             `json:"employee"`
	DeviceHardware  *DeviceHardwareDetail        `json:"deviceHardware,omitempty"`
	StorageDevices  []StorageDeviceDetail        `json:"storageDevices"`
	NetworkInfo     *NetworkInfoResponse         `json:"networkInfo,omitempty"`
	Applications    []InstalledApplicationDetail `json:"applications"`
	Packages        []InstalledPackageDetail     `json:"packages"`
	HardwareDevices []HardwareDeviceDetail       `json:"hardwareDevices"`
	AppStatus       map[string]string            `json:"appStatus"`
	Permissions     []PermissionStatusDetail     `json:"permissions"`
	Stats           EmployeeActivityStats        `json:"stats"`
}
