'use client';

import { Suspense, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { AppWindow, Package, Search, Loader2 } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import InventoryTable from '@/components/employees/InventoryTable';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { formatDate } from '@/lib/format';
import type { EmployeeDetail } from '@/lib/api';

type TabKey = 'applications' | 'packages';

export default function DeviceSpecsSoftware() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Installed Software"
      subtitle="Applications and packages currently installed on the employee's machine."
      icon={AppWindow}
      fetchDetail
    >
      {({ detail }) => <SoftwareBody detail={detail!} />}
    </EmployeePage>
    </Suspense>
  );
}

function SoftwareBody({ detail }: { detail: EmployeeDetail }) {
  // URL-synced filters for this view: the active tab and one search field
  // per tab. The `tab` is URL-addressable so deep links land on the right
  // surface (e.g. /device-specs/software?tab=packages&pkgSearch=npm).
  const [tabFilters, setFilters] = useUrlQueryState(
    { tab: {}, appSearch: {}, pkgSearch: {} },
    { tab: 'applications', appSearch: '', pkgSearch: '' },
  );
  const tab = (tabFilters.tab || 'applications') as TabKey;
  const setTab = (next: TabKey) => setFilters({ tab: next });
  const appSearch = tabFilters.appSearch;
  const pkgSearch = tabFilters.pkgSearch;
  const setAppSearch = (next: string) => setFilters({ appSearch: next });
  const setPkgSearch = (next: string) => setFilters({ pkgSearch: next });

  const { applications, packages } = detail;

  const filteredApps = useMemo(() => {
    const q = appSearch.trim().toLowerCase();
    if (!q) return applications;
    return applications.filter(a =>
      a.appName.toLowerCase().includes(q) ||
      a.publisher.toLowerCase().includes(q) ||
      (a.binaryName || '').toLowerCase().includes(q),
    );
  }, [applications, appSearch]);

  const filteredPackages = useMemo(() => {
    const q = pkgSearch.trim().toLowerCase();
    if (!q) return packages;
    return packages.filter(p =>
      p.packageName.toLowerCase().includes(q) ||
      p.category.toLowerCase().includes(q) ||
      p.sourceManager.toLowerCase().includes(q),
    );
  }, [packages, pkgSearch]);

  const TABS: { key: TabKey; label: string; icon: React.ElementType; count: number }[] = [
    { key: 'applications', label: 'Applications', icon: AppWindow, count: applications.length },
    { key: 'packages', label: 'Packages', icon: Package, count: packages.length },
  ];

  return (
    <div className="space-y-4">
      <div role="tablist" aria-label="Software type" className="flex items-center gap-1 overflow-x-auto pb-1 -mx-1 px-1">
        {TABS.map(({ key, label, icon: Icon, count }) => (
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
              {count}
            </span>
          </button>
        ))}
      </div>

      <motion.div key={tab} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.25 }}>
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
      </motion.div>
    </div>
  );
}
