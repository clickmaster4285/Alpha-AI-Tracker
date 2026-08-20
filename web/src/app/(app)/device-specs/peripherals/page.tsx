'use client';

import { motion } from 'framer-motion';
import { Usb } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import DeviceClassIcon from '@/components/employees/DeviceClassIcon';
import { formatDateShort } from '@/lib/format';

export default function DeviceSpecsPeripherals() {
  return (
    <EmployeePage
      title="Peripherals"
      subtitle="USB and hot-plugged devices connected to the employee's machine."
      icon={Usb}
      fetchDetail
    >
      {({ detail }) => {
        const { hardwareDevices } = detail!;

        if (hardwareDevices.length === 0) {
          return <EmptyState icon={Usb} text="No peripheral devices recorded yet" />;
        }

        return (
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
        );
      }}
    </EmployeePage>
  );
}
