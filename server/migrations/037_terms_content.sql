-- 037_terms_content.sql
-- Stores editable Terms & Conditions content per feature.
-- slug is auto-generated from heading (snake_case).
-- body is HTML — compatible with both web rich-text editor and Avalonia HTML rendering.

CREATE TABLE IF NOT EXISTS terms_content (
    id              VARCHAR(36) PRIMARY KEY DEFAULT gen_random_uuid()::text,
    slug            VARCHAR(100) NOT NULL,
    heading         TEXT NOT NULL DEFAULT '',
    body            TEXT NOT NULL DEFAULT '',
    terms_version   VARCHAR(50) NOT NULL DEFAULT '1.0',
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_terms_content_slug
    ON terms_content (slug)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_terms_content_updated
    ON terms_content (updated_at DESC);

-- Seed featured (system) content for each built-in feature
INSERT INTO terms_content (id, slug, heading, body, terms_version, is_system, sort_order)
VALUES
    ('tc-001', 'app_usage', 'Application Usage Tracking',
     '<h3>What We Track</h3><p>Alpha AI Tracker monitors which desktop applications are open and actively used on your work machine. This includes the application name, process name, and the duration each application remains in focus.</p><h3>How This Data Is Used</h3><p>Application usage data is used to generate productivity reports and help managers understand how time is allocated across tools and workflows. It is not used for punitive purposes.</p><h3>Data Retention</h3><p>Application usage records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, session data is automatically purged.</p><h3>Your Rights</h3><p>This feature is required for the Alpha AI Tracker to function. You cannot revoke consent for application usage tracking while the tracker is active on your machine.</p>',
     '1.0', true, 1),
    ('tc-002', 'browser_journey', 'Browser Journey Tracking',
     '<h3>What We Track</h3><p>When enabled, Alpha AI Tracker records the web pages you visit in supported browsers (Chrome, Firefox, Edge, Brave, Opera, and others). This includes the page URL, page title, and the time spent on each page.</p><h3>How This Data Is Used</h3><p>Browser journey data helps your organization understand which websites and web applications are used for work. It can identify training needs, blocked resources, or productivity patterns.</p><h3>Incognito / Private Browsing</h3><p>By default, incognito and private browsing windows are NOT tracked. If your organization enables incognito capture, a separate consent prompt will appear before any private browsing data is collected.</p><h3>Data Retention</h3><p>Browser journey records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, journey data is automatically purged.</p><h3>Your Rights</h3><p>You may revoke consent for browser journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization''s data retention policy.</p>',
     '1.0', true, 2),
    ('tc-003', 'file_journey', 'File Explorer Journey Tracking',
     '<h3>What We Track</h3><p>When enabled, Alpha AI Tracker monitors file manager activity on your machine. This includes which folders you navigate to, and any files you create, rename, or delete through the file explorer.</p><h3>How This Data Is Used</h3><p>File journey data helps understand file organization patterns and collaboration workflows. It is used to generate insights about how your team manages documents and project files.</p><h3>What We Do NOT Track</h3><p>This feature does not read file contents, track file modifications made through applications (e.g., saving in VS Code or Word), or access files outside the directories you actively browse.</p><h3>Data Retention</h3><p>File journey records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, journey data is automatically purged.</p><h3>Your Rights</h3><p>You may revoke consent for file journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization''s data retention policy.</p>',
     '1.0', true, 3),
    ('tc-004', 'live_view', 'Live Screen Viewing',
     '<h3>What This Feature Does</h3><p>When enabled, authorized administrators can view your screen in real-time. This feature is intended for remote support, training, and collaboration scenarios.</p><h3>How This Data Is Used</h3><p>Live screen viewing is used only when an administrator explicitly initiates a viewing session. Your screen is not continuously recorded or streamed without your knowledge.</p><h3>Notification</h3><p>When an administrator initiates a live viewing session, you will receive a notification on your desktop. You will always know when your screen is being viewed.</p><h3>Your Rights</h3><p>You may revoke consent for live screen viewing at any time. Revoking consent will immediately terminate any active viewing session and prevent future sessions until you re-consent.</p>',
     '1.0', true, 4)
ON CONFLICT (slug) WHERE deleted_at IS NULL DO NOTHING;
