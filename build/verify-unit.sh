#!/usr/bin/env bash
#
# Run `systemd-analyze verify` over the committed fl-agent unit, inside a container whose
# systemd is the same one the frames run.
#
# WHY THIS FILE EXISTS
# --------------------
# On 2026-08-15 the mule logged, as systemd's very first line about the service:
#
#   systemd[1]: /etc/systemd/system/fl-agent.service:39: Unknown key 'StartLimitIntervalSec' in section [Service], ignoring.
#
# StartLimitIntervalSec is a [Unit] key. systemd parsed the file, dropped the key, and started
# the service anyway - so `systemctl status` was green, `systemctl start` exited 0, and the unit's
# restart rate limiting was silently absent. Nothing short of reading the journal at the moment of
# the first daemon-reload would have shown it.
#
# `systemd-analyze verify` reports it without a Pi, without a deploy, and without root. It is the
# only tool in this repository that reads a unit file with the real parser rather than by eye.
#
# THE EXIT CODE IS NOT THE ANSWER
# -------------------------------
# `systemd-analyze verify` exits 0 on an unknown key. Measured, on the unit as it was:
#
#   /run/systemd/verify/fl-agent.service:39: Unknown key 'StartLimitIntervalSec' in section [Service], ignoring.
#   rc=0
#
# So a gate written as `systemd-analyze verify unit && echo ok` passes on exactly the defect it was
# added to catch. This script therefore treats ANY output as failure and ignores the exit code.
#
# WHY THIS IS NOT IN THE TEST SUITE
# ---------------------------------
# `dotnet run --project tests/FrameLink.Tests` is ~290 tests in ~11 seconds, hermetic, and needs
# nothing but the checkout. Pulling a Debian image and apt-installing systemd costs minutes on a
# cold cache and needs both Docker and the network, which is a poor trade for every run of every
# test. The always-on half of the guard is in AgentSystemdUnitTests instead: it asserts that every
# directive in the unit sits in a section systemd accepts it in, using a table transcribed from
# systemd's own parser. That catches the next misplaced key on a laptop with no Docker at all.
# This script is the ground truth the table was checked against, kept runnable so the next person
# can re-derive it rather than trust a transcription.
#
# ARCHITECTURE
# ------------
# amd64 deliberately, unlike build/build.sh. The unit parser is architecture-independent - it reads
# text - so there is nothing for an emulated linux/arm64 container to tell us that a native one
# does not, and the native one costs seconds instead of minutes. Override with FL_PLATFORM if you
# want to prove that for yourself.
#
# USAGE
#   bash build/verify-unit.sh
#
# Requires Docker. Idempotent, read-only with respect to the repository, and leaves no container
# behind (--rm).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Debian 13 is Raspberry Pi OS Trixie's base, and carries the systemd the frames run
# (version2.md 7.2 records systemd 257).
FL_IMAGE="${FL_IMAGE:-debian:trixie}"
FL_PLATFORM="${FL_PLATFORM:-linux/amd64}"

UNITS=(
    "tools/harness/assets/fl-agent.service"
    "src/FrameLink.Agent/Systemd/fl-agent.service"
)

# Both copies are meant to be byte-identical (the test suite enforces it). Checking here too means
# this script gives a straight answer when run on a checkout that has not been built.
if ! cmp -s "${REPO_ROOT}/${UNITS[0]}" "${REPO_ROOT}/${UNITS[1]}"; then
    printf 'verify-unit: FAIL - the two committed copies of the unit differ:\n' >&2
    printf 'verify-unit:   %s\n' "${UNITS[@]}" >&2
    printf 'verify-unit: They are one text with two homes. Edit one, edit both.\n' >&2
    exit 2
fi

printf 'verify-unit: %s on %s\n' "${FL_IMAGE}" "${FL_PLATFORM}"

# The heredoc runs inside the container. /repo is mounted read-only, so the units are copied out
# before verification; the copy also lets us drop the 0755 the Windows bind mount reports, which
# systemd-analyze would otherwise complain about ("marked executable") on every run.
#
# `-i` is load-bearing and was missing on the first draft of this script. Without it Docker does
# not attach stdin to the container, so `bash -s` reads an immediately-closed stream, runs nothing,
# and exits 0 - a green gate that verified precisely nothing. It was caught by reintroducing the
# StartLimitIntervalSec defect on purpose and noticing the script still said OK, which is the only
# reason to ever test that a guard fails.
OUTPUT="$(docker run --rm -i --platform "${FL_PLATFORM}" -v "${REPO_ROOT}:/repo:ro" "${FL_IMAGE}" bash -s -- "${UNITS[@]}" <<'CONTAINER'
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null 2>&1
apt-get install -y -qq --no-install-recommends systemd >/dev/null 2>&1

printf 'verify-unit: %s\n' "$(systemd-analyze --version | head -1)" >&2

# ExecStart= must resolve to something executable or verify reports the absence as an error of its
# own, which would drown the parse findings this script exists to surface.
install -d /usr/local/bin
printf '#!/bin/sh\nexit 0\n' > /usr/local/bin/fl-agent
chmod 0755 /usr/local/bin/fl-agent

install -d /run/verify
status=0
for unit in "$@"; do
    install -m 0644 "/repo/${unit}" "/run/verify/$(basename "${unit}")"
    # Output, not exit code: an unknown key is reported on stderr and still exits 0.
    if ! findings="$(systemd-analyze verify "/run/verify/$(basename "${unit}")" 2>&1)"; then
        status=1
    fi
    if [ -n "${findings}" ]; then
        printf '%s: %s\n' "${unit}" "${findings}"
        status=1
    fi
done
exit "${status}"
CONTAINER
)" && RC=0 || RC=$?

if [ -n "${OUTPUT}" ]; then
    printf 'verify-unit: FAIL - systemd-analyze verify has findings:\n' >&2
    printf '%s\n' "${OUTPUT}" >&2
    exit 1
fi

if [ "${RC}" -ne 0 ]; then
    printf 'verify-unit: FAIL - the container exited %s with no findings; the run itself broke.\n' "${RC}" >&2
    exit "${RC}"
fi

printf 'verify-unit: OK - systemd-analyze verify reports nothing on either copy.\n'
