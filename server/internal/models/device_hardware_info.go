package models

import "time"

type DeviceHardwareInfo struct {
	ID             string    `json:"id" db:"id"`
	EmployeeID     string    `json:"employeeId" db:"employee_id"`
	DeviceID       string    `json:"deviceId" db:"device_id"`
	MacAddress     string    `json:"macAddress" db:"mac_address"`
	Hostname       string    `json:"hostname" db:"hostname"`
	OsName         string    `json:"osName" db:"os_name"`
	OsVersion      string    `json:"osVersion" db:"os_version"`
	CpuModel       string    `json:"cpuModel" db:"cpu_model"`
	CpuCores       int       `json:"cpuCores" db:"cpu_cores"`
	RamTotalMB     int64     `json:"ramTotalMb" db:"ram_total_mb"`
	StorageDevices string    `json:"storageDevices" db:"storage_devices"`
	GpuModel       string    `json:"gpuModel" db:"gpu_model"`
	GpuVramMB      int64     `json:"gpuVramMb" db:"gpu_vram_mb"`
	CollectedAt    time.Time `json:"collectedAt" db:"collected_at"`
	SyncedAt       *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt      time.Time `json:"createdAt" db:"created_at"`
}

type InstalledApplication struct {
	ID              string     `json:"id" db:"id"`
	EmployeeID      string     `json:"employeeId" db:"employee_id"`
	AppName         string     `json:"appName" db:"app_name"`
	AppVersion      string     `json:"appVersion" db:"app_version"`
	Publisher       string     `json:"publisher" db:"publisher"`
	InstallPath     string     `json:"installPath" db:"install_path"`
	InstallDate     *time.Time `json:"installDate,omitempty" db:"install_date"`
	UninstallString string     `json:"uninstallString" db:"uninstall_string"`
	ChangeType      string     `json:"changeType" db:"change_type"`
	DetectedAt      time.Time  `json:"detectedAt" db:"detected_at"`
	SyncedAt        *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt       time.Time  `json:"createdAt" db:"created_at"`
}

type NetworkInfo struct {
	ID                   string    `json:"id" db:"id"`
	EmployeeID           string    `json:"employeeId" db:"employee_id"`
	PublicIP             string    `json:"publicIp" db:"public_ip"`
	PrivateIP            string    `json:"privateIp" db:"private_ip"`
	MacAddress           string    `json:"macAddress" db:"mac_address"`
	NetworkInterfaceName string    `json:"networkInterfaceName" db:"network_interface_name"`
	CollectedAt          time.Time `json:"collectedAt" db:"collected_at"`
	SyncedAt             *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt            time.Time `json:"createdAt" db:"created_at"`
}

type SessionEvent struct {
	ID         string    `json:"id" db:"id"`
	EmployeeID string    `json:"employeeId" db:"employee_id"`
	EventType  string    `json:"eventType" db:"event_type"`
	OsUsername string    `json:"osUsername" db:"os_username"`
	EventAt    time.Time `json:"eventAt" db:"event_at"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt  time.Time `json:"createdAt" db:"created_at"`
}

type ShellCommand struct {
	ID                string     `json:"id" db:"id"`
	EmployeeID        string     `json:"employeeId" db:"employee_id"`
	MachineID         string     `json:"machineId" db:"machine_id"`
	Timestamp         time.Time  `json:"timestamp" db:"timestamp"`
	ShellName         string     `json:"shellName" db:"shell_name"`
	ShellPid          string     `json:"shellPid" db:"shell_pid"`
	Command           string     `json:"command" db:"command"`
	WorkingDirectory  string     `json:"workingDirectory" db:"working_directory"`
	ExitCode          string     `json:"exitCode" db:"exit_code"`
	UserName          string     `json:"userName" db:"user_name"`
	Platform          string     `json:"platform" db:"platform"`
	SessionID         string     `json:"sessionId" db:"session_id"`
	SyncedAt          *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt         time.Time  `json:"createdAt" db:"created_at"`
}
