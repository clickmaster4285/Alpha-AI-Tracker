-- 037_terms_content.sql
-- Stores the editable Terms & Conditions content per feature.

CREATE TABLE IF NOT EXISTS terms_content (
    id              VARCHAR(36) PRIMARY KEY,
    feature_id      VARCHAR(50) NOT NULL,
    heading         TEXT NOT NULL DEFAULT '',
    body            TEXT NOT NULL DEFAULT '',
    terms_version   VARCHAR(50) NOT NULL DEFAULT '1.0',
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_terms_content_feature
    ON terms_content (feature_id)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_terms_content_updated
    ON terms_content (updated_at DESC);

-- Seed default content for each feature
INSERT INTO terms_content (id, feature_id, heading, body, terms_version)
VALUES
    ('tc-001', 'app_usage', 'Application Usage Tracking', 'Alpha AI Tracker monitors which desktop applications are open and actively used on your work machine. This includes the application name, process name, and the duration each application remains in focus.', '1.0'),
    ('tc-002', 'browser_journey', 'Browser Journey Tracking', 'When enabled, Alpha AI Tracker records the web pages you visit in supported browsers (Chrome, Firefox, Edge, Brave, Opera, and others). This includes the page URL, page title, and the time spent on each page.', '1.0'),
    ('tc-003', 'file_journey', 'File Explorer Journey Tracking', 'When enabled, Alpha AI Tracker monitors file manager activity on your machine. This includes which folders you navigate to, and any files you create, rename, or delete through the file explorer.', '1.0'),
    ('tc-004', 'live_view', 'Live Screen Viewing', 'When enabled, authorized administrators can view your screen in real-time. This feature is intended for remote support, training, and collaboration scenarios.', '1.0')
ON CONFLICT (feature_id) WHERE deleted_at IS NULL DO NOTHING;
