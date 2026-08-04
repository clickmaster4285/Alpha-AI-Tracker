"""Propagate a browser-authored Chromium extension entry from a seed profile
to all other profiles under the same User Data dir (valid MAC included).

Usage:
  python propagate_chromium_extension.py <user_data_dir> <seed_profile> <ext_id>
"""
from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path


SKIP = {
    "System Profile",
    "Guest Profile",
    "Crashpad",
    "Snapshots",
    "Safe Browsing",
    "External Extensions",
    "component_crx_cache",
    "extensions_crx_cache",
}


def main() -> int:
    if len(sys.argv) < 4:
        print("usage: propagate_chromium_extension.py <user_data> <seed_profile> <ext_id>")
        return 2

    ud = Path(sys.argv[1])
    seed_name = sys.argv[2]
    ext_id = sys.argv[3]

    seed_sp = ud / seed_name / "Secure Preferences"
    if not seed_sp.exists():
        print(f"missing seed Secure Preferences: {seed_sp}")
        return 1

    src = json.loads(seed_sp.read_text(encoding="utf-8"))
    try:
        entry = src["extensions"]["settings"][ext_id]
        mac = src["protection"]["macs"]["extensions"]["settings"][ext_id]
        dev_mac = src["protection"]["macs"]["extensions"]["ui"].get("developer_mode")
    except KeyError as e:
        print(f"seed missing node: {e}")
        return 1

    copied = 0
    for p in sorted(ud.iterdir()):
        if not p.is_dir() or p.name in SKIP or p.name == seed_name:
            continue
        if p.name.startswith("."):
            continue
        sp = p / "Secure Preferences"
        if not sp.exists():
            print(f"skip {p.name}: no Secure Preferences")
            continue

        bak = p / "Secure Preferences.aat.bak"
        if not bak.exists():
            shutil.copy2(sp, bak)

        data = json.loads(sp.read_text(encoding="utf-8"))
        data.setdefault("extensions", {}).setdefault("settings", {})[ext_id] = entry
        data.setdefault("extensions", {}).setdefault("ui", {})["developer_mode"] = True
        prot = data.setdefault("protection", {}).setdefault("macs", {}).setdefault("extensions", {})
        prot.setdefault("settings", {})[ext_id] = mac
        if dev_mac is not None:
            prot.setdefault("ui", {})["developer_mode"] = dev_mac

        eh = prot.get("settings_encrypted_hash")
        if isinstance(eh, dict):
            eh.pop(ext_id, None)
        ui = prot.get("ui")
        if isinstance(ui, dict):
            ui.pop("developer_mode_encrypted_hash", None)

        tmp = sp.with_suffix(".aat.tmp")
        tmp.write_text(json.dumps(data, separators=(",", ":"), ensure_ascii=False), encoding="utf-8")
        tmp.replace(sp)
        copied += 1
        print(f"copied -> {p.name}")

    print(f"done copied={copied}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
