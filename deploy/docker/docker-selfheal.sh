#!/bin/sh
# FrameLink: repair Docker after power-loss corruption, then make sure the
# slideshow container is running. Runs once per boot, after docker.service has
# had its chance to start on its own.
#
# The failure this heals was observed on hardware: an abrupt power cut can leave
# Docker's network store (/var/lib/docker/network/files/local-kv.db) holding a
# duplicate default-bridge entry. dockerd then refuses to start at every boot
# ("networks have same bridge name"), the immich-kiosk container never runs, and
# the kiosk's slideshow iframe points at a dead port — which drives a Chromium
# renderer memory leak (~50 MB/min measured) that ends in an OOM kill or a
# hardware-watchdog reset. Retiring the corrupt store is safe: Docker recreates
# the default networks, and compose-defined networks are recreated on container
# start. The old file is kept beside it with a .corrupt timestamp for forensics.

sleep 20   # let docker.service finish (or exhaust) its own start attempts first

if systemctl is-failed --quiet docker.service; then
    if journalctl -b -u docker --no-pager 2>/dev/null | grep -q "networks have same bridge name"; then
        echo "docker-selfheal: corrupt network store detected, repairing"
        systemctl stop docker docker.socket
        mv /var/lib/docker/network/files/local-kv.db \
           "/var/lib/docker/network/files/local-kv.db.corrupt.$(date +%s)" 2>/dev/null
        systemctl reset-failed docker
        systemctl start docker
    else
        echo "docker-selfheal: docker failed for an unrecognised reason; not touching it"
    fi
fi

if systemctl is-active --quiet docker.service; then
    # restart:always normally covers this; docker start is a no-op when it did.
    docker start immich-kiosk >/dev/null 2>&1 || true
fi
echo "docker-selfheal: done"
