// Application-wide constants
export const APP_NAME = "Alpha AI Tracker, Monitoring & Productivity System";
export const APP_SHORT_NAME = "Alpha AI Tracker";
// prefix used for keys in localStorage
export const STORAGE_PREFIX = "alpha_ai_tracker_";

// GitHub Releases for desktop app downloads
export const GITHUB_REPO = "AlphaDev-7/Alpha-AI-Tracker";
export const GITHUB_RELEASES_URL = `https://github.com/${GITHUB_REPO}/releases`;
export const GITHUB_LATEST_RELEASE_API = `https://api.github.com/repos/${GITHUB_REPO}/releases/latest`;

// File name patterns to match assets per platform (used by dashboard download dialog)
export const INSTALLER_PATTERNS: Record<string, string[]> = {
  windows: ['.exe'],
  linux: ['.deb'],
  macos: ['.dmg'],
};

