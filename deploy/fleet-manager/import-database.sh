#!/usr/bin/env bash
#
# Moves an existing FrameLink fleet database into the named volume the development stack uses
# (version2.md §3.1, §3.3; docs/15-local-fleet-manager.md step 6).
#
# WHY THIS EXISTS AT ALL, RATHER THAN A BIND MOUNT
# ------------------------------------------------
# On Windows, Docker Desktop presents a host directory to the container over 9p (drvfs), and
# SQLite's file locking does not cross that boundary. Measured on this workstation: with a
# process on the Windows side holding BEGIN EXCLUSIVE on the database file, a write issued inside
# the container returned 204 in 50 ms, and the two sides then disagreed about the contents — the
# container could read a fleet setting the host could not. That is two writers with no mutual
# exclusion and no error anywhere, against the one file that holds every adopted frame's identity
# binding (§3.3). So the database lives on ext4 inside the Docker VM, and getting an existing one
# in there is this script.
#
# WHAT IT DOES NOT DO
# -------------------
# It never touches the source directory. Not a move, not a rename, not a lock — it opens the
# files for reading and nothing else, so the copy the operator started from is still there
# afterwards and rolling back to `dotnet run` is always possible.
#
# IDEMPOTENCY (§0 principle 1)
# ----------------------------
# Running it a second time is a no-op that says so. A volume that already holds framelink.db is
# left alone and the script exits 0 — because the second run is far more likely to be a repeat of
# a procedure than a request to overwrite a live fleet database with an older copy. FL_FORCE=1
# overwrites deliberately, and even then it takes a timestamped copy inside the volume first.
#
#   bash deploy/fleet-manager/import-database.sh /c/Users/jori/framelink-control-data
#   FL_VOLUME=framelink-data bash deploy/fleet-manager/import-database.sh <dir>
#   FL_FORCE=1 bash deploy/fleet-manager/import-database.sh <dir>
#
# Inputs:
#   $1 / FL_SOURCE   directory holding framelink.db          (required)
#   FL_VOLUME        the named volume                        (default framelink-data)
#   FL_IMAGE         throwaway image used for the copy       (default debian:trixie-slim)
#   FL_FORCE         overwrite an already-populated volume   (default 0)
#
# Exit codes:
#   0  imported, or already imported and left alone
#   4  docker is not available
#   5  the source directory has no framelink.db
#   6  the source database is still open by a running Fleet Manager
#   7  the copy does not match the source byte for byte

set -euo pipefail

# Git Bash rewrites any argument that looks like a POSIX absolute path into a Windows one before
# the process ever sees it, so `docker ... ls /data` runs `ls 'C:/Program Files/Git/data'` and
# fails on a path nobody typed. Both variables are read only by the MSYS runtime and are ignored
# everywhere else, which is why they are set unconditionally rather than behind an OS test.
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

FL_VOLUME="${FL_VOLUME:-framelink-data}"
FL_IMAGE="${FL_IMAGE:-debian:trixie-slim}"
FL_FORCE="${FL_FORCE:-0}"
SOURCE="${1:-${FL_SOURCE:-}}"

say() { printf '[import] %s\n' "$*"; }
die() { printf '[import] ERROR: %s\n' "$1" >&2; exit "${2:-1}"; }

command -v docker >/dev/null 2>&1 || die "docker is not on PATH. Docker Desktop must be running." 4
docker version --format '{{.Server.Version}}' >/dev/null 2>&1 \
    || die "the Docker daemon is not responding. Start Docker Desktop." 4

[ -n "${SOURCE}" ] || die "pass the directory holding framelink.db as the first argument" 5
[ -f "${SOURCE}/framelink.db" ] || die "${SOURCE}/framelink.db does not exist" 5

# A -wal beside the database means SQLite did not close cleanly, which on a live directory means
# the Fleet Manager still has it open. Copying a database and its write-ahead log while a writer
# is mid-transaction produces a set of files that do not agree with each other, and the symptom
# arrives days later as a fleet that has forgotten a frame. Stop the writer first.
if [ -f "${SOURCE}/framelink.db-wal" ] && [ "${FL_FORCE}" != "1" ]; then
    die "${SOURCE}/framelink.db-wal exists, so a Fleet Manager still has this database open. Stop it, wait for the -wal and -shm files to disappear, then run this again. FL_FORCE=1 copies anyway." 6
fi

say "source ${SOURCE}"
say "volume ${FL_VOLUME}"

# Idempotent by construction: the second run prints the name and changes nothing.
docker volume create "${FL_VOLUME}" >/dev/null
say "volume ${FL_VOLUME} exists"

EXISTING="$(docker run --rm --volume "${FL_VOLUME}:/data" "${FL_IMAGE}" \
    sh -c 'if [ -f /data/framelink.db ]; then sha256sum /data/framelink.db | cut -d" " -f1; fi')"

if [ -n "${EXISTING}" ] && [ "${FL_FORCE}" != "1" ]; then
    say "the volume already holds framelink.db (sha256 ${EXISTING})"
    say "nothing to do. FL_FORCE=1 would replace it, keeping a timestamped copy first."
    exit 0
fi

SOURCE_SHA="$(sha256sum "${SOURCE}/framelink.db" | cut -d' ' -f1)"
say "source sha256 ${SOURCE_SHA}"

# The copy travels as a tar stream on stdin rather than through a bind mount. Two reasons, and
# the first is the whole point of this script: a bind mount is the mechanism whose locking does
# not work here, so not creating one at all is better than creating one carefully. The second is
# ordinary Windows friction — Git Bash rewrites a `-v C:\...:/data` argument into a path that does
# not exist, silently, and the container then writes into a directory nobody meant.
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
tar -C "${SOURCE}" -cf - framelink.db \
    | docker run --rm --interactive --volume "${FL_VOLUME}:/data" "${FL_IMAGE}" sh -c '
        set -eu
        if [ -f /data/framelink.db ]; then
            cp -p /data/framelink.db "/data/framelink.db.replaced-'"${STAMP}"'"
            echo "[import] kept the previous database as framelink.db.replaced-'"${STAMP}"'"
        fi
        tar -C /data -xf -
        chown -R 10001:10001 /data
        chmod 0640 /data/framelink.db
    '

# The image runs as uid 10001 and the volume was just written as root, so ownership is asserted
# above rather than hoped for. Verifying the bytes afterwards is what turns "the command did not
# fail" into "the database on the other side is the database that went in".
COPIED="$(docker run --rm --volume "${FL_VOLUME}:/data" "${FL_IMAGE}" \
    sh -c 'sha256sum /data/framelink.db | cut -d" " -f1')"

[ "${COPIED}" = "${SOURCE_SHA}" ] || die "the copy in the volume is sha256 ${COPIED}, the source is ${SOURCE_SHA}" 7

say "imported sha256 ${COPIED} — byte for byte identical"
docker run --rm --volume "${FL_VOLUME}:/data" "${FL_IMAGE}" ls -la /data
say "done. The source directory was not modified."
