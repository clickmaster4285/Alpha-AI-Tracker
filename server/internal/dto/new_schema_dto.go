package dto

import "time"

// ────────────────────────────────
// GENERIC SYNC REQUEST/RESPONSE
// ────────────────────────────────

type SyncBatchRequest struct {
	EmployeeID string          `json:"employeeId"`
	Token      string          `json:"token"`
	Entries    []any           `json:"entries"`
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
	EmployeeID string                   `json:"employeeId"`
	Token      string                   `json:"token"`
	Entries    []DeviceHardwareInfoEntry `json:"entries"`
}

type DeviceHardwareInfoResponse struct {
	ID             string    `json:"id"`
	EmployeeID     string    `json:"employeeId"`
	MacAddress     string    `json:"macAddress"`
	Hostname       string    `json:"hostname"`
	OsName         string    `json:"osName"`
	OsVersion      string    `json:"osVersion"`
	CpuModel       string    `json:"cpuModel"`
	CpuCores       int       `json:"cpuCores"`
	RamTotalMb     int64     `json:"ramTotalMb"`
	StorageDevices string    `json:"storageDevices"`
	GpuModel       string    `json:"gpuModel"`
	GpuVramMb      int64     `json:"gpuVramMb"`
	CollectedAt    time.Time `json:"collectedAt"`
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
}

type SyncInstalledAppsRequest struct {
	EmployeeID string                     `json:"employeeId"`
	Token      string                     `json:"token"`
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
	EmployeeID string            `json:"employeeId"`
	Token      string            `json:"token"`
	Entries    []NetworkInfoEntry `json:"entries"`
}

type NetworkInfoResponse struct {
	ID                   string    `json:"id"`
	EmployeeID           string    `json:"employeeId"`
	PublicIP             string    `json:"publicIp"`
	PrivateIP            string    `json:"privateIp"`
	MacAddress           string    `json:"macAddress"`
	NetworkInterfaceName string    `json:"networkInterfaceName"`
	CollectedAt          time.Time `json:"collectedAt"`
	SyncedAt             *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 1: Session Events
// ────────────────────────────────

type SessionEventEntry struct {
	ID         string `json:"id"`
	EventType  string `json:"eventType"`
	OsUsername string `json:"osUsername"`
	EventAt    string `json:"eventAt"`
}

type SyncSessionEventsRequest struct {
	EmployeeID string             `json:"employeeId"`
	Token      string             `json:"token"`
	Entries    []SessionEventEntry `json:"entries"`
}

type SessionEventResponse struct {
	ID         string    `json:"id"`
	EmployeeID string    `json:"employeeId"`
	EventType  string    `json:"eventType"`
	OsUsername string    `json:"osUsername"`
	EventAt    time.Time `json:"eventAt"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: App Sessions
// ────────────────────────────────

type AppSessionEntry struct {
	ID              string  `json:"id"`
	ProcessName     string  `json:"processName"`
	AppDisplayName  string  `json:"appDisplayName"`
	StartedAt       string  `json:"startedAt"`
	EndedAt         *string `json:"endedAt,omitempty"`
	MachineID       string  `json:"machineId"`
	SessionID       string  `json:"sessionId"`
	Platform        string  `json:"platform"`
	ProcessID       *int    `json:"processId,omitempty"`
	ParentProcessID *int    `json:"parentProcessId,omitempty"`
}

type SyncAppSessionsRequest struct {
	EmployeeID string           `json:"employeeId"`
	Token      string           `json:"token"`
	Entries    []AppSessionEntry `json:"entries"`
}

type AppSessionResponse struct {
	ID             string     `json:"id"`
	EmployeeID     string     `json:"employeeId"`
	ProcessName    string     `json:"processName"`
	AppDisplayName string     `json:"appDisplayName"`
	StartedAt      time.Time  `json:"startedAt"`
	EndedAt        *time.Time `json:"endedAt,omitempty"`
	MachineID       string     `json:"machineId"`
	SessionID       string     `json:"sessionId"`
	Platform        string     `json:"platform"`
	ProcessID       *int       `json:"processId,omitempty"`
	ParentProcessID *int       `json:"parentProcessId,omitempty"`
	SyncedAt        *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: Browser Contexts
// ────────────────────────────────

type BrowserContextEntry struct {
	ID                 string  `json:"id"`
	AppSessionID       string  `json:"appSessionId"`
	BrowserProfileName string  `json:"browserProfileName"`
	TabID              string  `json:"tabId"`
	OpenedAt           string  `json:"openedAt"`
	ClosedAt           *string `json:"closedAt,omitempty"`
}

type SyncBrowserContextsRequest struct {
	EmployeeID string               `json:"employeeId"`
	Token      string               `json:"token"`
	Entries    []BrowserContextEntry `json:"entries"`
}

type BrowserContextResponse struct {
	ID                 string     `json:"id"`
	EmployeeID         string     `json:"employeeId"`
	AppSessionID       string     `json:"appSessionId"`
	BrowserProfileName string     `json:"browserProfileName"`
	TabID              string     `json:"tabId"`
	OpenedAt           time.Time  `json:"openedAt"`
	ClosedAt           *time.Time `json:"closedAt,omitempty"`
	SyncedAt           *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: File Explorer Contexts
// ────────────────────────────────

type FileExplorerContextEntry struct {
	ID         string  `json:"id"`
	AppSessionID string `json:"appSessionId"`
	FolderPath string  `json:"folderPath"`
	OpenedAt   string  `json:"openedAt"`
	ClosedAt   *string `json:"closedAt,omitempty"`
}

type SyncFileExplorerContextsRequest struct {
	EmployeeID string                    `json:"employeeId"`
	Token      string                    `json:"token"`
	Entries    []FileExplorerContextEntry `json:"entries"`
}

type FileExplorerContextResponse struct {
	ID           string     `json:"id"`
	EmployeeID   string     `json:"employeeId"`
	AppSessionID string     `json:"appSessionId"`
	FolderPath   string     `json:"folderPath"`
	OpenedAt     time.Time  `json:"openedAt"`
	ClosedAt     *time.Time `json:"closedAt,omitempty"`
	SyncedAt     *time.Time `json:"syncedAt,omitempty"`
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
	SyncedAt     *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// URLs
// ────────────────────────────────

type UrlEntry struct {
	ID          string `json:"id"`
	URL         string `json:"url"`
	Domain      string `json:"domain"`
	FirstSeenAt string `json:"firstSeenAt"`
}

type SyncUrlsRequest struct {
	EmployeeID string    `json:"employeeId"`
	Token      string    `json:"token"`
	Entries    []UrlEntry `json:"entries"`
}

type UrlResponse struct {
	ID          string    `json:"id"`
	EmployeeID  string    `json:"employeeId"`
	URL         string    `json:"url"`
	Domain      string    `json:"domain"`
	FirstSeenAt time.Time `json:"firstSeenAt"`
	SyncedAt    *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// PHASE 2: URL Visits
// ────────────────────────────────

type UrlVisitEntry struct {
	ID               string `json:"id"`
	BrowserContextID string `json:"browserContextId"`
	UrlID            string `json:"urlId"`
	PathAndQuery     string `json:"pathAndQuery"`
	PageTitle        string `json:"pageTitle"`
	VisitedAt        string `json:"visitedAt"`
}

type SyncUrlVisitsRequest struct {
	EmployeeID string         `json:"employeeId"`
	Token      string         `json:"token"`
	Entries    []UrlVisitEntry `json:"entries"`
}

type UrlVisitResponse struct {
	ID               string    `json:"id"`
	EmployeeID       string    `json:"employeeId"`
	BrowserContextID string    `json:"browserContextId"`
	UrlID            string    `json:"urlId"`
	PathAndQuery     string    `json:"pathAndQuery"`
	PageTitle        string    `json:"pageTitle"`
	VisitedAt        time.Time `json:"visitedAt"`
	SyncedAt         *time.Time `json:"syncedAt,omitempty"`
}

// ────────────────────────────────
// Shell Commands
// ────────────────────────────────

type ShellCommandEntry struct {
	ID               string `json:"id"`
	MachineID        string `json:"machineId"`
	Timestamp        string `json:"timestamp"`
	ShellName        string `json:"shellName"`
	ShellPid         string `json:"shellPid"`
	Command          string `json:"command"`
	WorkingDirectory string `json:"workingDirectory"`
	ExitCode         string `json:"exitCode"`
	UserName         string `json:"userName"`
	Platform         string `json:"platform"`
	SessionID        string `json:"sessionId"`
}

type SyncShellCommandsRequest struct {
	EmployeeID string             `json:"employeeId"`
	Token      string             `json:"token"`
	Commands   []ShellCommandEntry `json:"commands"`
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
