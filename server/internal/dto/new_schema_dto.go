package dto

import "time"

// ────────────────────────────────
// GENERIC SYNC REQUEST/RESPONSE
// ────────────────────────────────

type SyncBatchRequest struct {
	EmployeeID string `json:"employeeId"`
	Token      string `json:"token"`
	Entries    []any  `json:"entries"`
}

type SyncBatchResponse struct {
	Synced  int    `json:"synced"`
	Message string `json:"message"`
}

// ────────────────────────────────
// PHASE 1: Device Hardware Info
// ────────────────────────────────

type DeviceHardwareInfoEntry struct {
	ID             string `json:"id"`
	MacAddress     string `json:"macAddress"`
	Hostname       string `json:"hostname"`
	OsName         string `json:"osName"`
	OsVersion      string `json:"osVersion"`
	CpuModel       string `json:"cpuModel"`
	CpuCores       int    `json:"cpuCores"`
	RamTotalMb     int64  `json:"ramTotalMb"`
	StorageDevices string `json:"storageDevices"`
	GpuModel       string `json:"gpuModel"`
	GpuVramMb      int64  `json:"gpuVramMb"`
	CollectedAt    string `json:"collectedAt"`
}

type SyncDeviceHardwareRequest struct {
	EmployeeID string                    `json:"employeeId"`
	Token      string                    `json:"token"`
	Entries    []DeviceHardwareInfoEntry `json:"entries"`
}

type DeviceHardwareInfoResponse struct {
	ID             string     `json:"id"`
	EmployeeID     string     `json:"employeeId"`
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

// ────────────────────────────────
// PHASE 1: Installed Applications
// ────────────────────────────────

type InstalledApplicationEntry struct {
	ID              string  `json:"id"`
	AppName         string  `json:"appName"`
	AppVersion      string  `json:"appVersion"`
	Publisher       string  `json:"publisher"`
	InstallPath     string  `json:"installPath"`
	InstallDate     *string `json:"installDate,omitempty"`
	UninstallString string  `json:"uninstallString"`
	ChangeType      string  `json:"changeType"`
	DetectedAt      string  `json:"detectedAt"`
	BinaryName      string  `json:"binaryName"`
	IsBrowser       bool    `json:"isBrowser"`
	DesktopID       string  `json:"desktopId"`
	Categories      string  `json:"categories"`
}

type SyncInstalledAppsRequest struct {
	EmployeeID string                      `json:"employeeId"`
	Token      string                      `json:"token"`
	Entries    []InstalledApplicationEntry `json:"entries"`
}

type InstalledApplicationResponse struct {
	ID              string     `json:"id"`
	EmployeeID      string     `json:"employeeId"`
	AppName         string     `json:"appName"`
	AppVersion      string     `json:"appVersion"`
	Publisher       string     `json:"publisher"`
	InstallPath     string     `json:"installPath"`
	InstallDate     *time.Time `json:"installDate,omitempty"`
	UninstallString string     `json:"uninstallString"`
	ChangeType      string     `json:"changeType"`
	DetectedAt      time.Time  `json:"detectedAt"`
	SyncedAt        *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 1: Network Info
// ────────────────────────────────

type NetworkInfoEntry struct {
	ID                   string `json:"id"`
	PublicIP             string `json:"publicIp"`
	PrivateIP            string `json:"privateIp"`
	MacAddress           string `json:"macAddress"`
	NetworkInterfaceName string `json:"networkInterfaceName"`
	CollectedAt          string `json:"collectedAt"`
}

type InstalledPackageEntry struct {
	ID            string `json:"id"`
	PackageName   string `json:"packageName"`
	Version       string `json:"version"`
	Category      string `json:"category"`
	SourceManager string `json:"sourceManager"`
	InstallPath   string `json:"installPath"`
	Publisher     string `json:"publisher"`
	Description   string `json:"description"`
	DetectedAt    string `json:"detectedAt"`
}

type SyncInstalledPackagesRequest struct {
	EmployeeID string                  `json:"employeeId"`
	Token      string                  `json:"token"`
	Entries    []InstalledPackageEntry `json:"entries"`
}

type InstalledPackageResponse struct {
	ID            string     `json:"id"`
	EmployeeID    string     `json:"employeeId"`
	PackageName   string     `json:"packageName"`
	Version       string     `json:"version"`
	Category      string     `json:"category"`
	SourceManager string     `json:"sourceManager"`
	InstallPath   string     `json:"installPath"`
	Publisher     string     `json:"publisher"`
	Description   string     `json:"description"`
	DetectedAt    time.Time  `json:"detectedAt"`
	SyncedAt      *time.Time `json:"syncedAt,omitempty"`
}

type SyncNetworkInfoRequest struct {
	EmployeeID string             `json:"employeeId"`
	Token      string             `json:"token"`
	Entries    []NetworkInfoEntry `json:"entries"`
}

type NetworkInfoResponse struct {
	ID                   string     `json:"id"`
	EmployeeID           string     `json:"employeeId"`
	PublicIP             string     `json:"publicIp"`
	PrivateIP            string     `json:"privateIp"`
	MacAddress           string     `json:"macAddress"`
	NetworkInterfaceName string     `json:"networkInterfaceName"`
	CollectedAt          time.Time  `json:"collectedAt"`
	SyncedAt             *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 1: Session Events
// ────────────────────────────────

type SessionEventEntry struct {
	ID         string  `json:"id"`
	EventType  string  `json:"eventType"`
	OsUsername string  `json:"osUsername"`
	EventAt    string  `json:"eventAt"`
	Count      *int    `json:"count,omitempty"`
	FirstAt    *string `json:"firstAt,omitempty"`
	LastAt     *string `json:"lastAt,omitempty"`
}

type SyncSessionEventsRequest struct {
	EmployeeID string              `json:"employeeId"`
	Token      string              `json:"token"`
	Entries    []SessionEventEntry `json:"entries"`
}

type SessionEventResponse struct {
	ID         string     `json:"id"`
	EmployeeID string     `json:"employeeId"`
	EventType  string     `json:"eventType"`
	OsUsername string     `json:"osUsername"`
	EventAt    time.Time  `json:"eventAt"`
	Count      int        `json:"count"`
	FirstAt    time.Time  `json:"firstAt"`
	LastAt     time.Time  `json:"lastAt"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: App Sessions
// ────────────────────────────────

type AppSessionEntry struct {
	ID                 string  `json:"id"`
	ProcessName        string  `json:"processName"`
	AppDisplayName     string  `json:"appDisplayName"`
	StartedAt          string  `json:"startedAt"`
	EndedAt            *string `json:"endedAt,omitempty"`
	MachineID          string  `json:"machineId"`
	SessionID          string  `json:"sessionId"`
	Platform           string  `json:"platform"`
	ProcessID          *int    `json:"processId,omitempty"`
	ParentProcessID    *int    `json:"parentProcessId,omitempty"`
	InstalledAppID     *string `json:"installedAppId,omitempty"`
	InstalledPackageID *string `json:"installedPackageId,omitempty"`
	GroupedBy          *string `json:"groupedBy,omitempty"`
	CgroupScope        *string `json:"cgroupScope,omitempty"`
	ContextLabel       *string `json:"contextLabel,omitempty"`
	ForegroundSeconds  float64 `json:"foregroundSeconds"`
	BackgroundSeconds  float64 `json:"backgroundSeconds"`
	// Optional — client populates on heartbeat so the server sweeper
	// can distinguish "activity within last X min" from "just the
	// session record survived". Server defaults to started_at on INSERT.
	LastActivityAt     *string `json:"lastActivityAt,omitempty"`
}

type SyncAppSessionsRequest struct {
	EmployeeID string            `json:"employeeId"`
	Token      string            `json:"token"`
	Entries    []AppSessionEntry `json:"entries"`
}

type AppSessionResponse struct {
	ID                 string     `json:"id"`
	EmployeeID         string     `json:"employeeId"`
	ProcessName        string     `json:"processName"`
	AppDisplayName     string     `json:"appDisplayName"`
	StartedAt          time.Time  `json:"startedAt"`
	EndedAt            *time.Time `json:"endedAt,omitempty"`
	MachineID          string     `json:"machineId"`
	SessionID          string     `json:"sessionId"`
	Platform           string     `json:"platform"`
	ProcessID          *int       `json:"processId,omitempty"`
	ParentProcessID    *int       `json:"parentProcessId,omitempty"`
	InstalledAppID     *string    `json:"installedAppId,omitempty"`
	InstalledPackageID *string    `json:"installedPackageId,omitempty"`
	GroupedBy          *string    `json:"groupedBy,omitempty"`
	CgroupScope        *string    `json:"cgroupScope,omitempty"`
	ContextLabel       *string    `json:"contextLabel,omitempty"`
	ForegroundSeconds  float64    `json:"foregroundSeconds"`
	BackgroundSeconds  float64    `json:"backgroundSeconds"`
	SyncedAt           *time.Time `json:"syncedAt,omitempty"`
	// 3-state lifecycle (2026-09-02): ACTIVE → STALE → CLOSED.
	Status         string     `json:"status"`
	LastActivityAt *time.Time `json:"lastActivityAt,omitempty"`
	LastSyncAt     *time.Time `json:"lastSyncAt,omitempty"`
}

// ────────────────────────────────
// App Usage (per-app aggregate for web dashboard)
// ────────────────────────────────

type AppUsageRow struct {
	AppDisplayName       string    `json:"appDisplayName"`
	ProcessName          string    `json:"processName"`
	SessionCount         int       `json:"sessionCount"`
	FirstOpenedAt        time.Time `json:"firstOpenedAt"`
	LastClosedAt         time.Time `json:"lastClosedAt"`
	TotalDurationSeconds float64   `json:"totalDurationSeconds"`
}

type AppUsageListResponse struct {
	Data       []AppUsageRow `json:"data"`
	Total      int           `json:"total"`
	Page       int           `json:"page"`
	PerPage    int           `json:"perPage"`
	TotalPages int           `json:"totalPages"`
}

// ────────────────────────────────
// App Items (replaces browser_contexts, file_explorer_contexts, urls, url_visits)
// ────────────────────────────────

type AppItemEntry struct {
	ID           string  `json:"id"`
	AppSessionID string  `json:"appSessionId"`
	ParentItemID *string `json:"parentItemId,omitempty"`
	ItemType     string  `json:"itemType"`
	Title        string  `json:"title"`
	Identifier   string  `json:"identifier"`
	Url          string  `json:"url"`
	Domain       string  `json:"domain"`
	OpenedAt     string  `json:"openedAt"`
	ClosedAt     *string `json:"closedAt,omitempty"`
	ProcessID    *int    `json:"processId,omitempty"`
	ObjectType   string  `json:"objectType"`
	Action       string  `json:"action"`
	JourneyID    string  `json:"journeyId"`
	Sequence     int     `json:"sequence"`
	PreviousPath string  `json:"previousPath"`
	CurrentPath  string  `json:"currentPath"`
	WindowID     *int    `json:"windowId,omitempty"`
	TabID        *int    `json:"tabId,omitempty"`
	MetadataJSON string  `json:"metadataJson"`
}

type SyncAppItemsRequest struct {
	EmployeeID string         `json:"employeeId"`
	Token      string         `json:"token"`
	Entries    []AppItemEntry `json:"entries"`
}

type AppItemResponse struct {
	ID           string     `json:"id"`
	EmployeeID   string     `json:"employeeId"`
	AppSessionID string     `json:"appSessionId"`
	ParentItemID *string    `json:"parentItemId,omitempty"`
	ItemType     string     `json:"itemType"`
	Title        string     `json:"title"`
	Identifier   string     `json:"identifier"`
	Url          string     `json:"url"`
	Domain       string     `json:"domain"`
	OpenedAt     time.Time  `json:"openedAt"`
	ClosedAt     *time.Time `json:"closedAt,omitempty"`
	ProcessID    *int       `json:"processId,omitempty"`
	ObjectType   string     `json:"objectType"`
	Action       string     `json:"action"`
	JourneyID    string     `json:"journeyId"`
	Sequence     int        `json:"sequence"`
	PreviousPath string     `json:"previousPath"`
	CurrentPath  string     `json:"currentPath"`
	WindowID     *int       `json:"windowId,omitempty"`
	TabID        *int       `json:"tabId,omitempty"`
	MetadataJSON string     `json:"metadataJson"`
	BrowserName  string     `json:"browserName"`
	SyncedAt     *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: LIST RESPONSES
// ────────────────────────────────

type AppSessionListResponse struct {
	Data       []AppSessionResponse `json:"data"`
	Total      int                  `json:"total"`
	Page       int                  `json:"page"`
	PerPage    int                  `json:"perPage"`
	TotalPages int                  `json:"totalPages"`
}

type AppItemListResponse struct {
	Data       []AppItemResponse `json:"data"`
	Total      int               `json:"total"`
	Page       int               `json:"page"`
	PerPage    int               `json:"perPage"`
	TotalPages int               `json:"totalPages"`
}

// ────────────────────────────────
// PHASE 3: app_status / hardware_devices / permission_status / storage_devices
// (2026-08-11 — previously local-only tables now synced; never deleted client-side)
// ────────────────────────────────

type AppStatusEntry struct {
	Key       string `json:"key"`
	Value     string `json:"value"`
	UpdatedAt string `json:"updatedAt"`
}

type SyncAppStatusRequest struct {
	EmployeeID string           `json:"employeeId"`
	Token      string           `json:"token"`
	Entries    []AppStatusEntry `json:"entries"`
}

type HardwareDeviceEntry struct {
	ID          string  `json:"id"`
	DeviceClass string  `json:"deviceClass"`
	Vendor      string  `json:"vendor"`
	Product     string  `json:"product"`
	Serial      string  `json:"serial"`
	BusPath     string  `json:"busPath"`
	DeviceNode  string  `json:"deviceNode"`
	PluggedAt   string  `json:"pluggedAt"`
	UnpluggedAt *string `json:"unpluggedAt,omitempty"`
}

type SyncHardwareDevicesRequest struct {
	EmployeeID string                `json:"employeeId"`
	Token      string                `json:"token"`
	Entries    []HardwareDeviceEntry `json:"entries"`
}

type PermissionStatusEntry struct {
	CheckID     string `json:"checkId"`
	SessionID   string `json:"sessionId"`
	SessionType string `json:"sessionType"`
	Platform    string `json:"platform"`
	CheckedAt   string `json:"checkedAt"`
	Method      string `json:"method"`
	Works       bool   `json:"works"`
	Details     string `json:"details"`
}

type SyncPermissionStatusRequest struct {
	EmployeeID string                  `json:"employeeId"`
	Token      string                  `json:"token"`
	Entries    []PermissionStatusEntry `json:"entries"`
}

type StorageDeviceEntry struct {
	ID               string `json:"id"`
	DeviceHardwareID string `json:"deviceHardwareId"`
	DeviceType       string `json:"deviceType"`
	Model            string `json:"model"`
	CapacityMB       int64  `json:"capacityMb"`
}

type SyncStorageDevicesRequest struct {
	EmployeeID string               `json:"employeeId"`
	Token      string               `json:"token"`
	Entries    []StorageDeviceEntry `json:"entries"`
}

// ────────────────────────────────
// location_samples (Phase 3 GPS, 2026-09-01)
// ────────────────────────────────

type LocationSampleEntry struct {
	ID         string   `json:"id"`
	Latitude   float64  `json:"latitude"`
	Longitude  float64  `json:"longitude"`
	AccuracyM  *float64 `json:"accuracyM,omitempty"`
	AltitudeM  *float64 `json:"altitudeM,omitempty"`
	Source     string   `json:"source"`
	Address    *string  `json:"address,omitempty"`
	CapturedAt string   `json:"capturedAt"`
}

type SyncLocationSamplesRequest struct {
	EmployeeID string                `json:"employeeId"`
	Token      string                `json:"token"`
	Entries    []LocationSampleEntry `json:"entries"`
}

type LocationSampleResponse struct {
	ID             string     `json:"id"`
	EmployeeID     string     `json:"employeeId"`
	EmployeeName   string     `json:"employeeName,omitempty"`
	Latitude       float64    `json:"latitude"`
	Longitude      float64    `json:"longitude"`
	AccuracyM      *float64   `json:"accuracyM,omitempty"`
	AltitudeM      *float64   `json:"altitudeM,omitempty"`
	Source         string     `json:"source"`
	Address        *string    `json:"address,omitempty"`
	CapturedAt     time.Time  `json:"capturedAt"`
	SyncedAt       *time.Time `json:"syncedAt,omitempty"`
	GeofenceStatus string     `json:"geofenceStatus,omitempty"`
}

type LocationSampleListResponse struct {
	Data       []LocationSampleResponse `json:"data"`
	Total      int                      `json:"total"`
	Page       int                      `json:"page"`
	PerPage    int                      `json:"perPage"`
	TotalPages int                      `json:"totalPages"`
}
