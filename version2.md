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
- **Convergence is the whole job.** Keeping a correctly-configured system running — restarting a
  bloated browser, recycling a wedged camera node — is *supervision*, a second agent responsibility
  standing beside this loop with opposite rules about interrupting the product (§2.10).

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
call continuity; in normal operation nothing drifts, and when it does, everyone can see why. A
supervised restart is not drift and never triggers this rule (§2.10).

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
4. **A countdown bar** before the verifying reboot (default 60 s), with a **"Reboot now"**
   button to skip once read — a tap on the touchscreen, and the same skip is available
   remotely. This is the one screen where v1's touch shield must *not* block input. It runs on
   drift repair only; initial provisioning does not count down (decision 51).
5. **Attempt number** when retrying ("Attempt 2 of 5").
6. **Backoff state** including remaining wait, so a pause never looks like a hang.
7. **Escalation state** when the budget is exhausted and the operator has been notified.

Countdown duration resolves in one order, most specific first: **per-device override → fleet
default → 60 seconds** (decision 48). Both configured levels are Fleet Manager settings (§3.4)
and both are resolved server-side, so a frame is told one already-effective value. The install
flag and boot-partition file decision 25 put above them are **gone**: the operator considered
that channel and removed it, and the boot file went with it rather than surviving alone, because
it existed only as the flag's local, pre-adoption sibling.

**Scope: the countdown is for drift repair, not for initial provisioning** (decision 51). The
pause exists so that a person can read what is being done before the screen changes, and that is
an argument about a viewer standing in front of a working frame while a repair takes their photos
away. Initial provisioning has neither half of it — the frame has never displayed anything, nobody
is waiting in front of it, and nothing is being interrupted. A frame that has never reached
`InSync` therefore reboots as soon as a resource is applied; once a frame has been green, every
later repair gets the full countdown, because then the transparency argument above holds
completely. The condition is *has this device ever been `InSync`*, persisted in the agent's
progress journal beside the attempt ledger (§2.1) so that it survives the reboot every resource
takes and the version change every update brings, and it is never cleared — a frame that has been
green and later drifts is still a frame with a viewer. What made the scope worth stating is the
arithmetic: 79 resources at 60 s is 79 minutes of countdown against 29 minutes of measured reboot,
so three quarters of a bare provision would have been spent pausing for nobody.

One consequence follows, and it is stated rather than worked around. §3.3 gives a pending device
*nothing* — no configuration at all — so the built-in 60 s is the whole chain an unadopted frame
has, and "development runs use 0" is not reachable through configuration. Decision 51 answers that
for provisioning — adoption is itself a resource (decision 34), so an unadopted frame has never
been green and does not count down at all — but it does not answer it for a mule that has already
converged once. That mule *is* a frame that has been green, and every repair on it pauses like any
other. What serves it is `--development`, a switch on the agent binary that forces the countdown
to zero. That is a local
debugging switch and deliberately not a settings channel: it is an argument chosen by whoever
starts the process on the machine they are sitting at, nothing writes or persists it, no operator
can push it from the Fleet Manager, and it lasts exactly as long as that process. Removing the
install flag and keeping this are therefore not in tension.

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

### 2.10 Supervision

The agent has two jobs, not one. §2.2's loop converges *declared state*; supervision keeps a
correctly-configured system *running*. They are separate responsibilities because they answer
different questions and, as §2.6 shows, must obey opposite rules about interrupting the product.

**The distinguishing question: is the desired state wrong, or is a correctly-configured thing
misbehaving?** Reconciliation owns the first, supervision the second. A mixer control that no
longer reads what it should, a `config.txt` line someone deleted, a unit file whose hash moved —
declared and observed disagree, and the loop converges them. A browser renderer that has leaked
900 MB, a camera node hung in shutdown while its unit still reports `active`, a page that stopped
answering — every declared setting is exactly right and the running system is sick anyway. No
amount of level-triggered convergence finds those, because there is nothing to compare against:
the fault is in behaviour, not in state. The test generalises to cases not on the list below. If
you can name the file, value or unit content that is wrong, it is a resource; if the honest answer
is "nothing is wrong with the configuration, it just stopped working", it is supervision.

**The four supervised behaviours**, all carried over from v1, all measured on this hardware:

| Behaviour | Trigger | Action |
|---|---|---|
| **Memory watchdog** | Chromium process tree over 1.8 GB RSS, **or** system `MemAvailable` under 350 MB, sampled every five minutes | Restart the browser |
| **Daily restart** | 03:00 local, catching a missed run up once after an outage | Restart the browser |
| **Kiosk liveness** | The app's local channel silent for 90 s, evaluated every 15 s, five-minute cooldown | Restart the browser |
| **Camera recycle** | Every call-end | Restart the camera node |

The numbers are evidence, not preference:

- **Sum the whole process tree, never the main process.** A leaking renderer grew past 1.4 GB and
  was OOM-killed while the main process sat at an innocent 130 MB. A watchdog that reads the main
  process never fires.
- **1.8 GB is deliberately high.** After hours of slideshow the healthy tree legitimately reaches
  ~1.7 GB of iframe image cache — released the instant the iframe unloads — and a full six-way call
  runs a lean ~1.3 GB, so any lower ceiling restarts healthy frames. The measured pathologies cross
  it quickly regardless: a dead slideshow iframe leaked 50 MB/min, and an expired token's
  connect-reject-retry loop 15 MB/min.
- **The 350 MB floor is the sharper instrument.** System-wide multi-second stalls began once free
  memory fell into the low hundreds of megabytes, whatever was consuming it; the browser is always
  this machine's largest tenant, so restarting it is the right answer to pressure from any source.
- **90 s catches what systemd cannot.** An OOM-killed renderer leaves an "Aw, Snap!" tab while the
  unit stays `active`, so `Restart=` never fires — the app's dropped local channel is the only
  honest liveness signal. Validated live: a SIGKILLed renderer healed in exactly 90 s. Restarting
  the browser is deliberately the *first* recovery action anywhere on the frame, because it frees
  the renderer's memory and on a starved system nothing else can run until that happens.
- **The daily restart bounds age where the watchdog bounds size.** A session left up for weeks
  accumulates staleness no threshold measures.
- **The camera recycle is a workaround with a known expiry.** `gstpipewiresink` in PipeWire 1.4.x
  (Trixie ships 1.4.2) raises a fatal element error when a consumer tears down abruptly and can then
  hang in shutdown, leaving the unit `active` with a dead stream — measured, the third acquisition
  on one node instance failed, and `Restart=always` cannot fire on a hung process. PipeWire ≥ 1.6.0
  guards that path; when the OS carries it, this behaviour is switched off by setting, then deleted.

**The camera recycle is the same responsibility, not a second one.** Its trigger is a product event
rather than a health check, but what it fixes is identical in kind — a correctly-configured unit
misbehaving in a way its own restart policy cannot catch — and v1 demonstrates the unity by having
implemented it in the same daemon as the liveness watchdog. Supervision therefore has two trigger
kinds, **health-triggered** and **event-triggered**, sharing one reporting path, one interlock and
one settings mechanism. One asymmetry follows and is worth stating: the recycle is prophylactic and
fires whether or not anything is wrong, so one restart per call is the *healthy* rate — which is
why the fault counter below is per behaviour and never a fleet-wide total.

**Supervision does not stop the product, and that is the whole reason it is not a resource.** §2.6
holds that any drift stops the product, including an active call, and that is correct for drift: a
drifted frame is not the frame the operator declared, and running a product on top of an unknown
configuration is worse than showing the reason. But a supervised restart is not a departure from
the declared state — it *is* the declared state being kept alive, and what comes back comes back
into the same configuration it left. Modelling supervision as drift would force the two rules into
collision and one of them would have to yield: either drift stops being absolute (correctness lost)
or a routine browser blink blanks the frame, kills the call and shows a repair screen every morning
at 03:00 (continuity lost). Keeping them separate lets each rule stay absolute in its own domain —
drift always interrupts, supervision never does.

Two consequences follow directly. **Supervision never reboots the device**: its entire vocabulary
is restarting a supervised process, and a reboot blanks the frame for a minute, which is exactly
the product-stopping behaviour it must not have. When restarting is not enough, the handoff below
gives the problem to reconciliation, which may reboot with the full §2.7 narration. And
**supervision defers only what can wait**: the daily restart stands down while a call is active and
runs at the next opportunity, exactly as v1's `Persistent=true` catches a missed run up, while the
memory watchdog defers for nothing — the alternative to acting during a call is an OOM kill or a
hardware-watchdog reset, which ends that call anyway and takes the frame with it.

**The interlock with the reconciler.** Both actors can touch the same unit and each can misread the
other's work, so one rule covers both directions: **the reconciler holds a lock on what it is
applying, and a supervision action opens a window on what it touches.**

1. Supervision does not act on anything the reconciler is `Progressing`, `AwaitingReboot` or
   `Blocked` on. Restarting a browser the reconciler is deliberately holding down, or racing an
   apply, produces exactly the interference that makes "which change broke it" unanswerable (§1.2
   principle 5).
2. While a supervision window is open, the transient wrongness it causes — a kiosk process that is
   briefly not running — is expected rather than drift, so it never trips §2.6.
3. The window closes when the supervised thing is healthy again, or when
   `supervision.recoveryDeadline` expires. **If the deadline expires the condition becomes ordinary
   drift**, and everything §2.6 and §2.7 prescribe takes over: the device leaves `InSync`, the
   product stops, the screen narrates, and a browser that will not render at all falls to console
   narration under §2.7's fallback rule. Supervision owns the transient, drift owns the persistent,
   and the deadline is the boundary between them.

Supervision runs at full strength in `NoContact` — that is the case where no help is coming, and it
is the offline half of §1.2 principle 2. It stands down in every state where the product is not
running, because the agent owns the screen then and restarting a browser that is deliberately not
showing the product repairs nothing.

**What supervision reports.** Nothing is repaired invisibly (§1.2 principle 3). Every action emits
on the `events` channel — which behaviour fired, the measured value against its threshold, what was
restarted, how long recovery took — and lands in the Fleet Manager's per-device history beside
reconciliation events, under the same one-month retention (§3.5). It buffers offline like
everything else on that channel (§4.1), so a frame that spent the night restarting itself tells the
whole story when it reconnects.

**Repeated supervision is itself a fault, and it does not reuse §2.5's ladder.** That ladder is
budget-based and ends in `Halted` — stop touching it — which is the right terminal state for a
resource that cannot be applied and the wrong one for a frame that needs restarting to stay alive:
giving up there means a dark frame. Supervision's signal is a *rate*, not a budget, because each
action is individually legitimate and the abnormality is the frequency. More than
`supervision.faultRateThreshold` actions of one behaviour within `supervision.faultRateWindow`
raises a **supervision fault**, which notifies the operator through the same §3.5 path as an
escalation. The fault never inhibits supervision — the restarts continue, because a frame
restarting every ten minutes still beats a dark one — so escalation here is diagnostic where
§2.5's is inhibitory. A frame that keeps needing repair is a broken frame someone has to look at,
even though every individual repair was correct.

**Against the device state ladder: an annotation, not a rung.** §2.6's rungs answer exactly one
question — does the product run? — and a supervision action does not change that answer, so it
cannot become a rung without either duplicating `InSync` or stopping the product. **A supervised
restart while `InSync` leaves the device `InSync`.** The one addition proposed is an annotation any
state can carry — `Supervision(behaviour, last action, rate)` — surfaced in telemetry and on the
Fleet Manager's device row. Below fault level it is operator-facing only. At fault level it also
renders on the frame as the small persistent overlay §2.6 gives `NoContact`, because a frame
visibly blinking every ten minutes is an abnormal condition and principle 3 says abnormal
conditions are named on the frame's own screen. The overlay does not stop the product; that is the
point of it being an annotation.

**The constants are fleet settings** under §3.4's fleet-default-plus-per-device-override mechanism,
so a threshold can be retuned across the fleet, or on one struggling frame, without a release. They
are settings rather than resources because they have no independent on-device drift surface —
nothing on disk holds them, the agent holds them in memory.

| Setting | Default | Governs |
|---|---|---|
| `supervision.browserTreeRssCeilingKb` | `1843200` | Chromium tree RSS ceiling |
| `supervision.memAvailableFloorKb` | `358400` | System available-memory floor |
| `supervision.memoryCheckInterval` | `5m` | Sampling interval for both memory limits |
| `supervision.dailyRestartTime` | `03:00` | Scheduled browser restart; empty disables it |
| `supervision.kioskSilenceTimeout` | `90s` | Local-channel silence that triggers a restart |
| `supervision.kioskCheckInterval` | `15s` | How often that silence is evaluated |
| `supervision.kioskRestartCooldown` | `5m` | Minimum spacing between liveness restarts |
| `supervision.cameraRestartOnCallEnd` | `true` | Per-call camera recycle; off at PipeWire ≥ 1.6 |
| `supervision.recoveryDeadline` | `2m` | When an unrecovered supervision action becomes drift |
| `supervision.faultRateThreshold` | `3` | Actions of one behaviour that raise a fault |
| `supervision.faultRateWindow` | `1h` | The window that count is taken over |

**Two v1 self-heal behaviours do not come along.** `docker-selfheal` is moot: §2.1 removes Docker
from the frame, which deletes the corrupt-network-store failure class rather than repairing it. Its
shape *was* supervision — a correctly-configured daemon refusing to start because of damaged
runtime state, healed by a narrow signature match that deliberately left every other failure to a
human — and that classification is worth keeping even though the behaviour has nothing left to act
on. `sshd-mute-monitor` was never product: its own unit description calls it testbed diagnostics,
and what it improvised — noticing that a frame had entered system-wide stalls — is what continuous
telemetry and offline alerting (§3.5) now do by design, with the underlying symptom covered
directly by the `MemAvailable` floor. Neither is reimplemented.

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
escalation, supervision, boot), `control` (reconcile now, retry resource, maintenance mode, open
shell), and `shell` (only while a session is open).

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
2. **Home Assistant** at `http://10.20.30.250:8123` — port verified against the live instance
   2026-08-15 (§6.1) — plus the frame's plug entity and authorisation to switch it. ✅ available
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
| **Home Assistant** | ✅ **`http://10.20.30.250:8123`**, verified 2026-08-15: `GET /api/` answers `{"message":"API running."}`. **The harness had the wrong port.** It defaulted to `:8086`, which is a different service on the same host (an MCP server) answering HTTP **404 for every path, including `/api/`** — so an entity lookup returned a 404 that the harness reported as *"Home Assistant does not know `switch.wall_plug_25`"* while that entity was live and drawing **3.54 W**. A misleading diagnostic costs more than none: it sends the reader to check the one thing that was never wrong. `tools/harness` now defaults to 8123 and, on any 404, probes `/api/` first so the message distinguishes a wrong entity from a server that is not Home Assistant at all. |
| Reboot cost | ✅ **22.3 s** relay-on to SSH-ready, measured twice 2026-08-15 (22.3 s and ~20 s) with loss of port 22 confirmed between them. Materially cheaper than the ~57 s per resource the resource catalog's 75–80 minute budget for 79 resources implied. **Re-derived:** the catalog now budgets **~30 minutes** of reboot overhead for 79 resources, quoting §2.7's countdown as a separate configurable term rather than folding it in. Cost was the main argument against decision 26, and the real cost is under half what was assumed. |
| `POWER_OFF_ON_HALT` | ✅ **set to 1** in this Pi's EEPROM, so `halt`/`poweroff` genuinely cut power rather than leaving the board idling. A silent frame on a live relay therefore has three explanations, not two: booting, hung, or halted and drawing nothing. |
| Own-hostname resolution | ⚠️ **stale `/etc/hosts` after a rename, 2026-08-15.** `hostnamectl set-hostname` does **not** maintain `/etc/hosts`, so `127.0.1.1` still named the shipped hostname; resolution fell through to DNS and the search domain answered `getent hosts framelink-mule` with `217.61.253.65 framelink-mule.huisman.io` — **the frame resolved its own name to a public internet address.** Anything that resolves its own name (a service bind, a certificate, an advertised media address) is pointed at a machine that is not this one. `sudo`'s "unable to resolve host" warning is the only signal, and the harness had been suppressing it as benign; it now reports it once per connection. **This, and not the cloud-init trap Appendix B used to record, is what makes `identity.hostname` worth doing correctly** — the trap did not reproduce on the same session, while this did, twice. The hostname resource must own `/etc/hosts` too. |
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
- **Platform independence across the whole product** — software, operator tooling and guides.
  Nothing *shipped* is Windows-bound: the agent is a Linux ELF, the Fleet Manager a Linux
  container, both built inside Linux containers. The gap is the operator's workstation.
  [Guide 2](docs/2-sd-flash-first-boot.md) is built around thirteen Raspberry Pi Imager captures
  with Windows title bars, one showing the card `Mounted as F:\` — the tool is cross-platform,
  the screenshots are not, and re-shooting them is the expensive part — and guides
  [7](docs/7-livekit-server.md) and [8](docs/8-webrtc-validation.md) install the LiveKit CLI with
  the same `winget` line, the only Windows-only command in any guide. The harness under
  `tools/harness/` is already portable and no operator runs it, so it is the smallest part of
  this. **SD image generation** above (decision 32) may deliver most of the rest on its own: hand
  someone a ready-to-flash file and their workstation's OS stops mattering. ⚠ Until then, a
  household with no Windows machine has no supported path through the guides.

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
| 25 | Countdown duration | Install flag → fleet default → per-device override; 0 in development — **superseded by decision 48**, kept here as the record of what was decided first |
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
| 47 | Supervision | A **second agent responsibility beside reconciliation**, not a kind of resource (§2.10). Memory watchdog, 03:00 restart, 90 s kiosk liveness and per-call camera recycle are health- or event-triggered repairs of correctly-configured things, so they never stop the product — modelling them as drift would collide with §2.6. Reports on `events`; escalates by *rate*, not §2.5's budget; annotates the §2.6 ladder rather than adding a rung; the measured constants become `supervision.*` fleet settings |
| 48 | Countdown resolution | **Per-device override → fleet default → 60 s**, most specific first (§2.7). **Supersedes decision 25**, which stays above as history. The install flag is removed outright — considered and rejected — and the boot-partition file goes with it, since it existed only as the flag's local pre-adoption sibling and keeping it would preserve exactly what was deleted. Both surviving levels are Fleet Manager settings, so §3.3's "a pending device receives nothing" means an unadopted frame can only ever get 60 s. `--development` is **kept** as a local debugging switch that forces 0: an argument to the binary, not a setting, and therefore not a reintroduction of the flag. **Scoped by decision 51**, which leaves this chain intact and narrows which reboots it applies to |
| 49 | Halt scope | `Halted` is **device-level, not resource-level**. One resource exhausting its escalation budget stops the loop touching *everything* on that device — including resources ordered ahead of the halted one, and across process restarts. Continuing to reboot a frame an administrator has been told about twice is the same damage under another resource's name |
| 50 | Display granularity | The display is **two resources**, panel overlay and console rotation, so a dark panel and a sideways one are different diagnoses (§2.2). The dependency runs one way only — rotation depends on the overlay, never the reverse — so a failed cosmetic rotation can never keep the panel dark or mark it `Blocked`. A sideways console is a strictly better state than a dark one, which is what makes the split affordable under decision 46's early scheduling |
| 51 | Countdown scope | The countdown applies to **drift repair, not to initial provisioning** (§2.7). It does not supersede decision 48 — that chain still decides *how long*; this decides *whether at all*. §2.7's reason for the pause is a viewer in front of a working frame reading what a repair is about to do before it takes their photos away, and initial provisioning has no viewer and no product to interrupt: the frame has never displayed anything and nobody is standing there. At decision 48's 60 s that pause costs 79 minutes across 79 resources against 29 minutes of measured reboot — three quarters of a bare provision spent waiting for nobody. So a frame that has never reached `InSync` reboots as soon as a resource is applied; once it has been green, every later repair pauses in full. The condition is durable state — first-green is written to the progress journal beside the attempt ledger (§2.1), never inferred from the link, the hub or anything else that resets at boot — and it is never cleared. `--development` is unaffected and still forces 0, which is what covers the mule *after* its first convergence, since by this decision it is then a frame that has been green. One function decides it, so reverting is one edit |

## Appendix B — Open items

Not blockers for starting; each has a recorded default that applies unless overridden.

1. **Resource catalog** — the enumeration of every atomic setting extracted from guides 3–12.
   This is the first task of M3, not a design question.
   - ⚠ **The hostname trap recorded here has been measured and does not reproduce.** This item
     used to state that the hostname was cloud-init managed and silently reverted at the next
     boot. On the mule 2026-08-15 it **survives** — `hostnamectl set-hostname framelink-mule`,
     then a real reboot (`boot_id` changed), and the name is still `framelink-mule`, with
     cloud-init logging nothing about hostnames. The shipped seed carries no hostname to
     re-apply, and cloud-init's `update_hostname` stands down once it sees a human has taken
     over. The earlier observation was made on the Imager-flashed v1 frame, which is provisioned
     differently; whether an Imager-written card genuinely behaves that way is **a difference to
     verify, not an established fact.** No mechanism is asserted here any more.
   - ✅ **A real defect was underneath it, and it is worse than a wrong name.** `hostnamectl`
     does not maintain `/etc/hosts`. After the rename `127.0.1.1` still named the old host, so
     the frame resolved **its own name** through DNS to a public internet address — anything
     binding to that name, or advertising it, is pointed at a machine that is not this one
     (§6.1). The corrected `identity.hostname` entry in
     [the resource catalog](reference/resource-catalog.md) is the authority: `hostnamectl` plus
     an idempotent `/etc/hosts` rewrite, Observe checking both the name *and* that
     `getent hosts $(hostname)` answers loopback, and the resource reclassified as **not**
     brick-adjacent, since nothing under `/boot/firmware` is written.
   - **Decision 26 survives the correction intact.** A write-only check would still have been
     wrong here — `hostnamectl` returns success while the resource is half-applied at that
     instant — so the reboot is still what proves the whole state rather than the half the tool
     owns. The lesson was right; only the mechanism attributed to it was not.
2. **Cross-household connectivity** — advertised IP, TURN and TLS for frames outside the
   operator's LAN. Deferred within v2; LAN calling is validated first.
3. **Design system specifics** — palette, typography and motion language, to be defined and
   documented at M2 with the first real screen shown early for correction.
4. **Chromium/OS update policy** — Debian security-only auto-updates stay on, expressed as a
   reconciled resource so the policy is visible and centrally changeable.
5. **Cross-compile benchmark** — emulated arm64 builds are proven; measure whether the
   cross-compiling container is worth adopting.
