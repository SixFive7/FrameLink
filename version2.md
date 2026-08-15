# FrameLink v2 — Build Specification

**Status: design complete, not yet started.** No v2 code exists. This document is the
specification the build follows; §5 is the execution plan, and Appendix A preserves every
decision with its reasoning.

v2 replaces the hand-followed build guides with two programs: an agent that provisions and
maintains each frame, and a fleet manager that owns them centrally.

---

## 0. Prime directive and preconditions

**Prime directive.** The existing build guides (`docs/1`–`docs/13`) remain the canonical
installation path until every piece of functionality they encode is **verified** as moved into
the agent. The guides are the specification the agent implements; nothing is deleted before
parity is proven (§5.1, Mn+3).

**Precondition zero — freeze v1 as data before repurposing the frame.** Parity is measured
against v1, so it must exist in machine-readable form first:

1. **A full state inventory** exported from the working frame: package set, unit files and
   enablement, config contents and hashes, mixer values, firmware versions, kernel parameters,
   container state. This is the definition of "at parity" that the state-diff harness consumes.
2. **A full SD card image.** Archival reference *and* the recovery card during development, so
   a bricked provisioning attempt costs a card swap rather than a rebuild.

There is no production fleet and no frame to migrate — the single existing unit is a desk
prototype, so it becomes the development mule once those two artifacts exist.

**Deliberately skipped:** filling the remaining `[Pending capture]` EXPECTED OUTPUT blocks in
guides 3–13. Those sections are slated for deletion at parity (§8, decision 24), so capturing
them is work with a short half-life. Revisit only if v2 stalls and the guides must remain the
product.

---

## 1. Architecture

### 1.1 The two programs

| | **FrameLink Agent** | **FrameLink Fleet Manager** |
|---|---|---|
| Runs on | Every frame — Pi 5, linux-arm64 | The operator's server — linux-amd64 container |
| Form | One Native AOT binary, systemd service | ASP.NET Core (AOT) + SQLite + embedded Svelte GUI + bundled LiveKit |
| Owns | The device's entire system state, the product app, the screen | Devices, settings, telemetry, the update feed, calls |
| Code | `FrameLink.Agent` → `fl-agent`, `fl-agent.service` | `FrameLink.Control` → `fl-control` |

Naming has three registers: **product** ("FrameLink Agent", "FrameLink Fleet Manager") for
anything a human reads; **code** (`FrameLink.Agent` / `FrameLink.Control`); **system**
(`fl-agent` / `fl-control`) for binaries, units and routes.

### 1.2 Governing principles

1. **Self-hosting is a first-class requirement.** Anyone must run their own Fleet Manager and
   frames with no dependency on infrastructure this project operates — no rendezvous server, no
   account, no phone-home. An operator needs exactly two things: the container and network
   reach to it.
2. **Local authority, central appearance.** The agent decides and acts; the Fleet Manager
   observes, configures and requests. A frame must provision and self-heal with the server
   unreachable.
3. **Total transparency.** Nothing is repaired invisibly. Every abnormal state is named on the
   frame's own screen and in the Fleet Manager (§2.6).
4. **Version-lock by construction.** The agent binary is served by the Fleet Manager, so agent
   version is a function of server version and the wire protocol always matches.
5. **One diagnosis per change.** Resources are atomic and applied one at a time, so "which
   change broke it" is always answerable (§2.2).

---

## 2. FrameLink Agent

### 2.1 Packaging and delivery

- **One single Native AOT binary. No supplemental program files, ever.** No helper
  executables, no shared libraries, no loose web assets. The terminal interface, the on-device
  web interface, fonts and every asset ship *inside* the binary as embedded resources.
- **The product app is embedded too.** The v1 SPA service and its on-disk git checkout are
  gone; the agent serves the app from its own binary, so the app can never drift from the agent
  managing it, and the repair screen and product share one local origin.
- **Persisted state is data, not program files**, under `/var/lib/fl-agent`: device keypair,
  last-known desired values, progress journal, telemetry buffer. Never touched by an update.
- **Immich Kiosk stays upstream.** It is a mature product with a team behind it; v2 does not
  reimplement it. The agent fetches the pinned release (`immich-kiosk_Linux_arm64.tar.gz`,
  ~7.4 MB, static Go binary, AGPL-3.0), verifies its checksum, and **supervises it as a child
  process**. This gives real lifetime control and removes Docker from the frame entirely —
  along with the corrupt-network-store failure class that began the August 2026 incident chain.
  Fetching from upstream rather than redistributing keeps AGPL source-offer obligations off
  this project and off every self-hoster; a Fleet-Manager mirror stays available as a later
  operator setting.

### 2.2 The reconciliation loop

Adapted from the Kubernetes controller model, minus the cluster machinery.

- **Level-triggered, not edge-triggered.** The agent never "runs an installer"; it continuously
  converges observed state toward desired state. Provisioning a bare frame and repairing a
  drifted one are the same code path.
- **The resource is the unit** — the smallest independently verifiable setting: one
  `config.txt` line, one mixer control, one systemd unit (content *and* enablement), one apt
  package, one firmware version. Granularity rule: **one differential diagnosis = one
  resource.**
- **Static logic, dynamic values.** The catalog is compiled into the agent; the Fleet Manager
  supplies values, sequencing requests and allowlisted diagnostics — never logic. A
  server-driven executor would need a DSL or shell strings, which would dissolve the
  AOT/analyzer/test regime and turn the agent into a root remote-execution proxy.
- **Explicit lightweight DAG.** Each resource declares `dependsOn`; the loop orders
  topologically and marks dependents `Blocked(dependency)` rather than letting them fail
  confusingly on their own.
- **Sequential and single-threaded.** Determinism beats throughput on a 2 GB appliance.

### 2.3 Resource contract

Every resource implements **Observe → Compare → Act (only on drift) → Verify → Status**.
Observe and Verify share one implementation, and every guide CHECKPOINT becomes such a check —
which is exactly what the parity harness needs, so it falls out of the same functions.

Status vocabulary: `InSync` · `Progressing` · `AwaitingReboot` · `Degraded(reason, delta,
attempts)` · `Blocked(dependency)` · `Escalated(admin-notified)` · `Halted(persistent-failure)`.

### 2.4 Reboot and retry discipline

- **Every resource reboots. No exceptions, no per-resource cleverness.** Change one thing,
  reboot, verify it stuck. Some reboots are unnecessary; that is accepted deliberately, because
  deciding *which* settings need one is precisely the reasoning that produced v1's governor bug
  (the kernel parameter landed in `/proc/cmdline` and the governor still came up wrong).
  "Applied" is never claimed from a successful write — only from an observation after the
  setting had to survive a boot.
- **Exponential backoff per resource.** Backoff exists to stop a reboot loop from wearing the
  hardware; an unbounded retry cycle is more damaging than a stalled provision.

### 2.5 Escalation ladder

The loop must be willing to give up.

1. Resource fails post-boot verification → retry with growing delay.
2. Attempt budget exhausted → stop touching it, mark `Degraded` with the exact
   expected-vs-observed delta and attempt count, notify the Fleet Manager.
3. Fleet Manager notifies the operator (Home Assistant; SMTP for self-hosters without it) and
   offers two explicit actions: **retry** (reset the budget) or **open a remote shell**.
4. Repeated escalation on the same resource → `Halted` for that device. An administrator has
   been told more than once; continuing to reboot a persistently broken frame is damage, not
   diligence.

### 2.6 Device state ladder

Every "not green" condition is a distinct, explicitly rendered state. Outermost to innermost:

| State | Cause | On the frame | Product runs? |
|---|---|---|---|
| `NoContact` | Fleet Manager unreachable (silence) | Small persistent overlay | **Yes** — but only if the frame was fully green when contact was lost |
| `ControlNotConfigured` | Server reachable, no admin credential set | "Connected to a Fleet Manager, but it is not set up yet" | No |
| `NotAdopted` | New, blocked, or orphaned by a rebuilt server | "This device is healthy — adopt it in your Fleet Manager" | No |
| `VersionMismatch` | Agent version ≠ served version | Update progress | No |
| `Reconciling` | A resource drifted or was never applied | Narrated repair screen | No |
| `InSync` | Everything verified | — | **Yes** |

**Rejection is an answer; silence is not.** An authoritative "you are not adopted" stops the
product. An unreachable server does not — provided the frame was fully green when contact
dropped. The photo library is deliberately offline-capable, and an outage in the *operator's*
house must never blank a frame in someone else's.

**Any drift stops the product**, including an active call. Correctness and transparency outrank
call continuity; in normal operation nothing drifts, and when it does, everyone can see why.

**Conflict drift** — a change that keeps returning after correction (something is actively
fighting the desired state), or a desired-value change pushed from the Fleet Manager — is
treated as maximally serious and always interrupts the product.

### 2.7 The screen

The screen belongs to the agent whenever anything is not green. It is the product's primary
honesty mechanism.

**What the repair screen renders:**

1. **What was detected** — in plain language ("The speaker volume setting is not what it
   should be").
2. **Why it matters** — one short sentence.
3. **What is being done** — the exact command or change, plus a plain-language gloss.
4. **A countdown bar** before the verifying reboot (default 25 s), with a **"Reboot now"**
   button to skip once read — a tap on the touchscreen, and the same skip is available
   remotely. This is the one screen where v1's touch shield must *not* block input.
5. **Attempt number** when retrying ("Attempt 2 of 5").
6. **Backoff state** including remaining wait, so a pause never looks like a hang.
7. **Escalation state** when the budget is exhausted and the operator has been notified.

Countdown duration is configurable at three levels, most specific winning: install-flag/boot
file → fleet default → per-device override. Development runs use 0.

**Two-stage rendering, with a hard rule against blank screens:**

1. **Console stage.** Before any graphical stack exists, the agent writes directly to
   `/dev/tty1` (the DSI panel's default) — a designed terminal interface with colour, box
   drawing and animated progress, not log spew. No login session, no dependencies, works from
   the first second of the first boot. **The panel has to exist first.** On a stock image there
   is no DSI connector, no `/dev/fb0` and no backlight, and a write to `/dev/tty1` succeeds
   while producing no pixels — a stage that trusted its own write would report success and show
   nothing. The display overlay and the console rotation are therefore reconciled **first**,
   right after the agent-version root and ahead of adoption: a deliberate carve-out from §5.5's
   brick-capable-last ordering, decided in favour of this section (decision 46).
2. **Browser stage.** As soon as the reconciler has brought up the kiosk stack, that same
   browser renders the agent's page. Bringing the stack up is therefore front-loaded in the DAG.
3. **Fallback rule.** After starting the GUI the agent requires the page to check in over the
   local channel within a short deadline. If it does not render, the agent **tears the
   graphical session down** and returns to console narration explaining why. A blank or broken
   desktop is never an acceptable state.

### 2.8 Self-update

- **The Fleet Manager serves the agent binary.** The container is the feed — no S3, no CDN, no
  project-operated bucket in anyone's deployment. The install command, installer and binary all
  come from the same address the agent will later report to, so discovery URL and software
  source are one thing.
- **Custom minimal updater** (Velopack was evaluated and dropped for the agent — §6.2): fetch →
  verify SHA-256 → write `fl-agent.new` → atomic rename → restart. Roughly 150 lines.
- **Hourly out-of-band convergence is the primary mechanism.** The agent checks its Fleet
  Manager every hour, independently of the socket, and **matches the served version — upgrade
  or downgrade, always**. Reverting the container tag therefore reverts the fleet within the
  hour. Every failure mode (protocol mismatch, server restarted, frame offline for a week)
  resolves through this check on its own.
- **The handshake is an optimisation, not a mechanism.** It triggers the update immediately
  instead of waiting for the hourly tick; correctness never depends on it.
- **The applied version is an ordinary resource** — the root of the DAG, with the same
  verification and escalation machinery as everything else.
- **Updates are capability only** — features and fixes, never a configuration channel. On by
  default, operator-disableable per fleet or device.

### 2.9 On-device state and secrets

Device keypair and issued tokens live in root-only files under `/var/lib/fl-agent`, never in
the repository and never in logs. The keypair is generated on first boot and is the device's
permanent identity (§3.3).

---

## 3. FrameLink Fleet Manager

### 3.1 Runtime and packaging

One container: **ASP.NET Core (Native AOT) + SQLite + embedded Svelte GUI + embedded agent
binary + bundled LiveKit.** Single image, single IP/MAC, single firewall rule, single volume.

- SQLite (WAL) via **raw `Microsoft.Data.Sqlite`** — EF Core is documented as unsuitable for
  production under AOT (§6.2). A repository interface keeps a later move to Postgres contained.
- Static files via `UseStaticFiles()`, **not** `MapStaticAssets()`, which silently serves empty
  200s under the slim builder (§6.2).
- Svelte 5 / SvelteKit 2 with `adapter-static` in SPA mode, output served from `wwwroot`.

### 3.2 First run and authentication

- **Single operator, one very long password, from an environment variable only.** No user
  accounts, no roles.
- **An unconfigured instance explains itself rather than failing silently.** With the variable
  unset the container still starts; browsing to the GUI shows a designed page naming exactly
  which variable is missing and how to set it in Docker Compose, with a copyable example.
  Devices that connect are answered `not-configured`, and each frame displays *"connected to a
  Fleet Manager, but it is not set up yet"*. The operator is usually the first person to
  connect a frame, so the frame itself becomes a diagnostic for the server.
- **The `/agent` route is exempt** from the password — devices authenticate by keypair.
- First run then collects the minimum fleet configuration: Immich server URL and API key, room
  name, and fleet defaults. LiveKit's key and secret are generated automatically.

### 3.3 Enrollment and adoption

UniFi-style. **Pointing a frame at a control URL is enough to make it appear**; the operator
presses **Adopt** or **Block**. Blocked devices are filtered from the list by default, with a
"show blocked" toggle so an accidental block is reversible.

- **Identity is the keypair, not a claimed name.** The agent generates a keypair on first boot;
  its public-key fingerprint is the immutable device ID. Adoption binds that key to a record
  and issues identity, room, LiveKit token and desired values. Every later reconnect
  authenticates with the key.
- **Physical matching:** a pending frame displays its short fingerprint and hardware serial on
  screen, so the operator can tell which row is which frame on the bench.
- **A pending device receives nothing** — no configuration, no token, no commands. It may only
  report what it is and display "waiting to be adopted".
- **The registration endpoint is fully open**, with mandatory abuse controls because the server
  is internet-exposed: per-IP rate limiting, a hard cap on pending records, auto-expiry of
  unadopted rows, and pending records that allocate no resources. An attacker can create noise
  rows and nothing else.
- **Decommissioning** is a confirmed, destructive action: the agent erases its keypair, tokens
  and cached content, then re-registers as pending against the same Fleet Manager. An agent
  cannot exist without pointing at one, so "factory reset" naturally means "back in the
  adoption queue".
- **Disaster recovery is re-adoption.** No backup subsystem — the operator backs up the
  volume-mapped data directory. A replacement server at the same URL sees every configured
  agent reappear as pending.

### 3.4 Settings model

**Every setting is fleet-managed: a fleet default with a per-device override**, the override
always winning. Not a fixed list but a generic mechanism, because the list will grow. Covers
connection values (identity, room, LiveKit, Immich), audio (volume), display (backlight
schedule), slideshow (album, interval), locale and time zone, countdown duration, call room,
and whatever comes later.

Calls stay **single-room** with v1's one-button, auto-answer, no-choices behaviour. Because
room is a per-device setting, group calling is already achievable by configuration if ever
wanted — with no new UX for the viewer.

### 3.5 Telemetry, presence and notifications

- **Presence is the socket.** Connection presence *is* online status — no polling, no heartbeat
  table to age out. States: `Online`, `Online-Degraded`, `Incompatible`, `Offline` (with
  *offline since*), `Never enrolled`.
- **Ping/pong every ~20–30 s with a missed-pong deadline.** A pulled plug can leave a half-open
  TCP connection that never closes; without pings, "instant offline detection" covers only the
  polite half of reality.
- **Live reconciliation progress** is a first-class GUI screen: current resource and phase,
  settings applied, settings still drifted, reboots expected before convergence, and the
  per-resource status list.
- **Retention: one month** of events and reconciliation history in SQLite, then rolled off.
  Never any photo or call content.
- **Notifications route through Home Assistant** (already wired here, no new credentials);
  SMTP remains an option for self-hosters without it. Offline beyond a threshold is alertable —
  the exact signal whose absence made the August 2026 incident invisible for days.

### 3.6 Remote access and diagnostics

- **Reverse shell, outbound-initiated, on demand.** Households have zero inbound ports — a v1
  property that must not regress. The shell rides the existing socket, is opened only by an
  explicit action, is audited (who, when, duration, transcript), and auto-closes.
- **Diagnostics allowlist is deliberately small: screenshot and journal tail.** Everything else
  — service status, memory, CPU, resource state — already streams as telemetry, so buttons for
  it would be duplicated surface.

### 3.7 Bundled LiveKit

The Fleet Manager carries `livekit-server` (static Go binary, Apache-2.0, no telemetry unless
configured), generates its configuration, owns the API secret, and supervises it as a child
process with the same restart-and-report discipline it applies to device resources.

- **What it buys:** guide 7 disappears; the LiveKit URL and secret become internal details; call
  tokens become artifacts the Fleet Manager mints at adoption and can rotate at will —
  permanently retiring the credential-expiry failure class rather than deferring it.
- **Version coupling is a feature** — the tested combination is the shipped combination.
- **Network exposure splits in two, unavoidably:** signalling can ride Traefik (WebSocket over
  TLS); WebRTC media cannot, so the stack publishes LiveKit's TCP fallback and UDP range
  directly, as the standalone stack already does. Set `use_external_ip: false` for LAN.
- **Deferred within v2:** advertised IP and TURN/TLS for frames in other households. LAN
  calling is validated first.
- **Escape hatch:** an operator with an existing LiveKit can point the Fleet Manager at it.
- **Migration:** this deployment runs LiveKit as a separate stack (`livekit.yml`,
  `172.16.14.0/24`). Keep it until the bundled path is proven, then retire it.

### 3.8 Deployment (this operator)

Published at **`https://framelink.huisman.io`** through the existing Traefik stack, authored as
a PortainerCompose stack under that repo's rules: own `/24` bridge from `config/networks.yaml`,
pinned IPv4 + MAC (`02:42:` + IP hex), single-homed, `dfw.rules` label, cross-stack reach only
via declared pinholes.

⚠ **Authelia cannot sit in front of the agent route** — machine-to-machine, no interactive SSO.
GUI routes behind Authelia; `/agent` authenticated by device keypair instead.

---

## 4. Protocol

### 4.1 Transport

**One persistent agent-initiated WebSocket over TLS.** Outbound 443 is the only network
requirement at every household; nothing is ever dialled inward.

Logical channels: `telemetry` (loop state, counts, per-resource status), `events` (drift,
escalation, boot), `control` (reconcile now, retry resource, maintenance mode, open shell), and
`shell` (only while a session is open).

**Reconnect discipline:** capped exponential backoff, retry forever, cleanup per failed attempt
— the v1 LiveKit post-mortem lesson (a retry loop that leaks is worse than an outage) applies
verbatim and gets its own test.

**Offline behaviour:** the agent reconciles, verifies, retries and escalates with no server
present; telemetry buffers on disk (bounded) and drains on reconnect.

### 4.2 Handshake and versioning

- **Every socket opens with a version handshake** — on every connect, not just the first.
- **The handshake envelope is frozen forever.** Its shape never changes across any protocol
  version, which is what lets a hopelessly outdated agent still say who it is and report that
  its update failed. Incompatibility is always *legible*, never a silent dead socket.
- **Strict matching is affordable** because a mismatch triggers an immediate update rather than
  waiting for the hourly tick — so no protocol skew window is needed and the agent never
  implements two dialects.
- **The update endpoint never changes shape** either: plain, versionless HTTPS routes outside
  the negotiated protocol, polled hourly regardless of socket state.

### 4.3 Discovery

One code path: **find a candidate endpoint → enroll → persist → never rediscover.** Candidates
come from, in order: an install flag, a boot-partition file, then mDNS (convenience only, never
a dependency).

Agent state stores an **endpoint list** — public URL first, optional LAN address second, tried
in order — so a frame built on the operator's bench survives being shipped to another
household, and hairpin-NAT setups still work from inside.

---

## 5. Execution plan

The build runs largely unattended, which imposes one hard requirement: a **closed feedback
loop** — change code → build → deploy → observe → judge → repeat. Everything here exists to
close it, and the honest limits are stated rather than glossed.

### 5.1 Milestones

| # | Milestone | Done when |
|---|---|---|
| **M0** | **Autonomy harness** | A code change reaches the mule and is verified with no human help: build path, deploy script, power-cycle control, screenshot + journal collection, resumable progress file, test runner. |
| **M1** | **Walking skeleton** | Agent connects → appears pending → adopted in the GUI → reconciles one trivial resource → self-updates from the Fleet Manager. Every *integration* risk retired at once. |
| **M2** | **Reconciler engine** | DAG, status vocabulary, retry/backoff, reboot-verified apply, escalation ladder, live telemetry, console and browser narration. |
| **M3…Mn** | **Resource migration** | Guide by guide, lowest-risk first, firmware DFU last. Each group passes the triple bar: state-diff vs the frozen v1 reference, checkpoint assertions, validation battery on the mule. |
| **Mn+1** | **Bundled LiveKit** | Fleet Manager supervises LiveKit and mints tokens at adoption; guide 7 obsolete. |
| **Mn+2** | **Production Fleet Manager** | Deployed as a PortainerCompose stack behind Traefik at `framelink.huisman.io`, with alerting. |
| **Mn+3** | **Parity** | Stock image → adopt → fully green frame, mechanically equal to the frozen v1 reference. Deep, triple-checked verification. Only then do guides retire to the minimum set (§8). |

**Why skeleton before reconciler:** every genuinely unknown risk is an *integration* risk —
AOT on arm64, the update path, the frozen handshake, adoption, socket liveness. None are hard
once proven and all are miserable to discover late underneath a finished reconciler. After M1
the work becomes pleasantly repetitive: add one resource, verify on the mule, repeat.

### 5.2 Build path

Native AOT cannot cross-compile from Windows, and building on the mule pollutes the target that
gets wiped repeatedly. **Proven path: an emulated `linux/arm64` container on the workstation**
(verified end to end — §6.1). A cross-compiling x64 container
(`dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-arm64`) is the speed optimisation to
benchmark later; GitHub Actions arm64 runners are the eventual CI lane. The same Docker
environment runs the Fleet Manager in development, so the server is not needed until Mn+2.

### 5.3 Hardware prerequisites

1. **Dev mule = the existing frame**, repurposed once Precondition zero's artifacts exist. It
   is already assembled and already on a controllable smart plug — the harness is in place.
2. **2–3 spare microSD cards**: one holding the v1 image (recovery), the others stock Raspberry
   Pi OS Lite with SSH enabled. Bare-metal provisioning gets tested many times, and a card swap
   is the recovery path for a boot-breaking resource.
3. **An SD card reader** — not currently attached (§6.1).
4. **The GPIO button** — still unsourced; needed only when parity reaches guide 11.
5. **Fleet behaviour needs no extra hardware:** multi-device features are exercised with
   **virtual agents** — the same agent built for linux-x64, running in containers. They cover
   everything except hardware-touching resources.

### 5.4 Access prerequisites

Supplied in-session, never written to any file (repo rule §1.2):

1. **Frame SSH** — hostname/IP and password. ✅ available
2. **Home Assistant** + the frame's plug entity and authorisation to switch it. ✅ available
3. **Docker Desktop running** (ideally auto-starting) — unlocks both the build container and
   the development Fleet Manager. ⚠ was not running; started manually
4. **GitHub push access.** ✅ working
5. **At Mn+2 only:** Portainer API, PortainerCompose stack authorisation, DNS + Traefik route
   for `framelink.huisman.io`.

### 5.5 Honest limits of autonomy

- **An unbootable mule needs hands.** A malformed `config.txt`, a bad EEPROM write or a failed
  DFU can produce a device nothing remote can reach. Mitigations: validate before writing, keep
  and restore backups, boot-count self-repair, and schedule brick-capable resources last.
  Residual risk is covered by pre-flashed spare cards — a swap, not a flashing session.
  **One carve-out from the ordering clause (decision 46):** the display overlay and the console
  rotation are scheduled *first*, because §2.7's narration cannot exist without a lit panel and a
  write to `/dev/tty1` on a dark frame succeeds silently. They keep every other mitigation in this
  bullet, and an early brick is the cheaper one — the card swap costs three reboot cycles of work
  rather than a whole provision. The ordering is narrowed by two resources; nothing else changes.
- **Physical assembly, wiring and card swaps** are always human steps.
- **Long runs lose context**, so progress is written to disk continuously and any session can
  resume mid-milestone.
- **All work lands on `main`** — no branches. The progress file and the parity harness protect
  against a half-built state, not branch ceremony.
- **The frame is unavailable as a photo frame while it is the mule.** Accepted: desk prototype.

---

## 6. Verified findings (2026-08-15)

### 6.1 Infrastructure — checked hands-on

| Check | Result |
|---|---|
| **Full build→deploy→run loop** | ✅ **PROVEN.** Native AOT `linux-arm64` built in an emulated arm64 container, deployed to the frame in 0.1 s, executed: `arch=Arm64, Debian GNU/Linux 13 (trixie), .NET 10.0.11`. Binary **1.35 MB**, links only `libc`/`libm`. |
| Docker | Installed, **was not running**; now `linux/amd64 server 29.7.2`, arm64 emulation confirmed |
| Workstation | 32 cores, 128 GB RAM, 506 GB free, WSL2 + Hyper-V |
| Toolchain | .NET SDK 10.0.302, Node v26.7.0 / npm 11.19.0, Git 2.55.0 |
| Frame | Pi 5 Model B Rev 1.0, Trixie, aarch64, kernel 6.12.75, 107 GB free of 119 GB |
| `sudo` NOPASSWD | ❌ **Not present, and not depended on.** Re-measured on a stock image 2026-08-15: the first user is in the `sudo` group, `/etc/sudoers.d` carries only the packaged drop-ins, and `sudo -n true` answers `sudo: a password is required`. The earlier ✅ was taken on the hand-built v1 frame and does not survive a reflash — which is the case that counts, since v2 bootstraps from a stock image. **The agent needs no `sudo` at all:** `fl-agent.service` sets `User=root`, so systemd starts it as root. Only the harness elevates, and it answers on stdin (`sudo -S`, probed once per connection). |
| FUSE | ✅ `/dev/fuse`, setuid `/usr/bin/fusermount3`, module loaded |
| glibc / systemd | 2.41 / 257 |
| Frame → workstation | ✅ HTTP 200 in 1.3 ms; Docker-published port 2 ms — firewall not an obstacle |
| Frame → internet | ✅ Debian repos and GitHub reachable |
| Smart plug | ✅ controllable, 9.24 W idle |
| Portainer API / LiveKit | ✅ verified |
| **SD card reader** | ⚠️ **not attached** |

### 6.2 Toolchain — researched with sources

**Cleared:**

- **Native AOT arm64** — proven end to end. Windows→Linux AOT is unsupported (irrelevant given
  Docker). Target `net10.0` (LTS, EOL 2028-11); .NET 8 and 9 both EOL 2026-11-10.
- **LiveKit** — static amd64/arm64 binaries, Apache-2.0, no default telemetry, fully
  configurable by env var or file, no init-system dependency.
- **Immich Kiosk** — standalone `Linux_arm64` binary (7.4 MB, Go, AGPL-3.0), v0.42.0.
- **ASP.NET Core under AOT** — static files ✔ and WebSockets ✔, the two things needed. Traps:
  `MapStaticAssets()` silently serves empty 200s under the slim builder (aspnetcore#58986,
  open); **EF Core is documented as production-unsuitable under AOT** — use raw
  `Microsoft.Data.Sqlite`.
- **Svelte 5 / SvelteKit 2** with `adapter-static` in SPA mode is the documented embedding path.

**🚩 Velopack is not suited to systemd daemons — the finding that changed a decision.**
Velopack applies updates by spawning a helper as an ordinary child process, which under systemd
lands inside the service cgroup; the default `KillMode=control-group` kills it the instant the
daemon exits — exactly when the update is being applied. The maintainer states plainly that
Velopack "is still not suited for this" for daemons, with systemd integration an open issue and
no timeline. Workarounds exist (`KillMode=process`, `systemd-run --scope`) but place the
fleet's entire update path on an explicitly unsupported road. Everything else about Velopack
checked out on Linux/arm64 — custom HTTP feed, downgrade support, channels, AOT compatibility —
so the decision was narrow: **keep the design, replace the mechanism** (§2.8). Velopack remains
the right tool for desktop apps such as UCC; its value is installer/delta/shortcut/GUI shaped,
none of which applies to a 1.35 MB headless ELF.

---

## 7. Project policies

### 7.1 Versioning and dependencies

**"Everything floats, the build freezes it"** (from BrowserAI, reinforced by Jeeves): every
dependency resolves to latest at build time and is frozen into the artifact. Never pin to work
around a break — fix forward. Version claims are never asserted from memory, only verified per
session and stamped `Verified <date> @ <version>`. An `upstream-review.json` ledger records
human-reviewed upstream versions, enforced by a marker test that fails when the build resolves
something newer than the last review.

**Fleet synthesis:** aggressive at build, gated at release (mule + test suite), frozen at
deploy — artifacts promote through the Fleet Manager; frames never resolve dependencies.

### 7.2 Testing doctrine

The suite is the only gate between a change and a release. Every behaviour change ships with
tests that assert outcomes, not the absence of exceptions. Every bug fix starts with a failing
reproduction. **No red test ships; no skipped or quarantined test ships.** Analyzers run at
error severity and are never weakened to make code pass. A `docs/MODERN.md` idiom baseline is
maintained, and each significant change gets an adversarial modernity review.

### 7.3 Licensing

The whole repository relicenses to the bespoke **FSL-1.1 (MIT Future License) five-year
variant** — source-available, converting to MIT on the fifth anniversary of each release.
Identifier `LicenseRef-FrameLink-FSL-1.1-MIT-5yr`; never the bare `FSL-1.1-MIT`. No per-file
SPDX headers.

Verified facts: **sole authorship** (42/42 commits), so relicensing forward is unencumbered.
**Previously published versions stay EUPL-1.2** — that grant is irrevocable for released
commits, which is worth stating plainly rather than implying withdrawal. **Vendored assets keep
their own licences** (`lit` BSD-3-Clause, `livekit-client` Apache-2.0).

Execution checklist, not yet performed:

1. Replace `LICENSE` with the FrameLink FSL five-year variant (adapted from BrowserAI).
2. Rewrite the licence section of `README.md`.
3. Update `TRADEMARK.md`, which references the EUPL split.
4. Add `THIRD-PARTY-NOTICES.md` for the vendored assets.

### 7.4 Visual design

Both interfaces are held to a high aesthetic standard — beautiful and richly animated, not
utilitarian, with motion as a first-class part of the design. **One design language across
both**, no toned-down variant for the device. A design system (palette, typography, motion) is
defined and documented in the repo, then applied consistently. The on-device UI runs in
Chromium with GPU rasterisation already proven on this hardware; the Fleet Manager's Svelte
stack has motion primitives built in. The console stage is held to the same bar within its
medium.

### 7.5 Tooling

MCP servers for v2 development: `microsoft-learn`, `nuget`, `github` (read-only), `context7`
(metered fallback), plus `svelte` and `mdn` for the GUI. Playwright servers deferred until GUI
E2E testing begins.

---

## 8. Deferred to v3

- **Fleet Manager MCP server** — the operator's local AI agent talking straight through to one
  or many devices via the shell channel.
- **SD image generation from the Fleet Manager** — ready-to-flash, with URL, Wi-Fi and settings
  pre-seeded.
- **Camera privacy** — an on-screen live-camera indicator, and a physical shutter as an
  enclosure design requirement.
- **Fleet Manager credential management and single sign-on** (multi-user, roles, SSO).
- **On-device Wi-Fi configuration** (rescue hotspot or setup screen) for households that change
  routers. ⚠ Until then, a router or password change strands a frame until someone is on site.

**Guide fate at parity:** after deep, triple-checked verification the guides shrink to hardware
assembly, Raspberry Pi OS install, agent install, and Fleet Manager container setup. Everything
else is deleted — git history preserves it. Nothing is deleted before parity is proven.

---

## Appendix A — Decision log

The record of *what was decided and why*, in the order decided.

| # | Question | Decision |
|---|---|---|
| 1 | Naming | **FrameLink Agent** / **FrameLink Fleet Manager**; code `FrameLink.Agent`/`FrameLink.Control`; system `fl-agent`/`fl-control` |
| 2 | Agent scope | **Maximal agent** — provisions everything from bare Trixie; guides become the specification |
| 3 | Migration order + verification bar | Risk-minimising order; verified = state-diff + checkpoint assertions + validation battery |
| 4 | Resource dependencies | **Explicit lightweight DAG** with `Blocked(dependency)` |
| 5 | Drift policy | Always auto-correct; report detours during provisioning, every drift after convergence |
| 6 | Reboot strategy | Reboot per atomic setting; do not minimise |
| 7 | Failure escalation | Budget → `Degraded` → notify + retry/shell choice → `Halted` |
| 8 | Remote access | Reverse shell, outbound-initiated, on demand |
| 9 | Transport | One persistent agent-initiated WebSocket over TLS, multiplexed |
| 10 | Discovery | Layered: install flag → boot file → mDNS; endpoint list persisted |
| 11 | Control endpoint | `https://framelink.huisman.io`; Authelia cannot front the agent route |
| 12 | Update delivery | Feed served by the Fleet Manager; the server is updated out-of-band by the operator |
| 13 | Version handshake | Every socket; a mismatch is the root DAG resource, fixed immediately |
| 14 | Screen ownership | The agent owns the screen whenever anything is not green; any drift stops the product |
| 15 | Catalog authority | **Static logic, dynamic values** — offline autonomy is a product requirement |
| 16 | Live presence | Socket presence *is* online status |
| 17 | Enrollment | UniFi-style adopt/block; generic image, no secrets |
| 18 | Repo + licensing | Single monorepo, whole project relicensed to FSL-5yr |
| 19 | Fleet Manager runtime | One container: ASP.NET Core AOT + SQLite + Svelte + bundled LiveKit |
| 20 | Repair screen | Narrated, deliberately paced, never silent |
| 21 | Telemetry retention | 1 month |
| 22 | Notifications | Home Assistant |
| 23 | Branching | None — everything on `main` |
| 24 | Guide fate | Deep parity check, then reduce to the minimum set |
| 25 | Countdown duration | Install flag → fleet default → per-device override; 0 in development |
| 26 | Reboot rule | Every resource reboots; no per-resource cleverness |
| 27 | Drift during a call | The call is killed |
| 28 | Diagnostics allowlist | Screenshot + journal tail only; the rest is telemetry or the shell |
| 29 | Visual bar | Beautiful and richly animated on both surfaces |
| 30 | Network onboarding | Install-time precondition check; no Wi-Fi UI in v2 |
| 31 | Disaster recovery | No backup subsystem; recovery is re-adoption |
| 32 | Image generation | v3 |
| 33 | Visual direction | Free rein, documented, one language for both |
| 34 | Adoption | A reconciled resource; an unadopted frame runs no product |
| 35 | Settings model | Everything fleet-managed: fleet default + per-device override |
| 36 | Agent packaging | One AOT binary; no supplemental program files, ever |
| 37 | Update mechanism | Custom minimal updater; Velopack dropped for the agent |
| 38 | Unreachable ≠ rejected | Keep the product running if the frame was green when contact dropped |
| 39 | App delivery | The agent embeds and serves the product app |
| 40 | Immich Kiosk | Keep upstream; supervised child process; no Docker on the frame |
| 41 | Kiosk delivery | Agent fetches the pinned release from upstream and verifies its checksum |
| 42 | Decommissioning | Confirmed action; wipe and return to pending |
| 43 | Fleet Manager auth | Single operator, one long password from an environment variable |
| 44 | Call rooms | Single room, fleet-controlled |
| 45 | First run | Unconfigured server explains itself on every surface |
| 46 | Display ordering | Overlay + console rotation go **first**, ahead of adoption — §2.7 outranks §5.5's brick-capable-last for this one group; every §5.5 mitigation kept, brick risk accepted |

## Appendix B — Open items

Not blockers for starting; each has a recorded default that applies unless overridden.

1. **Resource catalog** — the enumeration of every atomic setting extracted from guides 3–12.
   This is the first task of M3, not a design question.
   - ⚠ **Known trap, observed on the mule 2026-08-15:** the hostname is **cloud-init managed**
     on this image. `hostnamectl set-hostname` appears to succeed and is silently reverted at
     the next boot, even with a `preserve_hostname: true` drop-in. The hostname resource must
     therefore act on cloud-init's configuration, not on `hostnamectl` — and this is a textbook
     illustration of decision 26 (every resource reboots to prove it stuck): a write-only check
     would have marked this `InSync` while it was quietly wrong.
2. **Cross-household connectivity** — advertised IP, TURN and TLS for frames outside the
   operator's LAN. Deferred within v2; LAN calling is validated first.
3. **Design system specifics** — palette, typography and motion language, to be defined and
   documented at M2 with the first real screen shown early for correction.
4. **Chromium/OS update policy** — Debian security-only auto-updates stay on, expressed as a
   reconciled resource so the policy is visible and centrally changeable.
5. **Cross-compile benchmark** — emulated arm64 builds are proven; measure whether the
   cross-compiling container is worth adopting.
