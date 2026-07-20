'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { MapPin, Plus, Navigation } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Switch } from '@/components/ui/switch';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const locationData = [
  { employee: 'Yashodhan Kalia', timestamp: '2026-03-10 10:15 AM', address: '123 Tech Park, Bangalore', coords: '12.9716, 77.5946', geofence: 'Inside' },
  { employee: 'Rakesh Pathania', timestamp: '2026-03-10 10:20 AM', address: '456 Office Tower, Mumbai', coords: '19.0760, 72.8777', geofence: 'Inside' },
  { employee: 'Arush Sharma', timestamp: '2026-03-10 10:25 AM', address: '789 Coffee Shop, Delhi', coords: '28.6139, 77.2090', geofence: 'Outside' },
];

const geofences = [
  { id: '1', name: 'Office HQ', center: '12.9716, 77.5946', radius: 200, alertOnExit: true },
  { id: '2', name: 'Warehouse', center: '12.9580, 77.6420', radius: 500, alertOnExit: false },
];

export default function GPSLocationPage() {
  const [showGeoDialog, setShowGeoDialog] = useState(false);
  const [geoForm, setGeoForm] = useState({ name: '', center: '', radius: 200, alertOnExit: true });

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Location Log */}
      <div>
        <h3 className="font-display font-bold text-lg text-foreground mb-4">Location Log</h3>
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[700px]">
            <thead>
              <tr className="border-b border-border">
                {['Employee', 'Timestamp', 'Location', 'GPS Coordinates', 'Geofence'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {locationData.map((l, i) => (
                <motion.tr key={i} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                  <td className="px-4 py-3 text-sm font-medium text-foreground">{l.employee}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{l.timestamp}</td>
                  <td className="px-4 py-3 text-sm text-foreground flex items-center gap-1"><MapPin className="w-3.5 h-3.5 text-primary" />{l.address}</td>
                  <td className="px-4 py-3 text-xs font-mono text-muted-foreground">{l.coords}</td>
                  <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${l.geofence === 'Inside' ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>{l.geofence}</span></td>
                </motion.tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {/* Geofence Config */}
      <div>
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-display font-bold text-lg text-foreground">Geofence Zones</h3>
          <Button onClick={() => setShowGeoDialog(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> Add Zone</Button>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {geofences.map(g => (
            <div key={g.id} className="bg-card rounded-xl border border-border p-5">
              <div className="flex items-center gap-3 mb-3">
                <div className="w-10 h-10 rounded-lg bg-primary/10 flex items-center justify-center"><Navigation className="w-5 h-5 text-primary" /></div>
                <div><h4 className="font-display font-bold text-foreground">{g.name}</h4><p className="text-xs text-muted-foreground font-mono">{g.center}</p></div>
              </div>
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Radius: {g.radius}m</span>
                <span className={g.alertOnExit ? 'text-warning' : 'text-muted-foreground'}>{g.alertOnExit ? 'Exit alerts ON' : 'No alerts'}</span>
              </div>
            </div>
          ))}
        </div>
      </div>

      <Dialog open={showGeoDialog} onOpenChange={setShowGeoDialog}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Add Geofence Zone</DialogTitle></DialogHeader>
          <div className="space-y-4 mt-2">
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Zone Name *</label><Input value={geoForm.name} onChange={e => setGeoForm({...geoForm, name: e.target.value})} placeholder="e.g. Office HQ" /></div>
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Center Point (Lat, Long) *</label><Input value={geoForm.center} onChange={e => setGeoForm({...geoForm, center: e.target.value})} placeholder="12.9716, 77.5946" /></div>
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Radius (meters) *</label><Input type="number" value={geoForm.radius} onChange={e => setGeoForm({...geoForm, radius: Number(e.target.value)})} min={50} /></div>
            <div className="flex items-center justify-between">
              <span className="text-sm font-medium text-foreground">Alert on Exit</span>
              <Switch checked={geoForm.alertOnExit} onCheckedChange={v => setGeoForm({...geoForm, alertOnExit: v})} />
            </div>
            <Button className="w-full gradient-primary text-primary-foreground">Add Zone</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
