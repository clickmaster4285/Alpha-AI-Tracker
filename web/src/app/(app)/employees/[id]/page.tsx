'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { useParams } from 'next/navigation';
import { motion } from 'framer-motion';
import {
  ArrowLeft, Cpu, MemoryStick, Monitor, HardDrive, Wifi, AppWindow,
  Package, Usb, Activity, Loader2, Search, Key, Copy, Check,
  Building2, Mail, Clock, Globe, ShieldCheck, ShieldX, Fingerprint,
  Headphones, Keyboard, Camera, Network, CalendarDays, Layers,
} from 'lucide-react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  employeesApi, appSessionsApi,
  type AppSession, type DeviceHardwareDetail, type StorageDeviceDetail, type NetworkInfoDetail,
} from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

type TabKey = 'hardware' | 'applications' | 'packages' | 'peripherals' | 'permissions' | 'activity';

const TABS: { key: TabKey; label: string; icon: React.ElementType }[] = [
  { key: 'hardware', label: 'Hardware', icon: Monitor },
  { key: 'applications', label: 'Applications', icon: AppWindow },
  { key: 'packages', label: 'Packages', icon: Package },
  { key: 'peripherals', label: 'Peripherals', icon: Usb },
  { key: 'permissions', label: 'Permissions', icon: ShieldCheck },
  { key: 'activity', label: 'Activity', icon: Activity },
];

export default function UserDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params.id;

  const [tab, setTab] = useState<TabKey>('hardware');
  const [appSearch, setAppSearch] = useState('');
  const [pkgSearch, setPkgSearch] = useState('');
  const [showSecret, setShowSecret] = useState(false);
  const [secretValue, setSecretValue] = useState('');
  const [copied, setCopied] = useState(false);

  // ── Employee detail (aggregate machine picture) ──
  const { data, isLoading, error } = useQuery({
    queryKey: ['employee-detail', id],
    queryFn: () => employeesApi.detail(id),
    enabled: !!id,
  });

  const employeeId = data?.employee.employeeId;

  // ── Recent activity sessions for the Activity tab (fetched lazily when the tab opens) ──
  const { data: recentData, isLoading: sessionsLoading } = useQuery({
    queryKey: ['app-sessions', { employeeId, perPage: 10 }],
    queryFn: () => appSessionsApi.list({ employeeId, perPage: 10 }),
    enabled: !!employeeId && tab === 'activity',
  });

  const secretMutation = useMutation({
    mutationFn: () => employeesApi.generateSecret(id),
    onSuccess: (d) => {
      setSecretValue(d.secret);
      setCopied(false);
      setShowSecret(true);
      toast.success('Login secret generated!', {
        description: 'Share it with the employee — it expires in 5 minutes and can only be used once.',
        duration: 8000,
      });
    },
    onError: (err: Error) => toast.error('Failed to generate secret', { description: err.message }),
  });

  const handleCopySecret = () => {
    navigator.clipboard.writeText(secretValue);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const filteredApps = useMemo(() => {
    const q = appSearch.trim().toLowerCase();
    if (!q || !data) return data?.applications || [];
    return data.applications.filter(a =>
      a.appName.toLowerCase().includes(q) ||
      a.publisher.toLowerCase().includes(q) ||
      (a.binaryName || '').toLowerCase().includes(q),
    );
  }, [data, appSearch]);

  const filteredPackages = useMemo(() => {
    const q = pkgSearch.trim().toLowerCase();
    if (!q || !data) return data?.packages || [];
    return data.packages.filter(p =>
      p.packageName.toLowerCase().includes(q) ||
      p.category.toLowerCase().includes(q) ||
      p.sourceManager.toLowerCase().includes(q),
    );
  }, [data, pkgSearch]);

  // ── Loading ──
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Loading employee details...</p>
        </div>
      </div>
    );
  }

  // ── Error / not found ──
  if (error || !data) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Failed to load employee</p>
          <p className="text-sm text-muted-foreground mb-4">{error ? (error as Error).message : 'Employee not found'}</p>
          <Link href="/employees" className="text-sm text-primary hover:underline">
            Back to Employees
          </Link>
        </div>
      </div>
    );
  }

  const { employee, deviceHardware, storageDevices, networkInfo, applications, packages, hardwareDevices, permissions, stats } = data;

  return (
    <div className="space-y-5 animate-fade-in">
      {/* Breadcrumb */}
      <div className="flex items-center gap-2 text-sm">
        <Link href="/employees" className="flex items-center gap-1 text-muted-foreground hover:text-foreground transition-colors">
          <ArrowLeft className="w-3.5 h-3.5" /> Employees
        </Link>
        <span className="text-muted-foreground">/</span>
        <span className="font-medium text-foreground">{employee.name}</span>
      </div>

      {/* ── Profile hero ── */}
      <div className="rounded-xl border border-border bg-card overflow-hidden shadow-card">
        {/* <div className="h-24 gradient-primary "> */}
          {/* <div className="absolute inset-0 opacity-20 bg-[radial-gradient(circle_at_70%_20%,white,transparent_60%)]" /> */}
        {/* </div> */}
        <div className="p-6 ">
          <div className="flex flex-col sm:flex-row sm:items-end gap-4 ">
            <div
              className="w-20 h-20 rounded-2xl flex items-center justify-center text-2xl font-bold text-primary-foreground ring-4 ring-card shadow-card-hover flex-shrink-0"
              style={{ backgroundColor: employee.avatarColor || '#7C3AED' }}
            >
              {employee.avatar || employee.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex flex-wrap items-center gap-2 mt-8 sm:mt-0">
                <h1 className="text-2xl font-bold text-foreground font-display">{employee.name}</h1>
                <span className="text-xs font-mono px-2 py-0.5 rounded-md bg-muted text-muted-foreground">{employee.employeeId}</span>
                <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium flex items-center gap-1.5 ${
                  employee.trackingStatus === 'tracked' ? 'bg-success/15 text-success' : 'bg-warning/15 text-warning'
                }`}>
                  <span className={`w-1.5 h-1.5 rounded-full ${employee.isOnline ? 'bg-success animate-pulse-soft' : 'bg-muted-foreground'}`} />
                  {employee.isOnline ? 'Online' : 'Offline'} · {employee.trackingStatus === 'tracked' ? 'Tracked' : 'Untracked'}
                </span>
              </div>
              <div className="flex flex-wrap items-center gap-x-5 gap-y-1 mt-2 text-sm text-muted-foreground">
                <span className="flex items-center gap-1.5"><Mail className="w-3.5 h-3.5" /> {employee.email || '—'}</span>
                <span className="flex items-center gap-1.5"><Building2 className="w-3.5 h-3.5" /> {employee.department}</span>
                <span className="flex items-center gap-1.5"><Clock className="w-3.5 h-3.5" /> {employee.shift} shift</span>
                <span className="flex items-center gap-1.5"><CalendarDays className="w-3.5 h-3.5" /> Joined {formatDate(employee.createdAt)}</span>
                <span className="flex items-center gap-1.5"><Activity className="w-3.5 h-3.5" /> Last active {stats.lastActivityAt ? formatDateTime(stats.lastActivityAt) : '—'}</span>
              </div>
            </div>
            <button
              onClick={() => secretMutation.mutate()}
              disabled={secretMutation.isPending}
              className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity disabled:opacity-50 flex-shrink-0"
            >
              {secretMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Key className="w-4 h-4" />}
              Generate Login Secret
            </button>
          </div>
        </div>
      </div>

      {/* ── Stat tiles ── */}
      <div className="grid grid-cols-2 sm:grid-cols-3 xl:grid-cols-6 gap-3">
        <StatTile icon={Activity} label="Total Sessions" value={stats.totalSessions} accent="primary" />
        <StatTile icon={Monitor} label="Open Now" value={stats.openSessions} accent="success" />
        <StatTile icon={Layers} label="Tracked Items" value={stats.totalItems} accent="info" />
        <StatTile icon={AppWindow} label="Installed Apps" value={applications.length} accent="primary" />
        <StatTile icon={Package} label="Packages" value={packages.length} accent="warning" />
        <StatTile icon={Usb} label="Peripherals" value={hardwareDevices.length} accent="info" />
      </div>

      {/* ── Tabs ── */}
      <div role="tablist" aria-label="Employee details" className="flex items-center gap-1 overflow-x-auto pb-1 -mx-1 px-1">
        {TABS.map(({ key, label, icon: Icon }) => (
          <button
            key={key}
            role="tab"
            aria-selected={tab === key}
            onClick={() => setTab(key)}
            className={`flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium whitespace-nowrap transition-all duration-200 ${
              tab === key
                ? 'bg-primary text-primary-foreground shadow-card-hover'
                : 'text-muted-foreground hover:text-foreground hover:bg-muted'
            }`}
          >
            <Icon className="w-4 h-4" />
            {label}
            <span className={`text-xs px-1.5 py-0.5 rounded-md ${tab === key ? 'bg-white/20' : 'bg-muted text-muted-foreground'}`}>
              {key === 'applications' ? applications.length : key === 'packages' ? packages.length : key === 'peripherals' ? hardwareDevices.length : key === 'permissions' ? permissions.length : ''}
            </span>
          </button>
        ))}
      </div>

      <motion.div key={tab} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.25 }}>
        {tab === 'hardware' && (
          <HardwareTab
            deviceHardware={deviceHardware}
            storageDevices={storageDevices}
            networkInfo={networkInfo}
            appStatus={data.appStatus}
          />
        )}
        {tab === 'applications' && (
          <div className="space-y-3">
            <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-sm">
              <Search className="w-4 h-4 text-muted-foreground" />
              <input
                value={appSearch}
                onChange={e => setAppSearch(e.target.value)}
                placeholder="Search applications..."
                className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
              />
            </div>
            <InventoryTable
              headers={['Application', 'Version', 'Publisher', 'Installed', 'Source']}
              empty={filteredApps.length === 0}
              emptyText={appSearch ? 'No applications match your search' : 'No installed applications synced yet'}
            >
              {filteredApps.map(app => (
                <tr key={app.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                        <AppWindow className="w-4 h-4 text-primary" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-foreground truncate">{app.appName}</p>
                        {app.binaryName && <p className="text-xs text-muted-foreground font-mono truncate">{app.binaryName}</p>}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{app.version || '—'}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{app.publisher || '—'}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{app.installDate ? formatDate(app.installDate) : 'Unknown'}</td>
                  <td className="px-4 py-3">
                    {app.isBrowser ? (
                      <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-info/15 text-info">Browser</span>
                    ) : (
                      <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground">Application</span>
                    )}
                  </td>
                </tr>
              ))}
            </InventoryTable>
          </div>
        )}
        {tab === 'packages' && (
          <div className="space-y-3">
            <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-sm">
              <Search className="w-4 h-4 text-muted-foreground" />
              <input
                value={pkgSearch}
                onChange={e => setPkgSearch(e.target.value)}
                placeholder="Search packages..."
                className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
              />
            </div>
            <InventoryTable
              headers={['Package', 'Version', 'Category', 'Source Manager', 'Publisher']}
              empty={filteredPackages.length === 0}
              emptyText={pkgSearch ? 'No packages match your search' : 'No packages synced yet'}
            >
              {filteredPackages.map(pkg => (
                <tr key={pkg.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <div className="w-8 h-8 rounded-lg bg-warning/10 flex items-center justify-center flex-shrink-0">
                        <Package className="w-4 h-4 text-warning" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-foreground truncate">{pkg.packageName}</p>
                        {pkg.description && <p className="text-xs text-muted-foreground truncate">{pkg.description}</p>}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{pkg.version || '—'}</td>
                  <td className="px-4 py-3">
                    <span className="px-2 py-0.5 rounded-full text-xs font-medium capitalize bg-primary/10 text-primary">{pkg.category || 'tool'}</span>
                  </td>
                  <td className="px-4 py-3">
                    <span className="px-2 py-0.5 rounded-md text-xs font-mono bg-muted text-muted-foreground">{pkg.sourceManager || '—'}</span>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{pkg.publisher || '—'}</td>
                </tr>
              ))}
            </InventoryTable>
          </div>
        )}
        {tab === 'peripherals' && (
          hardwareDevices.length === 0 ? (
            <EmptyState icon={Usb} text="No peripheral devices recorded yet" />
          ) : (
            <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-3">
              {hardwareDevices.map((device, i) => (
                <motion.div
                  key={device.id}
                  initial={{ opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ delay: i * 0.03 }}
                  className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow"
                >
                  <div className="flex items-start gap-3">
                    <div className="w-10 h-10 rounded-xl bg-info/10 flex items-center justify-center flex-shrink-0">
                      <DeviceClassIcon deviceClass={device.deviceClass} />
                    </div>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-semibold text-foreground truncate">{device.product || device.deviceClass || 'Device'}</p>
                      {device.vendor && <p className="text-xs text-muted-foreground truncate">{device.vendor}</p>}
                    </div>
                    <span className={`flex items-center gap-1.5 text-xs font-medium whitespace-nowrap mt-0.5 ${
                      device.unpluggedAt ? 'text-muted-foreground' : 'text-success'
                    }`}>
                      <span className={`w-2 h-2 rounded-full ${device.unpluggedAt ? 'bg-muted-foreground' : 'bg-success animate-pulse-soft'}`} />
                      {device.unpluggedAt ? 'Disconnected' : 'Connected'}
                    </span>
                  </div>
                  <div className="mt-3 pt-3 border-t border-border space-y-1">
                    <div className="flex justify-between text-xs">
                      <span className="text-muted-foreground capitalize">{device.deviceClass || 'other'}</span>
                      <span className="text-muted-foreground">Plugged {formatDateShort(device.pluggedAt)}</span>
                    </div>
                    {device.serial && (
                      <div className="flex justify-between text-xs">
                        <span className="text-muted-foreground">Serial</span>
                        <span className="font-mono text-foreground truncate ml-3">{device.serial}</span>
                      </div>
                    )}
                    {device.unpluggedAt && (
                      <div className="flex justify-between text-xs">
                        <span className="text-muted-foreground">Unplugged</span>
                        <span className="text-foreground">{formatDateShort(device.unpluggedAt)}</span>
                      </div>
                    )}
                  </div>
                </motion.div>
              ))}
            </div>
          )
        )}
        {tab === 'permissions' && (
          <InventoryTable
            headers={['Permission Method', 'Platform', 'Status', 'Last Checked', 'Details']}
            empty={permissions.length === 0}
            emptyText="No permission checks recorded yet"
          >
              {permissions.map(perm => (
                <tr key={perm.checkId} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      {perm.works ? <ShieldCheck className="w-4 h-4 text-success" /> : <ShieldX className="w-4 h-4 text-destructive" />}
                      <span className="text-sm font-medium text-foreground">{perm.method || perm.checkId}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <span className="px-2 py-0.5 rounded-md text-xs font-mono bg-muted text-muted-foreground capitalize">{perm.platform || '—'}</span>
                  </td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium ${perm.works ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>
                      {perm.works ? 'Granted' : 'Blocked'}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(perm.checkedAt)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground truncate max-w-[260px]">{perm.details || '—'}</td>              </tr>
            ))}
          </InventoryTable>
        )}
        {tab === 'activity' && (
          <ActivityTab sessions={recentData?.data || []} loading={sessionsLoading} />
        )}
      </motion.div>

      {/* ── Secret dialog ── */}
      <Dialog open={showSecret} onOpenChange={(open) => { if (!open) { setShowSecret(false); setSecretValue(''); setCopied(false); } }}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Login Secret Generated</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <p className="text-sm text-muted-foreground">
              Share this secret with {employee.name}. It expires in 5 minutes and can only be used once.
            </p>
            {secretMutation.isPending ? (
              <div className="flex items-center justify-center py-6">
                <Loader2 className="w-6 h-6 animate-spin text-primary" />
              </div>
            ) : secretValue ? (
              <div className="flex items-center gap-2 bg-background border border-border rounded-lg p-3">
                <code className="flex-1 text-sm font-mono font-bold text-foreground select-all">{secretValue}</code>
                <button onClick={handleCopySecret} className="p-2 rounded-lg hover:bg-muted transition-colors" title="Copy secret">
                  {copied ? <Check className="w-4 h-4 text-success" /> : <Copy className="w-4 h-4 text-muted-foreground" />}
                </button>
              </div>
            ) : (
              <p className="text-sm text-destructive">Failed to generate secret. Try again.</p>
            )}
            <button
              onClick={() => { setShowSecret(false); setSecretValue(''); setCopied(false); }}
              className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
            >
              Close
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ─────────────────────────────
// Sub-components
// ─────────────────────────────

function HardwareTab({ deviceHardware, storageDevices, networkInfo, appStatus }: {
  deviceHardware?: DeviceHardwareDetail;
  storageDevices: StorageDeviceDetail[];
  networkInfo?: NetworkInfoDetail;
  appStatus: Record<string, string>;
}) {
  const specs = [
    { icon: Cpu, label: 'Processor', value: deviceHardware?.cpuModel ? `${deviceHardware.cpuModel} · ${deviceHardware.cpuCores} cores` : '—' },
    { icon: MemoryStick, label: 'Memory', value: deviceHardware ? formatMb(deviceHardware.ramTotalMb) : '—' },
    { icon: Monitor, label: 'Graphics', value: deviceHardware?.gpuModel ? (deviceHardware.gpuVramMb ? `${deviceHardware.gpuModel} · ${formatMb(deviceHardware.gpuVramMb)}` : deviceHardware.gpuModel) : '—' },
    { icon: Globe, label: 'Operating System', value: deviceHardware?.osName ? `${deviceHardware.osName} ${deviceHardware.osVersion}`.trim() : '—' },
    { icon: Fingerprint, label: 'Hostname', value: deviceHardware?.hostname || '—' },
    { icon: Network, label: 'MAC Address', value: deviceHardware?.macAddress || '—' },
    { icon: Clock, label: 'Snapshot Time', value: deviceHardware?.collectedAt ? formatDateTime(deviceHardware.collectedAt) : '—' },
  ];

  return (
    <div className="space-y-4">
      {!deviceHardware && (
        <EmptyState icon={Monitor} text="No hardware snapshot synced yet — the desktop client will upload one after login" />
      )}

      {deviceHardware && (
        <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-3">
          {specs.map(({ icon: Icon, label, value }) => (
            <div key={label} className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
              <div className="flex items-center gap-2 text-muted-foreground mb-2">
                <Icon className="w-4 h-4 text-primary" />
                <span className="text-xs font-semibold uppercase tracking-wide">{label}</span>
              </div>
              <p className="text-sm font-medium text-foreground leading-snug break-words">{value}</p>
            </div>
          ))}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Storage */}
        <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
          <div className="px-4 py-3 border-b border-border flex items-center justify-between">
            <div className="flex items-center gap-2">
              <HardDrive className="w-4 h-4 text-primary" />
              <h3 className="text-sm font-semibold text-foreground">Storage Devices</h3>
            </div>
            <span className="text-xs text-muted-foreground">{storageDevices.length} drive{storageDevices.length === 1 ? '' : 's'}</span>
          </div>
          {storageDevices.length === 0 ? (
            <p className="text-sm text-muted-foreground text-center py-8">No storage devices recorded</p>
          ) : (
            <table className="w-full">
              <tbody>
                {storageDevices.map(d => (
                  <tr key={d.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-3">
                      <p className="text-sm font-medium text-foreground truncate">{d.model || 'Unknown drive'}</p>
                      <p className="text-xs text-muted-foreground capitalize">{d.deviceType || 'storage'}</p>
                    </td>
                    <td className="px-4 py-3 text-right text-sm font-semibold text-foreground whitespace-nowrap">{formatMb(d.capacityMb)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        {/* Network + status */}
        <div className="space-y-4">
          <div className="bg-card rounded-xl border border-border shadow-card p-4">
            <div className="flex items-center gap-2 mb-3">
              <Wifi className="w-4 h-4 text-primary" />
              <h3 className="text-sm font-semibold text-foreground">Network</h3>
            </div>
            {!networkInfo ? (
              <p className="text-sm text-muted-foreground">No network snapshot synced yet</p>
            ) : (
              <div className="grid grid-cols-2 gap-3">
                <NetworkField label="Public IP" value={networkInfo.publicIp || '—'} mono />
                <NetworkField label="Private IP" value={networkInfo.privateIp || '—'} mono />
                <NetworkField label="Interface" value={networkInfo.networkInterfaceName || '—'} />
                <NetworkField label="MAC Address" value={networkInfo.macAddress || '—'} mono />
              </div>
            )}
          </div>

          {Object.keys(appStatus).length > 0 && (
            <div className="bg-card rounded-xl border border-border shadow-card p-4">
              <div className="flex items-center gap-2 mb-3">
                <Activity className="w-4 h-4 text-primary" />
                <h3 className="text-sm font-semibold text-foreground">Device Status</h3>
              </div>
              <div className="flex flex-wrap gap-2">
                {Object.entries(appStatus).map(([key, value]) => (
                  <span key={key} className="px-2 py-1 rounded-lg bg-muted text-xs">
                    <span className="text-muted-foreground">{key.replace(/_/g, ' ')}:</span>{' '}
                    <span className="font-semibold text-foreground">{value || '—'}</span>
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function ActivityTab({ sessions, loading }: { sessions: AppSession[]; loading: boolean }) {
  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }
  if (sessions.length === 0) {
    return <EmptyState icon={Activity} text="No app activity synced yet" />;
  }
  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
      <table className="w-full">
        <thead>
          <tr className="border-b border-border">
            {['Application', 'Platform', 'Started', 'Duration'].map(h => (
              <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sessions.map(s => (
            <tr key={s.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
              <td className="px-4 py-3">
                <p className="text-sm font-medium text-foreground">{s.appDisplayName || s.processName}</p>
                {s.appDisplayName && s.appDisplayName !== s.processName && (
                  <p className="text-xs text-muted-foreground font-mono">{s.processName}</p>
                )}
              </td>
              <td className="px-4 py-3">
                <span className="px-2 py-0.5 rounded-md text-xs font-mono bg-primary/10 text-primary capitalize">{s.platform || '—'}</span>
              </td>
              <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(s.startedAt)}</td>
              <td className="px-4 py-3 text-sm text-foreground">
                {formatDuration(s.startedAt, s.endedAt || new Date().toISOString())}
                {!s.endedAt && <span className="ml-2 text-xs text-success">· running</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function StatTile({ icon: Icon, label, value, accent }: {
  icon: React.ElementType; label: string; value: number; accent: 'primary' | 'success' | 'warning' | 'info';
}) {
  const accentClasses: Record<string, string> = {
    primary: 'bg-primary/10 text-primary',
    success: 'bg-success/15 text-success',
    warning: 'bg-warning/15 text-warning',
    info: 'bg-info/15 text-info',
  };
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center ${accentClasses[accent]}`}>
        <Icon className="w-4 h-4" />
      </div>
      <p className="mt-3 text-2xl font-bold text-foreground font-display">{value.toLocaleString()}</p>
      <p className="text-xs text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}

function NetworkField({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className={`text-sm font-medium text-foreground truncate ${mono ? 'font-mono' : ''}`}>{value}</p>
    </div>
  );
}

function InventoryTable({ headers, children, empty, emptyText }: {
  headers: string[]; children: React.ReactNode; empty: boolean; emptyText: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
      {empty ? (
        <div className="text-center py-12 text-muted-foreground text-sm">{emptyText}</div>
      ) : (
        <table className="w-full min-w-[640px]">
          <thead>
            <tr className="border-b border-border">
              {headers.map(h => <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>)}
            </tr>
          </thead>
          <tbody>{children}</tbody>
        </table>
      )}
    </div>
  );
}

function EmptyState({ icon: Icon, text }: { icon: React.ElementType; text: string }) {
  return (
    <div className="bg-card rounded-xl border border-border py-12 flex flex-col items-center gap-3 text-center px-6">
      <div className="w-12 h-12 rounded-xl bg-muted flex items-center justify-center">
        <Icon className="w-6 h-6 text-muted-foreground" />
      </div>
      <p className="text-sm text-muted-foreground">{text}</p>
    </div>
  );
}

function DeviceClassIcon({ deviceClass }: { deviceClass: string }) {
  const cls = deviceClass.toLowerCase();
  const icons: [string[], React.ElementType][] = [
    [['keyboard', 'input', 'mouse', 'hid'], Keyboard],
    [['audio', 'headphone', 'sound'], Headphones],
    [['display', 'monitor', 'camera'], Camera],
    [['storage', 'disk'], HardDrive],
    [['network', 'wifi', 'ethernet'], Wifi],
  ];
  const match = icons.find(([keys]) => keys.some(k => cls.includes(k)));
  const Icon = match ? match[1] : Usb;
  return <Icon className="w-5 h-5 text-info" />;
}

// ─────────────────────────────
// Formatting helpers
// ─────────────────────────────

function formatMb(mb: number): string {
  if (!mb) return '—';
  if (mb >= 1024) return `${(mb / 1024).toFixed(mb >= 1048576 ? 1 : 0)} GB`;
  return `${mb} MB`;
}

function formatDate(iso?: string): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function formatDateShort(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

function formatDuration(start: string, end: string): string {
  const diff = new Date(end).getTime() - new Date(start).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return '<1m';
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  const remaining = mins % 60;
  return `${hours}h ${remaining}m`;
}


