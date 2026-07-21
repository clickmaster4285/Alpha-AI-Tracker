#!/usr/bin/env python3
"""AT-SPI helper: get active window PID|TITLE on Wayland/GNOME.
Output: PID|TITLE  or nothing. Must exit within 2 seconds."""

import os
import signal
import sys

signal.alarm(2)

try:
    import dbus
except ImportError:
    sys.exit(1)


def main():
    try:
        out = os.popen(
            'gdbus call --session --dest org.a11y.Bus '
            '--object-path /org/a11y/bus --method org.a11y.Bus.GetAddress 2>/dev/null'
        ).read().strip().strip("('").strip("',)")
        if not out:
            return
    except Exception:
        return

    try:
        bus = dbus.bus.BusConnection(out)
        root = bus.get_object(
            'org.a11y.atspi.Registry', '/org/a11y/atspi/accessible/root'
        )
        children = root.GetChildren(dbus_interface='org.a11y.atspi.Accessible')
    except Exception:
        return

    for app_bus_name, _ in children:
        name = str(app_bus_name)
        if name == ':1.0':  # skip registry/desktop
            continue
        try:
            app_obj = bus.get_object(name, '/org/a11y/atspi/accessible/root')
            win_children = app_obj.GetChildren(
                dbus_interface='org.a11y.atspi.Accessible'
            )
        except Exception:
            continue

        for win_bus, win_path in win_children:
            try:
                win_obj = bus.get_object(str(win_bus), str(win_path))
                state = win_obj.GetState(
                    dbus_interface='org.a11y.atspi.Accessible'
                )
                if 8 not in state:
                    continue

                title = None
                try:
                    title = str(win_obj.Get(
                        'org.a11y.atspi.Accessible', 'Name',
                        dbus_interface='org.freedesktop.DBus.Properties'
                    ))
                except Exception:
                    pass

                pid = None
                try:
                    app_ref = win_obj.GetApplication(
                        dbus_interface='org.a11y.atspi.Accessible'
                    )
                    if app_ref:
                        parent_bus = str(app_ref[0])
                        dbus_obj = bus.get_object(
                            'org.freedesktop.DBus', '/org/freedesktop/DBus'
                        )
                        pid = dbus_obj.GetConnectionUnixProcessID(
                            parent_bus,
                            dbus_interface='org.freedesktop.DBus'
                        )
                except Exception:
                    pass

                print(f"{pid or ''}|{title or ''}")
                return
            except Exception:
                continue


if __name__ == '__main__':
    main()
