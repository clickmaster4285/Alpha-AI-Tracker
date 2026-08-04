package models

import "time"

// EmployeeInstalledApplication is a junction row linking an employee to a deduplicated
// catalog application (installed_applications keyed by app_fingerprint). Per-install
// metadata that can legitimately differ between machines (version, path, date) lives here.
type EmployeeInstalledApplication struct {
	ID                     int64      `json:"id" db:"id"`
	EmployeeID             string     `json:"employeeId" db:"employee_id"`
	InstalledApplicationID string     `json:"installedApplicationId" db:"installed_application_id"`
	AppVersion             string     `json:"appVersion" db:"app_version"`
	Publisher              string     `json:"publisher" db:"publisher"`
	InstallPath            string     `json:"installPath" db:"install_path"`
	InstallDate            *time.Time `json:"installDate,omitempty" db:"install_date"`
	FirstSeenAt            time.Time  `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt             time.Time  `json:"lastSeenAt" db:"last_seen_at"`
	IsActive               bool       `json:"isActive" db:"is_active"`
}

// EmployeeInstalledPackage is the package-table analogue of EmployeeInstalledApplication.
type EmployeeInstalledPackage struct {
	ID                 int64     `json:"id" db:"id"`
	EmployeeID         string    `json:"employeeId" db:"employee_id"`
	InstalledPackageID string    `json:"installedPackageId" db:"installed_package_id"`
	Version            string    `json:"version" db:"version"`
	Publisher          string    `json:"publisher" db:"publisher"`
	InstallPath        string    `json:"installPath" db:"install_path"`
	FirstSeenAt        time.Time `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt         time.Time `json:"lastSeenAt" db:"last_seen_at"`
	IsActive           bool      `json:"isActive" db:"is_active"`
}
