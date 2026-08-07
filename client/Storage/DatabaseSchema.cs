namespace client.Storage;

internal static class DatabaseSchema
{
    internal const string CreateTableSql = @"
        -- DEVICE & SYSTEM INFO TABLES

        CREATE TABLE IF NOT EXISTS device_hardware_info (
            id               TEXT PRIMARY KEY,
            mac_address      TEXT NOT NULL DEFAULT '',
            hostname         TEXT NOT NULL DEFAULT '',
            os_name          TEXT NOT NULL DEFAULT '',
            os_version       TEXT NOT NULL DEFAULT '',
            cpu_model        TEXT NOT NULL DEFAULT '',
            cpu_cores        INTEGER NOT NULL DEFAULT 0,
            ram_total_mb     INTEGER NOT NULL DEFAULT 0,
            gpu_model        TEXT NOT NULL DEFAULT '',
            gpu_vram_mb      INTEGER NOT NULL DEFAULT 0,
            collected_at     TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- Relational storage devices (replaces JSON storage_devices column from device_hardware_info)
        CREATE TABLE IF NOT EXISTS storage_devices (
            id                  TEXT PRIMARY KEY,
            device_hardware_id  TEXT NOT NULL REFERENCES device_hardware_info(id),
            device_type         TEXT NOT NULL DEFAULT '',
            model               TEXT NOT NULL DEFAULT '',
            capacity_mb         INTEGER NOT NULL DEFAULT 0,
            is_synced           INTEGER NOT NULL DEFAULT 0,
            synced_at           TEXT,
            created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_storage_devices_hw
            ON storage_devices(device_hardware_id);

        CREATE INDEX IF NOT EXISTS idx_device_hw_unsent
            ON device_hardware_info(is_synced, collected_at);

        CREATE TABLE IF NOT EXISTS installed_applications (
            id               TEXT PRIMARY KEY,
            app_name         TEXT NOT NULL UNIQUE,
            binary_name      TEXT NOT NULL DEFAULT '',
            app_version      TEXT NOT NULL DEFAULT '',
            publisher        TEXT NOT NULL DEFAULT '',
            install_path     TEXT NOT NULL DEFAULT '',
            install_date     TEXT,
            uninstall_string TEXT NOT NULL DEFAULT '',
            change_type      TEXT NOT NULL DEFAULT 'seen',
            is_browser       INTEGER NOT NULL DEFAULT 0,
            desktop_id       TEXT NOT NULL DEFAULT '',
            categories       TEXT NOT NULL DEFAULT '',
            detected_at      TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_installed_apps_unsent
            ON installed_applications(is_synced, detected_at);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_name
            ON installed_applications(app_name);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_binary
            ON installed_applications(binary_name);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_desktop_id
            ON installed_applications(desktop_id);

        CREATE TABLE IF NOT EXISTS installed_packages (
            id               TEXT PRIMARY KEY,
            package_name     TEXT NOT NULL,
            version          TEXT NOT NULL DEFAULT '',
            category         TEXT NOT NULL DEFAULT 'tool',
            source_manager   TEXT NOT NULL DEFAULT '',
            install_path     TEXT NOT NULL DEFAULT '',
            publisher        TEXT NOT NULL DEFAULT '',
            description      TEXT NOT NULL DEFAULT '',
            detected_at      TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_installed_packages_unsent
            ON installed_packages(is_synced, detected_at);

        CREATE INDEX IF NOT EXISTS idx_installed_packages_source
            ON installed_packages(source_manager);

        CREATE INDEX IF NOT EXISTS idx_installed_packages_category
            ON installed_packages(category);

        CREATE TABLE IF NOT EXISTS network_info (
            id                   TEXT PRIMARY KEY,
            public_ip            TEXT NOT NULL DEFAULT '',
            private_ip           TEXT NOT NULL DEFAULT '',
            network_interface_name TEXT NOT NULL DEFAULT '',
            collected_at         TEXT NOT NULL,
            first_seen_at        TEXT,
            last_seen_at         TEXT,
            is_current           INTEGER NOT NULL DEFAULT 1,
            is_synced            INTEGER NOT NULL DEFAULT 0,
            synced_at            TEXT,
            created_at           TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_network_unsent
            ON network_info(is_synced, collected_at);

        -- USB / peripheral / storage hotplug tracking (plug-in & plug-out history).
        -- device_class: 'storage' | 'input' | 'audio' | 'display' | 'usb' | 'power' | 'other'
        -- bus_path = udev DEVPATH (stable identity per physical plug), unplugged_at NULL while plugged.
        CREATE TABLE IF NOT EXISTS hardware_devices (
            id               TEXT PRIMARY KEY,
            device_class     TEXT NOT NULL DEFAULT 'other',
            vendor           TEXT NOT NULL DEFAULT '',
            product          TEXT NOT NULL DEFAULT '',
            serial           TEXT NOT NULL DEFAULT '',
            bus_path         TEXT NOT NULL DEFAULT '',
            device_node      TEXT NOT NULL DEFAULT '',
            plugged_at       TEXT NOT NULL,
            unplugged_at     TEXT,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_hardware_devices_class
            ON hardware_devices(device_class, plugged_at);

        CREATE INDEX IF NOT EXISTS idx_hardware_devices_open
            ON hardware_devices(unplugged_at, bus_path);

        CREATE TABLE IF NOT EXISTS session_events (
            id               TEXT PRIMARY KEY,
            event_type       TEXT NOT NULL,
            os_username      TEXT NOT NULL DEFAULT '',
            event_at         TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_session_events_unsent
            ON session_events(is_synced, event_at);

        -- APPLICATION LOGS

        CREATE TABLE IF NOT EXISTS app_sessions (
            id                  TEXT PRIMARY KEY,
            process_name        TEXT NOT NULL,
            app_display_name    TEXT NOT NULL DEFAULT '',
            started_at          TEXT NOT NULL,
            ended_at            TEXT,
            machine_id          TEXT NOT NULL DEFAULT '',
            employee_id         TEXT,
            employee_name       TEXT,
            session_id          TEXT NOT NULL DEFAULT '',
            platform            TEXT NOT NULL DEFAULT '',
            installed_app_id    TEXT REFERENCES installed_applications(id),
            installed_package_id TEXT REFERENCES installed_packages(id),
            process_id          INTEGER,
            parent_process_id   INTEGER,
            is_synced           INTEGER NOT NULL DEFAULT 0,
            synced_at           TEXT,
            created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_app_sessions_unsent
            ON app_sessions(is_synced, started_at);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_employee
            ON app_sessions(employee_id, started_at);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_process_id
            ON app_sessions(process_id);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_open
            ON app_sessions(ended_at, process_id);

        -- GENERIC APP ITEMS (replaces browser_contexts, file_explorer_contexts, urls, url_visits)
        -- Self-referencing via parent_item_id for nesting: app_session -> tab -> terminal/browser_navigation
        -- item_type: 'tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file', etc.
        -- url/domain stored separately from identifier for proper querying

        CREATE TABLE IF NOT EXISTS app_items (
            id                TEXT PRIMARY KEY,
            app_session_id    TEXT NOT NULL REFERENCES app_sessions(id),
            parent_item_id    TEXT REFERENCES app_items(id),
            item_type         TEXT NOT NULL DEFAULT '',
            title             TEXT NOT NULL DEFAULT '',
            identifier        TEXT NOT NULL DEFAULT '',
            url               TEXT NOT NULL DEFAULT '',
            domain            TEXT NOT NULL DEFAULT '',
            opened_at         TEXT NOT NULL,
            closed_at         TEXT,
            process_id        INTEGER,
            is_synced         INTEGER NOT NULL DEFAULT 0,
            synced_at         TEXT,
            created_at        TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            object_type       TEXT NOT NULL DEFAULT '',
            action            TEXT NOT NULL DEFAULT '',
            journey_id        TEXT NOT NULL DEFAULT '',
            sequence          INTEGER NOT NULL DEFAULT 0,
            previous_path     TEXT NOT NULL DEFAULT '',
            current_path      TEXT NOT NULL DEFAULT '',
            window_id         INTEGER,
            tab_id            INTEGER,
            metadata_json     TEXT NOT NULL DEFAULT '{}'
        );

        CREATE INDEX IF NOT EXISTS idx_app_items_unsent
            ON app_items(is_synced, opened_at);

        CREATE INDEX IF NOT EXISTS idx_app_items_app_session
            ON app_items(app_session_id);

        CREATE INDEX IF NOT EXISTS idx_app_items_parent
            ON app_items(parent_item_id);

        CREATE INDEX IF NOT EXISTS idx_app_items_context
            ON app_items(app_session_id, item_type, identifier);

        CREATE INDEX IF NOT EXISTS idx_app_items_journey
            ON app_items(journey_id, sequence);

        CREATE INDEX IF NOT EXISTS idx_app_items_object_action
            ON app_items(object_type, action);

        -- APP STATUS & PERMISSIONS

        CREATE TABLE IF NOT EXISTS app_status (
            key             TEXT PRIMARY KEY,
            value           TEXT NOT NULL,
            updated_at      TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE TABLE IF NOT EXISTS permission_status (
            check_id        TEXT PRIMARY KEY,
            session_id      TEXT NOT NULL,
            session_type    TEXT NOT NULL,
            platform        TEXT NOT NULL,
            checked_at      TEXT NOT NULL,
            method          TEXT NOT NULL,
            works           INTEGER NOT NULL DEFAULT 0,
            details         TEXT,
            employee_id     TEXT,
            employee_name   TEXT
        );

        CREATE TABLE IF NOT EXISTS employee_info (
            id              TEXT PRIMARY KEY,
            employee_id     TEXT NOT NULL,
            name            TEXT NOT NULL,
            email           TEXT NOT NULL,
            role            TEXT NOT NULL,
            department      TEXT NOT NULL,
            shift           TEXT,
            avatar          TEXT,
            avatar_color    TEXT,
            token           TEXT,
            logged_in_at    TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- shell_commands table intentionally removed — no longer collected
    ";

    internal const string MigrateSql = @"
        ALTER TABLE app_sessions ADD COLUMN process_id INTEGER;
        ALTER TABLE app_sessions ADD COLUMN parent_process_id INTEGER;
        ALTER TABLE app_items ADD COLUMN process_id INTEGER;
        ALTER TABLE installed_applications ADD COLUMN is_browser INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE installed_applications ADD COLUMN desktop_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installed_applications ADD COLUMN categories TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN url TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN domain TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN object_type TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN action TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN journey_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN sequence INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE app_items ADD COLUMN previous_path TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN current_path TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN window_id INTEGER;
        ALTER TABLE app_items ADD COLUMN tab_id INTEGER;
        ALTER TABLE app_items ADD COLUMN metadata_json TEXT NOT NULL DEFAULT '{}';
        ALTER TABLE app_sessions ADD COLUMN grouped_by TEXT;
        ALTER TABLE app_sessions ADD COLUMN cgroup_scope TEXT;
        ALTER TABLE app_sessions ADD COLUMN context_label TEXT;
        ALTER TABLE network_info ADD COLUMN first_seen_at TEXT;
        ALTER TABLE network_info ADD COLUMN last_seen_at TEXT;
        ALTER TABLE network_info ADD COLUMN is_current INTEGER NOT NULL DEFAULT 1;
        -- Backfill dedup: legacy databases have MULTIPLE is_current=1 rows per IP identity
        -- (one per launch — the pre-fix bug). Collapse them to the most recent per identity
        -- so the current-row contract holds going forward without a manual DB wipe.
        UPDATE network_info SET is_current = 0 WHERE is_current = 1 AND id NOT IN (
            SELECT id FROM (
                SELECT id, ROW_NUMBER() OVER (PARTITION BY public_ip, private_ip ORDER BY collected_at DESC) AS rn
                FROM network_info WHERE is_current = 1
            ) WHERE rn = 1
        );
        CREATE INDEX IF NOT EXISTS idx_network_current
            ON network_info(is_current, collected_at);
        CREATE INDEX IF NOT EXISTS idx_hardware_devices_class
            ON hardware_devices(device_class, plugged_at);
        CREATE INDEX IF NOT EXISTS idx_hardware_devices_open
            ON hardware_devices(unplugged_at, bus_path);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_hardware_devices_open_path
            ON hardware_devices(bus_path) WHERE unplugged_at IS NULL AND bus_path != '';
        DELETE FROM installed_packages
            WHERE id NOT IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (
                        PARTITION BY package_name, source_manager
                        ORDER BY detected_at DESC, id DESC
                    ) AS rn
                    FROM installed_packages
                )
                WHERE rn = 1
            );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_installed_packages_fingerprint
            ON installed_packages(package_name, source_manager);
    ";

    // PHASE 1: INSERT STATEMENTS

    internal const string InsertDeviceHardwareInfoSql = @"
        INSERT INTO device_hardware_info
            (id, mac_address, hostname, os_name, os_version, cpu_model, cpu_cores,
             ram_total_mb, gpu_model, gpu_vram_mb, collected_at)
        VALUES
            ($id, $mac_address, $hostname, $os_name, $os_version, $cpu_model, $cpu_cores,
             $ram_total_mb, $gpu_model, $gpu_vram_mb, $collected_at)
    ";

    internal const string InsertStorageDeviceSql = @"
        INSERT INTO storage_devices
            (id, device_hardware_id, device_type, model, capacity_mb)
        VALUES
            ($id, $device_hardware_id, $device_type, $model, $capacity_mb)
    ";

    internal const string MarkDeviceHardwareSentSql = @"
        UPDATE device_hardware_info
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string MarkStorageDevicesSentSql = @"
        UPDATE storage_devices
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertInstalledApplicationSql = @"
        INSERT INTO installed_applications
            (id, app_name, binary_name, app_version, publisher, install_path, install_date,
             uninstall_string, change_type, is_browser, desktop_id, categories, detected_at)
        VALUES
            ($id, $app_name, $binary_name, $app_version, $publisher, $install_path, $install_date,
             $uninstall_string, $change_type, $is_browser, $desktop_id, $categories, $detected_at)
        ON CONFLICT(app_name) DO UPDATE SET
            binary_name = COALESCE(NULLIF(excluded.binary_name, ''), installed_applications.binary_name),
            app_version = excluded.app_version,
            publisher = COALESCE(NULLIF(excluded.publisher, ''), installed_applications.publisher),
            install_path = COALESCE(NULLIF(excluded.install_path, ''), installed_applications.install_path),
            change_type = CASE WHEN installed_applications.change_type = 'installed' THEN 'installed' ELSE excluded.change_type END,
            is_browser = MAX(installed_applications.is_browser, excluded.is_browser),
            desktop_id = COALESCE(NULLIF(excluded.desktop_id, ''), installed_applications.desktop_id),
            categories = COALESCE(NULLIF(excluded.categories, ''), installed_applications.categories),
            detected_at = excluded.detected_at,
            is_synced = 0
    ";

    internal const string MarkInstalledAppsSentSql = @"
        UPDATE installed_applications
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertInstalledPackageSql = @"
        INSERT INTO installed_packages
            (id, package_name, version, category, source_manager, install_path,
             publisher, description, detected_at)
        VALUES
            ($id, $package_name, $version, $category, $source_manager, $install_path,
             $publisher, $description, $detected_at)
        ON CONFLICT(package_name, source_manager) DO UPDATE SET
            version = excluded.version,
            category = CASE WHEN excluded.category = 'tool' THEN installed_packages.category ELSE excluded.category END,
            detected_at = excluded.detected_at,
            is_synced = 0
    ";

    internal const string MarkInstalledPackagesSentSql = @"
        UPDATE installed_packages
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertNetworkInfoSql = @"
        INSERT INTO network_info
            (id, public_ip, private_ip, network_interface_name, collected_at, first_seen_at, last_seen_at, is_current)
        VALUES
            ($id, $public_ip, $private_ip, $network_interface_name, $collected_at, $first_seen_at, $last_seen_at, $is_current)
    ";

    internal const string MarkNetworkInfoSentSql = @"
        UPDATE network_info
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertHardwareDeviceSql = @"
        INSERT INTO hardware_devices
            (id, device_class, vendor, product, serial, bus_path, device_node, plugged_at)
        VALUES
            ($id, $device_class, $vendor, $product, $serial, $bus_path, $device_node, $plugged_at)
        ON CONFLICT(bus_path) WHERE unplugged_at IS NULL AND bus_path != '' DO NOTHING
    ";

    internal const string InsertSessionEventSql = @"
        INSERT INTO session_events
            (id, event_type, os_username, event_at)
        VALUES
            ($id, $event_type, $os_username, $event_at)
    ";

    internal const string MarkSessionEventsSentSql = @"
        UPDATE session_events
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    // PHASE 2: INSERT & QUERY STATEMENTS

    internal const string InsertAppSessionSql = @"
        INSERT INTO app_sessions
            (id, process_name, app_display_name, started_at, ended_at,
             machine_id, employee_id, employee_name, session_id, platform,
             installed_app_id, installed_package_id, process_id, parent_process_id,
             grouped_by, cgroup_scope, context_label)
        VALUES
            ($id, $process_name, $app_display_name, $started_at, $ended_at,
             $machine_id, $employee_id, $employee_name, $session_id, $platform,
             $installed_app_id, $installed_package_id, $process_id, $parent_process_id,
             $grouped_by, $cgroup_scope, $context_label)
        ON CONFLICT(id) DO UPDATE SET
            ended_at = COALESCE(excluded.ended_at, app_sessions.ended_at),
            parent_process_id = COALESCE(excluded.parent_process_id, app_sessions.parent_process_id)
    ";

    internal const string UpdateAppSessionEndedSql = @"
        UPDATE app_sessions
        SET ended_at = $ended_at
        WHERE id = $id
    ";

    internal const string MarkAppSessionsSentSql = @"
        UPDATE app_sessions
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    // APP ITEMS (generic child of app_sessions)

    internal const string InsertAppItemSql = @"
        INSERT INTO app_items
            (id, app_session_id, parent_item_id, item_type, title, identifier, url, domain,
             opened_at, closed_at, process_id,
             object_type, action, journey_id, sequence, previous_path, current_path,
             window_id, tab_id, metadata_json)
        VALUES
            ($id, $app_session_id, $parent_item_id, $item_type, $title, $identifier, $url, $domain,
             $opened_at, $closed_at, $process_id,
             $object_type, $action, $journey_id, $sequence, $previous_path, $current_path,
             $window_id, $tab_id, $metadata_json)
        ON CONFLICT(id) DO UPDATE SET
            title = excluded.title,
            identifier = excluded.identifier,
            url = COALESCE(NULLIF(excluded.url, ''), app_items.url),
            domain = COALESCE(NULLIF(excluded.domain, ''), app_items.domain),
            parent_item_id = COALESCE(excluded.parent_item_id, app_items.parent_item_id),
            closed_at = COALESCE(excluded.closed_at, app_items.closed_at),
            object_type = COALESCE(NULLIF(excluded.object_type, ''), app_items.object_type),
            action = COALESCE(NULLIF(excluded.action, ''), app_items.action),
            sequence = excluded.sequence,
            previous_path = COALESCE(NULLIF(excluded.previous_path, ''), app_items.previous_path),
            current_path = COALESCE(NULLIF(excluded.current_path, ''), app_items.current_path),
            metadata_json = excluded.metadata_json
    ";

    internal const string MarkAppItemsSentSql = @"
        UPDATE app_items
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string GetLastNetworkInfoSql = @"
        SELECT * FROM network_info
        WHERE is_current = 1
        ORDER BY collected_at DESC
        LIMIT 1
    ";

    // UTILITY STATEMENTS

    internal const string UpsertStatusSql = @"
        INSERT INTO app_status (key, value, updated_at)
        VALUES ($key, $value, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        ON CONFLICT(key) DO UPDATE SET
            value = excluded.value,
            updated_at = excluded.updated_at
    ";

    internal const string InsertPermissionSql = @"
        INSERT INTO permission_status
            (check_id, session_id, session_type, platform, checked_at, method, works, details, employee_id, employee_name)
        VALUES
            ($check_id, $session_id, $session_type, $platform, $checked_at, $method, $works, $details, $employee_id, $employee_name)
    ";
}
