'use client';

import React, { useState, useMemo } from 'react';
import { motion } from 'framer-motion';
import { Shield, RotateCcw, Check, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { usePermissions, ALL_MODULES, ALL_ROLES, Permission } from '@/lib/permissions';
import { getRoleName, UserRole } from '@/lib/auth';
import { toast } from '@/hooks/use-toast';

const PERMISSION_OPTIONS: { value: Permission; label: string; color: string }[] = [
  { value: 'full', label: 'Full', color: 'bg-success/15 text-success border-success/30' },
  { value: 'view', label: 'View', color: 'bg-info/15 text-info border-info/30' },
  { value: 'self', label: 'Self', color: 'bg-warning/15 text-warning border-warning/30' },
  { value: 'config', label: 'Config', color: 'bg-primary/15 text-primary border-primary/30' },
  { value: 'none', label: 'None', color: 'bg-muted text-muted-foreground border-border' },
];

const EDITABLE_ROLES: UserRole[] = ['hr_admin', 'manager', 'employee', 'security_analyst', 'it_admin', 'auditor'];

export default function PermissionManagement() {
  const { permissions, updatePermission, resetToDefaults } = usePermissions();
  const [search, setSearch] = useState('');
  const [selectedGroup, setSelectedGroup] = useState<string>('All');

  const groups = useMemo(() => {
    const g = new Set(ALL_MODULES.map(m => m.group));
    return ['All', ...Array.from(g)];
  }, []);

  const filteredModules = useMemo(() => {
    return ALL_MODULES.filter(m => {
      const matchesSearch = m.label.toLowerCase().includes(search.toLowerCase()) || m.key.toLowerCase().includes(search.toLowerCase());
      const matchesGroup = selectedGroup === 'All' || m.group === selectedGroup;
      return matchesSearch && matchesGroup;
    });
  }, [search, selectedGroup]);

  const handleReset = () => {
    resetToDefaults();
    toast({ title: 'Permissions Reset', description: 'All permissions have been restored to defaults.' });
  };

  const handlePermissionChange = (module: string, role: UserRole, permission: Permission) => {
    updatePermission(module, role, permission);
  };

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Header */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-primary/10 flex items-center justify-center">
            <Shield className="w-5 h-5 text-primary" />
          </div>
          <div>
            <h2 className="text-lg font-display font-bold text-foreground">Permission Management</h2>
            <p className="text-sm text-muted-foreground">Configure module access for each role</p>
          </div>
        </div>
        <Button variant="outline" size="sm" onClick={handleReset} className="gap-2">
          <RotateCcw className="w-4 h-4" /> Reset to Defaults
        </Button>
      </div>

      {/* Legend */}
      <div className="flex flex-wrap gap-2">
        {PERMISSION_OPTIONS.map(opt => (
          <span key={opt.value} className={`px-2.5 py-1 rounded-full text-xs font-medium border ${opt.color}`}>
            {opt.label}
          </span>
        ))}
      </div>

      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="relative flex-1 max-w-sm">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
          <Input
            placeholder="Search modules..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="pl-9"
          />
        </div>
        <div className="flex flex-wrap gap-1.5">
          {groups.map(g => (
            <button
              key={g}
              onClick={() => setSelectedGroup(g)}
              className={`px-3 py-1.5 rounded-lg text-xs font-medium transition-all ${
                selectedGroup === g
                  ? 'bg-primary text-primary-foreground'
                  : 'bg-card border border-border text-muted-foreground hover:text-foreground hover:border-primary/30'
              }`}
            >
              {g}
            </button>
          ))}
        </div>
      </div>

      {/* Permissions Table */}
      <motion.div
        initial={{ opacity: 0, y: 10 }}
        animate={{ opacity: 1, y: 0 }}
        className="bg-card rounded-xl border border-border overflow-x-auto"
      >
        <table className="w-full min-w-[900px]">
          <thead>
            <tr className="border-b border-border">
              <th className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground sticky left-0 bg-card z-10 min-w-[180px]">Module</th>
              {EDITABLE_ROLES.map(role => (
                <th key={role} className="text-center px-2 py-3 text-xs font-semibold text-muted-foreground min-w-[100px]">
                  {getRoleName(role)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filteredModules.map((mod, idx) => {
              const showGroupHeader = idx === 0 || filteredModules[idx - 1].group !== mod.group;

              return (
                <React.Fragment key={mod.key}>
                  {showGroupHeader && (
                    <tr>
                      <td colSpan={EDITABLE_ROLES.length + 1} className="px-4 py-2 bg-muted/50">
                        <span className="text-xs font-bold text-muted-foreground uppercase tracking-wider">{mod.group}</span>
                      </td>
                    </tr>
                  )}
                  <tr className="border-b border-border last:border-0 hover:bg-muted/20 transition-colors">
                    <td className="px-4 py-2.5 text-sm font-medium text-foreground sticky left-0 bg-card">
                      {mod.label}
                    </td>
                    {EDITABLE_ROLES.map(role => {
                      const currentPerm = permissions[mod.key]?.[role] || 'none';
                      return (
                        <td key={role} className="px-2 py-2.5 text-center">
                          <select
                            value={currentPerm}
                            onChange={e => handlePermissionChange(mod.key, role, e.target.value as Permission)}
                            className={`text-xs font-medium rounded-lg px-2 py-1.5 border cursor-pointer transition-all focus:ring-2 focus:ring-primary/20 outline-none ${
                              PERMISSION_OPTIONS.find(o => o.value === currentPerm)?.color || ''
                            }`}
                          >
                            {PERMISSION_OPTIONS.map(opt => (
                              <option key={opt.value} value={opt.value}>{opt.label}</option>
                            ))}
                          </select>
                        </td>
                      );
                    })}
                  </tr>
                </React.Fragment>
              );
            })}
          </tbody>
        </table>
      </motion.div>

      <p className="text-xs text-muted-foreground text-center">
        <Shield className="w-3 h-3 inline mr-1" />
        Super Admin and Org Admin always have full access and cannot be restricted.
      </p>
    </div>
  );
}
