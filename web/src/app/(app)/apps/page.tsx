'use client';

import { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Search, MoreVertical } from 'lucide-react';

interface AppEntry {
  id: string;
  name: string;
  type: 'Productive' | 'Unproductive' | 'Neutral';
  categorizeBy: string;
  category: string;
  productivityLevel: number;
}

const SAMPLE_APPS: AppEntry[] = [
  { id: '1', name: 'Visual Studio Code', type: 'Productive', categorizeBy: 'Super Admin', category: 'Development Tools', productivityLevel: 100 },
  { id: '2', name: 'Spotlight', type: 'Productive', categorizeBy: 'Super Admin', category: 'Utilities', productivityLevel: 55 },
  { id: '3', name: 'Workpuls', type: 'Productive', categorizeBy: 'Super Admin', category: 'Utilities', productivityLevel: 100 },
  { id: '4', name: 'Youtube', type: 'Unproductive', categorizeBy: 'Super Admin', category: 'Entertainment', productivityLevel: 0 },
  { id: '5', name: 'Microsoft Excel', type: 'Neutral', categorizeBy: 'Super Admin', category: 'Development Tools', productivityLevel: 70 },
  { id: '6', name: 'Eclipse', type: 'Neutral', categorizeBy: 'Super Admin', category: 'Development Tools', productivityLevel: 65 },
  { id: '7', name: 'Docker', type: 'Productive', categorizeBy: 'Super Admin', category: 'Development Tools', productivityLevel: 100 },
  { id: '8', name: 'Electron', type: 'Productive', categorizeBy: 'Super Admin', category: 'Utilities', productivityLevel: 100 },
  { id: '9', name: 'Gnome Calculator', type: 'Unproductive', categorizeBy: 'AI', category: 'Utilities', productivityLevel: 10 },
  { id: '10', name: 'Slack', type: 'Productive', categorizeBy: 'Super Admin', category: 'Communication', productivityLevel: 85 },
  { id: '11', name: 'Figma', type: 'Productive', categorizeBy: 'Super Admin', category: 'Design Tools', productivityLevel: 95 },
  { id: '12', name: 'Postman', type: 'Productive', categorizeBy: 'Super Admin', category: 'Development Tools', productivityLevel: 90 },
];

export default function AppsAndWebsites() {
  const [apps, setApps] = useState(SAMPLE_APPS);
  const [search, setSearch] = useState('');
  const [typeFilter, setTypeFilter] = useState('');
  const [tab, setTab] = useState<'Apps' | 'Websites'>('Apps');

  const filtered = apps.filter(a => {
    const matchSearch = a.name.toLowerCase().includes(search.toLowerCase());
    const matchType = !typeFilter || a.type === typeFilter;
    return matchSearch && matchType;
  });

  const updateType = (id: string, type: AppEntry['type']) => {
    setApps(prev => prev.map(a => a.id === id ? { ...a, type } : a));
  };

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Tabs */}
      <div className="flex border-b border-border gap-1">
        {(['Apps', 'Websites'] as const).map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2.5 text-sm font-medium transition-colors relative
              ${tab === t ? 'text-primary' : 'text-muted-foreground hover:text-foreground'}`}
          >
            {t}
            {tab === t && <motion.div layoutId="app-tab" className="absolute bottom-0 left-0 right-0 h-0.5 gradient-primary rounded-full" />}
          </button>
        ))}
      </div>

      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3 flex-wrap">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search apps" className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
        </div>
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>Default Department</option>
        </select>
        <select value={typeFilter} onChange={e => setTypeFilter(e.target.value)} className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option value="">Select Type</option>
          <option value="Productive">Productive</option>
          <option value="Unproductive">Unproductive</option>
          <option value="Neutral">Neutral</option>
        </select>
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>Categorize By</option>
        </select>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[800px]">
          <thead>
            <tr className="border-b border-border">
              {['App', 'Type', 'Categorize By', 'Category', 'Productivity Level', 'Actions'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filtered.map((app, i) => (
              <motion.tr
                key={app.id}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: i * 0.03 }}
                className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
              >
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <div className="w-7 h-7 rounded bg-accent flex items-center justify-center text-[10px] font-bold text-accent-foreground">{`</>`}</div>
                    <span className="text-sm font-medium text-foreground">{app.name}</span>
                  </div>
                </td>
                <td className="px-4 py-3">
                  <select
                    value={app.type}
                    onChange={e => updateType(app.id, e.target.value as AppEntry['type'])}
                    className={`text-xs font-medium rounded-lg px-2.5 py-1.5 border-none outline-none
                      ${app.type === 'Productive' ? 'bg-success/15 text-success' : app.type === 'Unproductive' ? 'bg-destructive/15 text-destructive' : 'bg-muted text-muted-foreground'}`}
                  >
                    <option value="Productive">Productive</option>
                    <option value="Unproductive">Unproductive</option>
                    <option value="Neutral">Neutral</option>
                  </select>
                </td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{app.categorizeBy}</td>
                <td className="px-4 py-3 text-sm text-foreground">{app.category}</td>
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <div className="flex-1 h-2 bg-muted rounded-full max-w-[120px]">
                      <div className="h-full rounded-full bg-info transition-all" style={{ width: `${app.productivityLevel}%` }} />
                    </div>
                    <span className="text-xs text-foreground font-medium">{app.productivityLevel}%</span>
                  </div>
                </td>
                <td className="px-4 py-3">
                  <button className="p-1.5 rounded hover:bg-muted transition-colors">
                    <MoreVertical className="w-4 h-4 text-muted-foreground" />
                  </button>
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
