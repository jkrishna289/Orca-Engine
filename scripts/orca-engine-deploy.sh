#!/usr/bin/env bash
# In-place Orca Engine DLL swap. Run as root: sudo bash /tmp/orca-engine-deploy.sh
#
# Lives in the repo because /tmp is reaped: the copy that had been used for every deploy vanished
# mid-session and had to be reconstructed from a log of its own output. Ship it with:
#   scp scripts/orca-engine-deploy.sh Wholphin.Engine/bin/Release/net9.0/Wholphin.Engine.dll host:/tmp/
set -euo pipefail

PLUGIN_ROOT=/var/lib/jellyfin/plugins
BACKUP_ROOT=/root/orca-plugin-backups
NEW_DLL=/tmp/Wholphin.Engine.dll
PORT=65321

[[ -f "$NEW_DLL" ]] || { echo "FATAL: $NEW_DLL missing — upload it first"; exit 1; }

# Match "Orca Engine_*" explicitly. A "wholphin" substring match would also hit the unrelated
# legacy Wholphin_0.4.0 folder and swap the DLL into the wrong plugin.
mapfile -t DIRS < <(find "$PLUGIN_ROOT" -maxdepth 1 -type d -name 'Orca Engine_*' | sort)
if [[ ${#DIRS[@]} -ne 1 ]]; then
    echo "FATAL: expected exactly one 'Orca Engine_*' folder, found ${#DIRS[@]}:"
    printf '  %s\n' "${DIRS[@]}"
    exit 1
fi
TARGET="${DIRS[0]}"
echo "target:  $TARGET"

# Backups live OUTSIDE the plugins dir on purpose: Jellyfin loads every subdirectory under
# plugins/, so a .bak copy in there becomes a second plugin advertising the same GUID.
mkdir -p "$BACKUP_ROOT"
BACKUP="$BACKUP_ROOT/$(basename "$TARGET")-$(date +%Y%m%d-%H%M%S)"
cp -a "$TARGET" "$BACKUP"
echo "backup:  $BACKUP"

echo "old dll: $(sha256sum "$TARGET/Wholphin.Engine.dll" 2>/dev/null | cut -c1-12 || echo none)"
install -m 0644 -o jellyfin -g jellyfin "$NEW_DLL" "$TARGET/Wholphin.Engine.dll"
echo "new dll: $(sha256sum "$TARGET/Wholphin.Engine.dll" | cut -c1-12)"

# Jellyfin loads ONLY the assemblies named in meta.json's "assemblies" array. A dependency sitting
# in the folder but missing from that list throws FileNotFoundException for a file that is right
# there — and Jellyfin then persists "status": "NotSupported" and never retries, so repairing
# "assemblies" alone still won't load. Both fields get rewritten from what is actually on disk.
python3 - "$TARGET" <<'PY'
import json, os, sys

target = sys.argv[1]
meta_path = os.path.join(target, "meta.json")
with open(meta_path) as fh:
    meta = json.load(fh)

on_disk = sorted(f for f in os.listdir(target) if f.endswith(".dll"))
before_assemblies = meta.get("assemblies", [])
before_status = meta.get("status")

meta["assemblies"] = on_disk
meta["status"] = "Active"

with open(meta_path, "w") as fh:
    json.dump(meta, fh, indent=4)

added = [a for a in on_disk if a not in before_assemblies]
print(f"meta:    status {before_status!r} -> 'Active', {len(on_disk)} assemblies"
      + (f", added {added}" if added else ", unchanged"))
PY
chown jellyfin:jellyfin "$TARGET/meta.json"

echo "restarting jellyfin..."
systemctl restart jellyfin

# Poll rather than sleep-and-hope: startup time varies with library size.
for i in $(seq 1 40); do
    if curl -s -m 3 "http://127.0.0.1:$PORT/OrcaEngine/Health" 2>/dev/null | grep -q '"Status":"ok"'; then
        echo
        echo "HEALTH:   $(curl -s -m 5 http://127.0.0.1:$PORT/OrcaEngine/Health)"
        echo "FEATURES: $(curl -s -m 5 http://127.0.0.1:$PORT/OrcaEngine/Settings/Features)"
        echo
        echo "DEPLOY OK"
        exit 0
    fi
    sleep 3
done

echo "FATAL: engine did not answer /Health within 120s. Recent plugin log lines:"
journalctl -u jellyfin --since '3 minutes ago' --no-pager | grep -iE 'orca|wholphin|plugin' | tail -30
echo
echo "Roll back with:  rm -rf '$TARGET' && cp -a '$BACKUP' '$TARGET' && systemctl restart jellyfin"
exit 1
