'use client';

import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Users, Clock, TrendingUp, TrendingDown, Award, Eye, Download, Loader2, Check, Monitor, Terminal, Apple } from 'lucide-react';
import { APP_SHORT_NAME, GITHUB_RELEASES_URL, GITHUB_LATEST_RELEASE_API, INSTALLER_PATTERNS } from '@/config';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import StatsCard from '@/components/ui/StatsCard';
import { getDashboardStats, getEmployees } from '@/lib/store';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const productivityData = [
  { day: 'Mon', productive: 8, unproductive: 3, idle: 1 },
  { day: 'Tue', productive: 7, unproductive: 4, idle: 1.5 },
  { day: 'Wed', productive: 9, unproductive: 2, idle: 0.5 },
  { day: 'Thu', productive: 6, unproductive: 5, idle: 2 },
  { day: 'Fri', productive: 10, unproductive: 1, idle: 0.5 },
  { day: 'Sat', productive: 3, unproductive: 1, idle: 0 },
  { day: 'Sun', productive: 0, unproductive: 0, idle: 0 },
];

const OS_OPTIONS = [
  { id: 'windows', label: 'Windows', icon: Monitor, desc: '.exe installer' },
  { id: 'linux', label: 'Linux', icon: Terminal, desc: '.deb package' },
  { id: 'macos', label: 'macOS', icon: Apple, desc: '.dmg image' },
] as const;

export default function Dashboard() {
  const stats = useMemo(() => getDashboardStats(), []);
  const employees = useMemo(() => getEmployees(), []);
  const bestPerformer = employees[0];

  // Download dialog state
  const [showDownload, setShowDownload] = useState(false);
  const [selectedOs, setSelectedOs] = useState<string | null>(null);
  const [downloadState, setDownloadState] = useState<'idle' | 'fetching' | 'ready' | 'error'>('idle');
  const [downloadUrl, setDownloadUrl] = useState('');

  const handleOsSelect = async (osId: string) => {
    setSelectedOs(osId);
    setDownloadState('fetching');

    try {
      // Fetch latest release from GitHub API
      const res = await fetch(GITHUB_LATEST_RELEASE_API);
      if (!res.ok) throw new Error('Failed to fetch latest release');

      const release = await res.json();
      const patterns = INSTALLER_PATTERNS[osId] || [];
      const asset = release.assets.find((a: any) =>
        patterns.some((p: string) => a.name.toLowerCase().includes(p))
      );

      if (asset?.browser_download_url) {
        setDownloadUrl(asset.browser_download_url);
        setDownloadState('ready');
      } else if (release.html_url) {
        // Fallback: go to the release page
        setDownloadUrl(release.html_url);
        setDownloadState('ready');
      } else {
        throw new Error('No download URL found');
      }
    } catch {
      // Fallback to the releases page
      setDownloadUrl(GITHUB_RELEASES_URL);
      setDownloadState('ready');
    }
  };

  const handleDownload = () => {
    if (downloadUrl) {
      window.open(downloadUrl, '_blank');
    }
  };

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Download banner */}
      <motion.div
        initial={{ opacity: 0, y: -10 }}
        animate={{ opacity: 1, y: 0 }}
        className="bg-card rounded-xl border border-border p-5 flex flex-col lg:flex-row items-start lg:items-center justify-between gap-4"
      >
        <div className="flex items-start gap-4">
          <div className="w-12 h-12 rounded-xl gradient-primary flex items-center justify-center flex-shrink-0">
            <TrendingUp className="w-6 h-6 text-primary-foreground" />
          </div>
          <div>
            <h2 className="font-display font-bold text-foreground text-lg">Download {APP_SHORT_NAME} Desktop App</h2>
            <p className="text-sm text-muted-foreground mt-0.5">Download and install the {APP_SHORT_NAME} desktop app to access all features.</p>
            <p className="text-xs text-warning mt-1 font-medium">Note: To uninstall/quit the {APP_SHORT_NAME} app, please use this password ****************</p>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <button className="px-4 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity">Demo Videos</button>
          <button className="px-4 py-2 rounded-lg border border-primary text-primary text-sm font-medium hover:bg-accent transition-colors">Installation Guide</button>
          <button
            onClick={() => { setShowDownload(true); setSelectedOs(null); setDownloadState('idle'); }}
            className="px-4 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity flex items-center gap-2"
          >
            <Download className="w-4 h-4" /> Download App
          </button>
        </div>
      </motion.div>

      {/* Stats */}
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard
          title="Total Employees"
          value={stats.totalEmployees}
          icon={Users}
          subtitle={`Tracked: ${stats.trackedCount}  Untracked: ${stats.untrackedCount}`}
          delay={0.05}
        />
        <StatsCard title="Total Idle Time" value={stats.totalIdleTime} icon={Clock} change={stats.idleChange} delay={0.1} />
        <StatsCard title="Total Productive Hours" value={stats.totalProductiveHours} icon={TrendingUp} change={stats.productiveChange} delay={0.15} />
        <StatsCard title="Total Unproductive Hours" value={stats.totalUnproductiveHours} icon={TrendingDown} change={stats.unproductiveChange} delay={0.2} />
      </div>

      {/* Best Performance */}
      {bestPerformer && (
        <motion.div
          initial={{ opacity: 0, y: 10 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ delay: 0.25 }}
          className="bg-card rounded-xl border border-border p-5 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4"
        >
          <div className="flex items-center gap-4">
            <div>
              <div className="flex items-center gap-2 mb-1">
                <Award className="w-5 h-5 text-warning" />
                <h3 className="font-display font-bold text-foreground">Best Performance</h3>
              </div>
              <p className="text-sm text-muted-foreground">Feb 24 - Mar 03, 2026</p>
            </div>
            <div className="flex items-center gap-3 ml-4">
              <div className="w-11 h-11 rounded-full flex items-center justify-center text-primary-foreground font-bold" style={{ backgroundColor: bestPerformer.avatarColor }}>
                {bestPerformer.avatar}
              </div>
              <div>
                <p className="font-semibold text-foreground">{bestPerformer.name}</p>
                <p className="text-xs text-muted-foreground">{bestPerformer.department}</p>
              </div>
            </div>
          </div>
          <button className="px-5 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity flex items-center gap-2">
            <Eye className="w-4 h-4" /> View All
          </button>
        </motion.div>
      )}

      {/* Chart */}
      <motion.div
        initial={{ opacity: 0, y: 10 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ delay: 0.3 }}
        className="bg-card rounded-xl border border-border p-5"
      >
        <h3 className="font-display font-bold text-foreground mb-4">Productive / Unproductive</h3>
        <div className="h-[300px]">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={productivityData}>
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
              <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <Tooltip
                contentStyle={{
                  backgroundColor: 'hsl(var(--card))',
                  border: '1px solid hsl(var(--border))',
                  borderRadius: '0.5rem',
                  fontSize: '0.875rem',
                }}
              />
              <Legend />
              <Bar dataKey="productive" fill="hsl(152, 60%, 45%)" radius={[4, 4, 0, 0]} />
              <Bar dataKey="unproductive" fill="hsl(0, 72%, 55%)" radius={[4, 4, 0, 0]} />
              <Bar dataKey="idle" fill="hsl(38, 92%, 55%)" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </motion.div>

      {/* Download Dialog */}
      <Dialog open={showDownload} onOpenChange={setShowDownload}>
        <DialogContent className="bg-card sm:max-w-[480px]">
          <DialogHeader>
            <DialogTitle className="font-display text-lg">Download Desktop App</DialogTitle>
          </DialogHeader>
          <div className="mt-2 space-y-4">
            <p className="text-sm text-muted-foreground">
              Select your operating system to download the latest {APP_SHORT_NAME} desktop client.
            </p>

            {/* OS Selection */}
            <div className="grid grid-cols-3 gap-3">
              {OS_OPTIONS.map(os => {
                const Icon = os.icon;
                const isSelected = selectedOs === os.id;
                const isLoading = isSelected && downloadState === 'fetching';
                const isReady = isSelected && downloadState === 'ready';

                return (
                  <button
                    key={os.id}
                    onClick={() => handleOsSelect(os.id)}
                    disabled={downloadState === 'fetching'}
                    className={`relative flex flex-col items-center gap-2 p-4 rounded-xl border-2 transition-all
                      ${isSelected
                        ? 'border-primary bg-primary/5'
                        : 'border-border hover:border-primary/50 hover:bg-accent/50'
                      }
                      ${isLoading ? 'opacity-70 cursor-wait' : 'cursor-pointer'}
                    `}
                  >
                    {isLoading && (
                      <div className="absolute -top-1 -right-1">
                        <Loader2 className="w-4 h-4 animate-spin text-primary" />
                      </div>
                    )}
                    {isReady && (
                      <div className="absolute -top-1 -right-1">
                        <Check className="w-4 h-4 text-success" />
                      </div>
                    )}
                    <Icon className="w-8 h-8 text-foreground" />
                    <span className="text-sm font-medium text-foreground">{os.label}</span>
                    <span className="text-[10px] text-muted-foreground">{os.desc}</span>
                  </button>
                );
              })}
            </div>

            {/* Download button */}
            {downloadState === 'ready' && (
              <motion.div
                initial={{ opacity: 0, y: 10 }}
                animate={{ opacity: 1, y: 0 }}
                className="space-y-3"
              >
                <div className="bg-success/10 border border-success/20 rounded-lg p-3 text-sm text-center">
                  <p className="text-success font-medium">
                    {selectedOs === 'windows' && 'Windows installer ready'}
                    {selectedOs === 'linux' && 'Linux package ready'}
                    {selectedOs === 'macos' && 'macOS package ready'}
                  </p>
                  <p className="text-muted-foreground text-xs mt-1">
                    You will be redirected to the download.
                  </p>
                </div>
                <button
                  onClick={handleDownload}
                  className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity flex items-center justify-center gap-2"
                >
                  <Download className="w-4 h-4" />
                  Download Now
                </button>
              </motion.div>
            )}

            {/* Error / Fallback */}
            {downloadState === 'error' && (
              <div className="bg-destructive/10 border border-destructive/20 rounded-lg p-3 text-sm text-center">
                <p className="text-destructive font-medium">Could not fetch latest release</p>
                <a
                  href={GITHUB_RELEASES_URL}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-primary hover:underline text-xs mt-1 inline-block"
                >
                  Visit GitHub Releases instead
                </a>
              </div>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}


