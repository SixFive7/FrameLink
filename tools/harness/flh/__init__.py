"""FrameLink autonomy harness - milestone M0 (version2.md section 5.1).

    "A code change reaches the mule and is verified with no human help: build path,
     deploy script, power-cycle control, screenshot + journal collection, resumable
     progress file, test runner."

One entry point drives all of it: ``python tools/harness/fl.py <subcommand>``.

Module map
----------
:mod:`flh.config`    hosts, paths, the FL_PW / FL_HA_TOKEN contract, relay safety limits
:mod:`flh.progress`  the resumable progress file (``tools/harness/progress.json``)
:mod:`flh.build`     drives the emulated linux/arm64 container that produces the AOT binary
:mod:`flh.deploy`    paramiko push + systemd unit install, idempotent, verified by the mule
:mod:`flh.power`     Home Assistant smart-plug control with wrong-entity and wear guards
:mod:`flh.collect`   the two allowlisted diagnostics: screenshot and journal tail
:mod:`flh.testrun`   the test suite, with telemetry off and the exit code propagated
:mod:`flh.png`       dependency-free PNG writer for the framebuffer screenshot path
:mod:`flh.ssh`       paramiko session handling and elevation; the only place a password is passed
:mod:`flh.ui`        console output

Everything that touches the mule is Python with paramiko, per CLAUDE.md section 1.3. The
non-mule parts are Python too, for one reason worth stating: the progress file is written
by every subcommand, so a second language would mean a second implementation of the one
artifact whose correctness the whole resume story depends on.
"""

__all__ = [
    "build",
    "collect",
    "config",
    "deploy",
    "png",
    "power",
    "progress",
    "ssh",
    "testrun",
    "ui",
]
