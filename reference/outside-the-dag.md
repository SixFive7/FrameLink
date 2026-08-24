# Everything on a frame that is not a resource in the graph

Read from the code, not from `version2.md`. Every claim cites the file and line it came from.
This continues `reference/reconcile-ordering-and-timeouts.md`, which established that the graph's
execution order is deterministic and enumerated 87 numeric constants; that document is the
starting point and is not repeated here.

**Snapshot caveat.** Three other agents were editing `src/FrameLink.Agent/` while this was
written. Every line number below was read out of the working tree on 2026-08-24 and **lines may
have moved since**. Where a number matters, the surrounding identifier is named as well so the
citation survives a shift.

**Scope.** Everything that runs on a frame: `src/FrameLink.Agent` (the whole binary), `app/` (the
product page, which ships inside that binary per §2.1), the agent's own systemd unit, and the
processes on the frame that this product configures but does not run. Where I bounded the search,
§10 says so and names what was skipped.

---

## 0. The counts, before any analysis

| Thing | Count | Where the count is checked |
|---|---|---|
| Resources in the graph | **81** | `Assert.Equal(81, graph.Count)`, `tests/FrameLink.Tests/AgentResourceGraphTests.cs:213` |
| Entries in the catalog document | 80 | `Assert.Equal(80, document.Count)`, `AgentResourceGraphTests.cs:325` |
| Declaration sites carrying a non-empty `DependsOn` | 42 | counted in the ordering document, §5.1 |
| **Concurrent background loops the host starts** | **14** | the `running` list, `src/FrameLink.Agent/AgentHost.cs:683-724` |
| Subsystem directories under `src/FrameLink.Agent` | **15** | `Discovery Firmware Hosting Identity Kiosk Link Local Reconcile Resources Stage State Supervise Systemd Telemetry Update`, plus 4 root files (`AgentBuild`, `AgentHost`, `AgentJson`, `Program`) — **16 areas** |
| `.cs` files in the agent, excluding `bin`/`obj` | 115 | `find src/FrameLink.Agent -name '*.cs'` |
| **Items in this inventory** | **41** | §2 (14) + §3 (8) + §4 (7) + §5 (6) + §6 (6) |

### 0.1 Twelve versus fourteen — resolved, with the two commits that did it

`AgentHost.cs:674` says *"Twelve loops now"*. `AgentHost.cs:683` allocates `new List<Task>(13)`.
The list at `:685-724` contains **fourteen** tasks. All three numbers have a history and the
comment was true when it was written:

| Commit | Date | What it did to the count |
|---|---|---|
| `fe61154c` | 2026-08-16 | Added `reporter`, took the list from 10 → **12**, and wrote the comment *"Twelve loops now"*. **Correct at the time.** |
| `422d4288` | 2026-08-23 | Added `arrayFirmware.RunAsync` → **13**. Capacity and comment both left alone. |
| `99080802` | 2026-08-24 | Added `arrayFlash.RunAsync` → **14**. Capacity raised 12 → 13; comment left alone. |

So the drift is entirely the two firmware loops of the last two days, and neither of them brought
the comment or the capacity along. The capacity is now one short, which costs exactly one list
growth per process start and nothing else. **The comment is the thing to fix**: it is what a future
reader trusts, and it is now wrong by two in the direction that hides the two most recently added
loops — which are also the two with the widest blast radius (§7).

### 0.2 The three-way classification, stated once

Every item below carries exactly one of these.

- **(i) Cannot be a resource.** It is infrastructure the graph itself stands on — the link to the
  Fleet Manager, the process host, the status hub, a signal handler, a lock. Making it a resource
  would be circular or meaningless. **19 of the 41.**
- **(ii) Could be a resource; deliberately kept out, with a recorded reason.** The reason is quoted
  and its location given in every case. **15 of the 41** — twelve inside the agent binary
  (A4, A6, A9, A10, A11, A12, A14, B1, C1, C3, C5, D2) and three in the environment (E2, E3, E4).
- **(iii) Could be a resource; outside for no recorded reason at all.** This is the class the
  operator is hunting for. **7 of the 41** — A1, A7, A8, A13, B8, C4, C6 — gathered in §8.

19 + 15 + 7 = 41. Where a classification is mine rather than the code's, the item says so.

---

## 1. What "in the graph" buys, so the trade is legible

Restating from the ordering document, because every "what would change if it moved in" answer
below is one of these four:

1. **It can stop the pass.** One escalation stops all acting frame-wide until a human retries
   (`ReconcileLoop.HasStopped`, `Reconcile/ReconcileLoop.cs:233-234`). This is the property the
   operator wants for the microphone firmware.
2. **It is reported by name**, with a status from §2.3's vocabulary, an expected-vs-observed delta,
   and an attempt count — and it reaches the screen through `ReconcileVoice` and the Fleet Manager
   through the census.
3. **It can declare and be declared a dependency**, and `Blocked(dependency)` about it is a derived
   fact rather than a claim (`ReconcileLoop.Blocker`, `:1527-1538`).
4. **It gets the reboot-and-verify discipline** — §2.4's "applied is never claimed from a
   successful write" — and the attempt budget, the conflict counter, and the reboot floor.

And what it costs:

5. **Every Act reboots the frame.** A thing that must run continuously, or that cannot survive a
   reboot, cannot be applied by an Act — this is the recorded reason three items below are outside.
6. **An Act that cannot succeed spends three attempts, three reboots and an escalation**, and by
   decision 68 that escalation stops everything. This is decision 90's reasoning, and it is why two
   observers and the firmware flash are outside.
7. **One Act per pass, in graph order.** A thing that must react in under a second cannot be a
   resource; the pass sleeps 5 minutes between passes (`ReconcileOptions.PassInterval`).

---

## 2. The fourteen concurrent loops

Listed in the order `AgentHost.cs:685-724` starts them.

---

### A1 — Console stage

**What and where.** `ConsoleStage` (`src/FrameLink.Agent/Stage/ConsoleStage.cs:32`), constructed at
`AgentHost.cs:182`, run as `stage.RunAsync` (`AgentHost.cs:685`).

**What it does.** Paints §2.7's designed terminal interface onto `/dev/tty8` — the agent's own
virtual terminal, not the panel's default one. It is the whole screen from the first second of the
first boot until a compositor exists.

**How it is driven.** Two things: its own 120 ms animation tick (`ConsoleStage.TickInterval`,
`:130`), and a subscription to the status hub (`:94`) that repaints synchronously the instant
anything publishes.

**Blocking.** *It can block every publisher.* `AgentStatusHub.Publish` calls its subscribers
**synchronously on the publisher's thread** (`State/AgentStatusHub.cs`, the `foreach (var listener
in listeners)` after the lock is released), and this stage's subscriber writes to a character
device. A `/dev/tty8` write that blocks blocks whoever published — including the reconcile loop
mid-pass. The code knows this: `Firmware/ArrayFlashProgress.cs` (class remark, ~`:430-460`) names
it as the reason the flash progress pump runs on a task of its own. The pass cannot block the
stage, except by not publishing.

**Why it is outside — (iii). No recorded reason.** Nothing in the file, in `AgentHost` or in
`version2.md` says why the console stage is not a resource. There is a strong (i) argument — this
stage *is* the surface a failed resource is reported on, so a resource asserting "the console is
painting" would have nowhere to report its own failure — but **that argument is not written down
anywhere**, and "the terminal takes bytes" is a perfectly observable device fact with a clean delta.
Classified (iii) on the operator's own rule: could be a resource, no recorded reason. The (i) case
is mine and I would expect the operator to accept it — but it should be *recorded*, because the
permanent demotion in §7.2 is the failure it would have to answer for.

**If it moved in.** It would gain a name on the census and the ability to stop the pass when the
panel is dark. It would lose the thing that makes it work: it must run from before the first pass
and repaint continuously, so an Act that "starts the console" would find it down on every boot
(the same argument the local origin and the kiosk child have recorded, B1 and A14). What it would
break is the reporting of its own failure.

---

### A2 — Control link

**What and where.** `ControlLink` (`Link/ControlLink.cs:26`), run at `AgentHost.cs:686`. One
`ConnectionAttempt` per iteration (`Link/ConnectionAttempt.cs`), over
`WebSocketControlTransport`.

**What it does.** §4.1's reconnect loop to the Fleet Manager: capped exponential backoff, **retry
forever**. Carries the handshake, settings pushes, operator contact, retry requests, and the
telemetry uplink.

**How it is driven.** Its own loop. `Backoff.DefaultInitial` 1 s → `DefaultCap` 30 s with 20 %
jitter (`Link/Backoff.cs:24,27,46`), `HandshakeTimeout` 20 s (`ControlLink.cs:119`).

**Blocking.** Neither direction, by design. The reconcile loop runs from the first second whether
or not anything ever answers (`AgentHost.cs:674-677`); what adoption gates, it gates through the
DAG. A resource that needs an issued value reports `Unevaluable` and is rechecked every 30 s
without spending an attempt (`ReconcileLoop.cs:813-835`).

**Why it is outside — (i).** This is the operator's own named example. The graph reports *through*
it; `agent.adoption` is the resource that depends on what it learns.

**If it moved in.** It would gain nothing it does not already have (the adoption resource already
carries the reportable fact) and would break §1.2.2 outright: a link resource that could not
converge would block the frame from provisioning with the server unreachable, which is the exact
inversion `DeviceCatalog.cs:203-212` warns about for `agent.version`.

---

### A3 — Agent status reporter

**What and where.** `AgentStatusReporter` (`Link/AgentStatusReporter.cs:54`), run at
`AgentHost.cs:692`.

**What it does.** Turns the reconciler's own `loopState` into the §2.3 vocabulary sentence the
Fleet Manager classifies, and pushes it whenever it changes. Appends a firmware-write-in-flight
suffix (decision 91).

**How it is driven.** Its own loop, woken by a semaphore release on hub changes. Sends nothing
while nothing changes.

**Blocking.** *Neither direction, and this is load-bearing.* The class remark (`:46-52`) states it:
"a Fleet Manager that has stopped answering therefore leaves a socket blocked here and an array
being written to entirely undisturbed."

**Why it is outside — (i).** It is the reporting path itself.

**If it moved in.** Nothing to gain. It would be a resource whose observation is the state of every
other resource.

---

### A4 — Update service

**What and where.** `UpdateService` (`Update/UpdateService.cs:62`), run at `AgentHost.cs:693`.

**What it does.** §2.8's hourly out-of-band version convergence: fetch → verify SHA-256 → write
`fl-agent.new` → atomic rename → ask the process to restart. Matches the served version — upgrade
or downgrade, always.

**How it is driven.** Its own hourly loop (`DefaultInterval` 1 h, `:65`), plus `TriggerNow()` from
the handshake (`AgentHost.OnVerdictAsync:920-930`) and from the `agent.version` resource's Act
(`AgentHost.cs:450`).

**Blocking.** *Both directions, deliberately.* It restarts the process, which cancels the shutdown
token the reconcile loop shares — so an update ends the current pass. And it stands down entirely
while a firmware write is open (`StandDown = () => flashWindow.Reason`, `AgentHost.cs:262`), asked
twice: once at the top of a check and again immediately before the swap (`UpdateService.cs:130-134`).

**Why it is outside — (ii), with a recorded reason, and it is the subtlest one here.** The
*version* is a resource — `agent.version` is the root of the DAG (`Resources/AgentRootResources.cs:57`)
— and this loop is that resource's mechanism, wired in as `ConvergeVersion = updates.TriggerNow`
(`AgentHost.cs:450`). The recorded reason is §2.8's and it is restated at `AgentHost.cs:446-449`:
"the resource asks, the hourly loop does, and correctness never depends on the ask arriving." The
loop exists outside the graph because §2.8 makes the hourly tick the *mechanism* and the socket
merely an optimisation — a version that could only converge when a pass ran would not repair a
frame whose pass is stopped by an escalation.

**If it moved in.** It already is in, in the only sense that matters. Folding the loop itself into
the resource would mean the update only happens when a pass acts on it — and a stopped pass
(decision 68) would then also stop the frame from ever receiving the fix that unstops it.

---

### A5 — The reconcile loop itself

**What and where.** `ReconcileLoop.RunAsync` (`Reconcile/ReconcileLoop.cs:460`), run at
`AgentHost.cs:694`.

**What it does.** Walks the 81 resources in graph order, one Act per pass, reboot and verify.

**How it is driven.** Its own loop, `PassInterval` 5 min when nothing is pending
(`ReconcileOptions.cs:208`).

**Blocking.** It is the pass.

**Why it is outside — (i).** It is the graph's driver.

---

### A6 — Supervisor

**What and where.** `Supervisor` (`Supervise/Supervisor.cs`, class at ~`:89`), run at
`AgentHost.cs:695`.

**What it does.** §2.10's five supervised behaviours: memory watchdog, daily 03:00 restart, kiosk
liveness, camera recycle on call-end, page refresh.

**How it is driven.** One loop on `supervision.kioskCheckInterval` (15 s, `Supervisor.cs:355`),
with per-behaviour intervals inside it.

**Blocking.** *Both directions, and this is the one pair that is fully designed.* The
`SupervisionInterlock` (D1) means supervision does not act on anything the reconciler is
`Progressing`/`AwaitingReboot`/`Blocked` on, and a supervision window makes the transient wrongness
it causes not-drift. `ReconcileLoop.cs:854-874` consults it mid-walk. The window's expiry
(`supervision.recoveryDeadline`, 2 min) hands the condition back to the reconciler as ordinary
drift.

**Why it is outside — (ii), and it carries the longest recorded reason in the repository.**
version2.md §2.10: *"Supervision does not stop the product, and that is the whole reason it is not
a resource… Modelling supervision as drift would force the two rules into collision and one of them
would have to yield: either drift stops being absolute (correctness lost) or a routine browser blink
blanks the frame, kills the call and shows a repair screen every morning at 03:00 (continuity
lost)."* Restated in code at `Supervisor.cs:93-95`: *"Not resources, and the reason is a collision
rather than a taxonomy."*

**If it moved in.** It would gain the ability to stop the pass — which is precisely what §2.10 says
it must not have, because a frame that needs restarting to stay alive and then stops is a dark
frame. It would lose the rate-based fault signal, which is deliberately diagnostic rather than
inhibitory.

---

### A7 — Browser stage loop

**What and where.** `AgentHost.BrowserStageLoopAsync` (`AgentHost.cs:817`) driving
`BrowserStage.TickAsync` (`Stage/BrowserStage.cs:312`), run at `AgentHost.cs:696`.

**What it does.** §2.7's fallback rule. Starts the GUI, requires the page to check in within 60 s
(`BrowserStage.cs:227-228`), and if it does not, **tears the graphical session down** — `systemctl
--user stop chromium-kiosk` and `systemctl stop getty@tty1.service` (`:460,464`) — narrates on the
console, waits 2 min, and starts the getty again (`:511`). Unbounded retries.

**How it is driven.** Its own 5 s loop (`AgentHost.cs:839`).

**Blocking.** *It acts on things the graph owns.* It opens an interlock window over four resources
by name — `unit.chromium-kiosk.running-matches-content`, `session.bash-profile-exec-labwc`,
`display.dsi2-transform`, `boot.autologin.getty-tty1` (`BrowserStage.SessionResources`, `:189-195`)
— because without them the reconciler read the teardown's own act as drift and rebooted for it
(`:179-186`, a measured defect). It also runs `systemctl` through the untimed process runner, so a
hung `systemctl` freezes the loop whose whole job is to prevent a blank screen (§7).

**Why it is outside — (iii). No recorded reason.** The file explains at length why the handover is
a switch rather than a reveal, why the getty must be stopped, and why the stage owns its own
arming. It never says why "the page renders" is not a resource. It is a level-triggered
observe-compare-act cycle on system units with a named delta and a named set of affected resources
— which is a resource in everything but registration.

**If it moved in.** It would gain: the ability to stop the pass when the panel will not render at
all (today it retries for ever and nothing escalates); a row on the census; and a real
`Blocked(dependency)` on the kiosk stack. It would lose: the 5 s reaction time (a pass runs every
5 min), and it would collide with `unit.chromium-kiosk.*`, which already own the same units — the
teardown would become an Act that makes four other resources wrong, which is exactly what the
interlock currently launders. **This is the item most similar in shape to the microphone firmware
decision.**

---

### A8 — Screen handover

**What and where.** `ScreenHandover` (`Stage/ScreenHandover.cs:58`), run at `AgentHost.cs:702`,
plus a one-shot `ReconcileAsync` before any loop starts (`AgentHost.cs:219`, item C4).

**What it does.** Decides which of the two virtual terminals the panel is showing, level-triggered
on compositor presence: the console keeps the panel while no `labwc` is running and hands it back
the moment one is.

**How it is driven.** Its own 2 s loop (`PollInterval`, `:123`), each tick a `pgrep -x labwc`
(`:292`). It **returns permanently** when `!Switchable` (`:243-250`) — every run off a real frame.

**Blocking.** *One direction, into the graph.* Its class remark (`:42-49`) records that a handover
which kept the panel while labwc ran would make `display.dsi2-transform` fail on every boot and
turn that into a reboot loop. So this loop's behaviour is a precondition of a graph resource
converging. A hung `pgrep` freezes it and the panel stops switching (§7).

**Why it is outside — (iii). No recorded reason, and the code says out loud that it is doing
reconciliation.** `AgentHost.cs:698-701`: *"which one the panel shows is state that has to be
reconciled like everything else (§2.2) rather than set once and trusted."* And `:51-56` of the
class: *"Level-triggered, like everything else here (§2.2)."* It declares itself a reconciler and
is not in the reconciler. Nothing anywhere records why.

**If it moved in.** It would gain a name, a delta ("the panel is showing tty1 and should be showing
tty8"), and the ability to stop the pass when the panel is stuck on the wrong terminal — which is
today entirely silent. It would lose the 2 s cadence, which matters for exactly one moment: the
instant a compositor dies, where a 5-minute wait would leave a blank panel. It would also need an
answer for the permanent early return.

---

### A9 — Package inventory reporter

**What and where.** `PackageInventoryReporter` (`Telemetry/PackageInventoryReporter.cs:53`),
constructed at `AgentHost.cs:280`, run at `:703`.

**What it does.** Reads dpkg's whole database (~930 packages) and reports it, but only when a
content hash says it changed.

**How it is driven.** Once at startup, then every `packages.reportInterval` (6 h default, `:69`).

**Blocking.** Neither direction — except that it runs `dpkg-query` through the untimed runner
(§7). Its output buffers on disk and drains on reconnect.

**Why it is outside — (ii), recorded, verbatim** (`:13-20`): *"§2.2's unit is 'the smallest
independently verifiable setting', and the ~930 packages on a frame are not a setting at all —
nothing declares them, nothing converges them, and a resource that asserted the closure would report
drift every time Debian re-cut a dependency. What the operator asked for is visibility, so this
observes and reports and never acts."*

**If it moved in.** It would gain nothing but the ability to stop the frame every time Debian
re-cuts a dependency. The fifteen packages that *are* declared already have resources.

---

### A10 — Array firmware reporter

**What and where.** `ArrayFirmwareReporter` (`Telemetry/ArrayFirmwareReporter.cs:60`), constructed
at `AgentHost.cs:305`, run at `:704`. Added 2026-08-23 in `422d4288`.

**What it does.** Reads which firmware the microphone array is running — twice, once from
`bcdDevice` in sysfs and once from `xvf_host VERSION` — and reports both plus whether they agree.
It never writes one.

**How it is driven.** Once at startup, then every 6 h (`DefaultInterval`, `:74`). No fleet setting,
deliberately.

**Blocking.** ***Yes, into the graph, and this is the sharpest coupling in the inventory.*** Its
`xvf_host` call takes `XvfHost.Conversation`, a `static SemaphoreSlim(1,1)` shared process-wide
with the audio block inside the graph (`Resources/AudioArrayResources.cs:670`). The remark there
(`:664-669`) states the wait is unbounded on purpose and why: *"a hung tool wedges the caller today,
with or without this gate."* So a `xvf_host` invocation from **this loop** that never returns holds
the semaphore and stalls the reconcile pass's audio resources indefinitely, with nothing on the
screen changing.

**Why it is outside — (ii), recorded, verbatim** (`:14-24`, decision 90): *"The array's firmware
version fails the second half of [Observe → Compare → Act → Verify] and cannot be made to pass it:
the only Act that could converge it is a DFU write, the operator has decided this product will never
perform one unattended, and a resource whose Act cannot succeed is exactly what decision 63
diagnosed."* And the cost of the alternative is written out at `:26-33`.

**If it moved in.** Exactly the failure decision 90 describes: three attempts, three reboots, an
escalation, and by decision 68 a frame carrying a factory array would never converge its screen,
its camera or its speaker. **This is the reasoning the operator is now revisiting for the flash
(A11); note that it applies to the *reporter* only insofar as the reporter has no Act. If a flash
step became a resource, the reporter's observation would become that resource's Observe and this
item would disappear into it.**

---

### A11 — Array firmware flash and its consent screen

**What and where.** `ArrayFirmwareFlash` (`Firmware/ArrayFirmwareFlash.cs`, class at ~`:265`),
constructed at `AgentHost.cs:651`, run at `:709`. Added 2026-08-24 in `99080802`. Four collaborators,
all outside the graph: `ArrayFlashApproval` (`Firmware/ArrayFlashApproval.cs:516`) owns the
full-screen consent prompt; `ArrayFlashWindow` (`Firmware/ArrayFlashWindow.cs:59`) owns the durable
in-progress marker; `ArrayFlashProgressPump` (`Firmware/ArrayFlashProgress.cs`, item B3) owns the
progress screen; `ArrayHardwareGate` (`Firmware/ArrayHardwareGate.cs:666`) is a nine-rung refusal
ladder read before anything is written.

**What it does.** The one code path in the product that can write firmware to the microphone array.
Reads a per-device authorisation naming an image **digest**, spends it atomically before starting,
runs the hardware gate, asks the person at the frame for a five-second hold, runs `dfu-util`, then
verifies by polling the USB bus for re-enumeration.

**How it is driven.** Its own loop, one tick a minute (`DefaultInterval`, `:319`), dropping to 5 s
while one of its screens is up (`PromptInterval`, `:327`).

**Blocking.** *Both directions, comprehensively.* It refuses the reconciler's reboot through
`RebootHold` (`AgentHost.cs:468-471`) and refuses the update service's restart through
`StandDown` (`AgentHost.cs:262`) — so a write in progress can hold up the pass. It takes
`XvfHost.Conversation` like A10. And it stands down itself when a call is active or a restart is
pending (`CallActive`, `RestartPending`, `AgentHost.cs:668-669`).

**Why it is outside — (ii), recorded at length** (`ArrayFirmwareFlash.cs:204-215`): *"A resource's
Act must be able to succeed. A firmware-version resource's Act cannot: on a frame nobody has
authorised, it would drift, spend three attempts and three reboots, escalate, and by decision 68
stop the whole pass — so a frame carrying a factory array would never converge its screen, its
camera or its speaker over a number nobody had agreed to write."* Note what the resource
`firmware.xvf3800.image` (`Resources/XvfFirmwareImageResource.cs:64`) does carry: the pinned image
files on the SD card. The images converge in the graph; the write does not.

**If it moved in — this is the operator's live question, so stated fully.** It would gain: the
ability to stop the pass and put a full-screen message in front of the household (which is exactly
what is wanted); named rows for each step; and real `dependsOn` edges — the hardware gate's nine
rungs are already a dependency chain in all but name, and `pkg.dfu-util`, `tool.xvf-host.installed`
and `firmware.xvf3800.image` are already resources it needs. What breaks, and each needs an answer:
1. **§2.4 says every resource reboots.** A DFU write must not be crossed by a reboot; today
   `RebootHold` refuses one from outside. As a resource, the Act *is* followed by a reboot.
2. **§2.5 says three attempts.** The flash is single-use by construction — the authorisation is
   spent before `dfu-util` starts (`:220-229`) — so a retrying ladder is the opposite of the
   design. A retry would need to be a no-op that reports rather than re-writes.
3. **The Act cannot succeed unattended**, which is decision 90's whole argument. A "not authorised"
   state would need to be `InSync`-with-a-note or `Unevaluable`, not drift — otherwise every
   unauthorised frame in the fleet escalates and stops.
4. **The consent screen owns the panel** for 30 min at a time; a resource cannot hold a screen
   across passes without a concept the loop does not have.
   These four are answerable — decision 91's interlocks all become graph concepts — but they are
   the whole of the work, and none of them is a reason not to.

---

### A12 — Call button watch

**What and where.** `ButtonWatch` (`Local/ButtonWatch.cs:74`), constructed at `AgentHost.cs:369`,
run at `:710`. Holds a `gpiomon` child (`Hosting/IGpioLines.cs:150-166`, item B4).

**What it does.** Guide 11's GPIO daemon, inside the agent. Holds the claim on BCM 17 for the life
of the process and turns a press into a `toggle` command on the local channel.

**How it is driven.** Its own loop, one long-lived `WatchAsync` per claim, `RetryDelay` 30 s after
a lost claim (`:100`), `Debounce` 50 ms (`:92`).

**Blocking.** Neither direction. The reconciler's `gpio.button.line` resource can ask it to re-arm
(`ButtonWatch.ReArm`, `:230`), which is that resource's Act — so the graph drives it, not the
reverse.

**Why it is outside — (ii), recorded, and it is the cleanest split in the inventory.** The *claim*
is the resource (`gpio.button.line`, `Resources/GpioButtonResources.cs:328`); the *daemon* is the
thing being asserted. `AgentHost.cs:373-379`: *"It holds the GPIO line for as long as the agent runs
… Created before the catalog because `gpio.button.line` observes this claim."* A resource cannot
hold a line, because §2.4 reboots after every Act.

**If it moved in.** Nothing to gain; the observable half is already a resource with a delta and an
Act.

---

### A13 — Touch retry reader

**What and where.** `TouchRetry` (`Local/TouchRetry.cs`, class at ~`:67`), constructed at
`AgentHost.cs:507`, run at `:716`. Reads the panel's evdev node directly via `EvdevTouchInput`
(`Hosting/ITouchInput.cs`).

**What it does.** §2.7 item 9's console half: a three-second hold anywhere on the panel resets
every exhausted budget, through the same `ResetExhaustedBudgets` the Fleet Manager's retry uses.
Since decision 91 it also carries the firmware screen's five-second approval hold, which outranks
the retry whenever one is up (`TouchRetryServices.Ask`, `:64`).

**How it is driven.** Its own loop: 50 ms poll while a device is open, 30 s rediscovery otherwise
(`:109,118`).

**Blocking.** *One direction, into the graph — and it is the only way a stopped pass ever restarts
without a Fleet Manager.* A completed hold calls `loop.ResetExhaustedBudgets()`
(`AgentHost.cs:494-503`). The pass cannot block it.

**Why it is outside — (iii). No recorded reason.** The file explains why a hold rather than a
button, and why a hold rather than a tap, at length. It never says why "the panel's touchscreen is
readable" is not a resource — and it is a genuinely observable device fact with a clean delta
(`/dev/input/eventN` exists, opens, and reports `BTN_TOUCH`). Classification is mine; the case for
it being (i) is that it is the input path a stopped frame needs, so a resource that stopped it would
remove the only local recovery.

**If it moved in.** It would gain a reportable answer to *"is there a touchscreen on this frame at
all?"* — which today is visible only as a screen that says so (§2.7 item 9's last sentence). It
would lose nothing structural, but a *failed* touch resource would stop the pass and thereby remove
the affordance that unstops it, which is a genuine circularity and probably the reason nobody wrote
one.

---

### A14 — Immich Kiosk child process

**What and where.** `KioskProcess` (`Kiosk/KioskProcess.cs:228`), constructed at `AgentHost.cs:391`,
run at `:724`.

**What it does.** Runs the pinned upstream Immich Kiosk binary as a direct child of the agent —
guide 9's `restart: always` without Docker underneath it — and relaunches it when it exits.

**How it is driven.** Its own loop, relaunch check every 5 s (`:689`), `StopGrace` 5 s (`:254`),
output budget 60 lines / 10 min (`Supervise/ChildOutputBudget.cs:72,80`).

**Blocking.** *Both directions, through the same interlock the supervisor uses.* A relaunch opens a
window over the resources it disturbs so the reconciler does not read a blink as drift; the window's
expiry (`RecoveryDeadline`, 2 min, `:686`) hands it back as ordinary drift.

**Why it is outside — (ii), recorded, and it names its own sibling.** `KioskProcess.cs:224-231`:
*"Started from the host, not only from its resource, for the same reason `LocalOrigin` is: the child
is this process's child, so it cannot survive the reboot every resource takes (§2.4). If starting it
were left to the Act, the resource would find it down on every boot, act, reboot and find it down
again — a loop that never converges."* And `:205-219` records why it is deliberately **not** a fifth
supervised behaviour either.

**If it moved in.** The paired resource `kiosk.process.supervised`
(`Resources/KioskResources.cs:661`) already carries everything reportable. Moving the *starting*
in produces the non-converging loop the remark describes.

---

## 3. Started by the process, but not in that list of fourteen

Eight more things run. None of them appears in `running`, so none of them is covered by
`WhenAllOrFirstFaultAsync` (`AgentHost.cs:779`) — a fault in any of these does not bring the agent
down and does not reach the journal through that path.

---

### B1 — The local HTTP server and its accept loop

**What and where.** `LocalOrigin` (`Local/LocalOrigin.cs:40`), constructed at `AgentHost.cs:319`,
started at `:332`. The accept loop is a discarded `Task.Run` (`LocalOrigin.cs:160`); each connection
is another discarded `Task.Run` (`:254`).

**What it does.** Serves the embedded app, the repair screen, `/config.json` and the `/local`
WebSocket channel on `127.0.0.1:8888`. Hand-written HTTP, ~100 lines, because §2.1 forbids linking
a web framework into a 1.35 MB AOT binary.

**How it is driven.** One-shot start; then event-driven per connection. WebSocket keepalive 20 s
(`:368`).

**Blocking.** Neither direction directly. But it is the *transport* for the browser stage's
check-in, the supervisor's liveness signal, the reboot-now button, the retry button and the
firmware answer — so a server that is not answering silently disables five separate behaviours.

**Why it is outside — (ii), recorded** (`AgentHost.cs:325-331`): *"Started here and not only by its
resource, because the server lives in this process and therefore cannot survive the reboot every
resource takes (§2.4)… Started at every process start, the resource becomes what it should be: an
assertion that the origin is answering, with an Act that retries the bind for the one case that can
actually fail, a port somebody else holds."* The paired resource is `app.http.local-origin`
(`Resources/AppResources.cs:41`).

**If it moved in.** Already in, in the reportable half. Moving the start in reproduces the
never-converging loop.

---

### B2 — The local channel and its four event handlers

**What and where.** `LocalChannel` (`Local/LocalChannel.cs`), constructed at `AgentHost.cs:318`.
Four handlers wired in the host: `RebootRequested → countdown.SkipNow` (`:346`),
`RetryRequested → retryFromFrame` (`:357`), `ArrayFlashAnswered → flashApproval.Answer` (`:365`),
and the page's `alive`/`call-ended` messages read by the supervisor.

**What it does.** Carries the page's liveness heartbeat, its configuration self-report, and four
operator gestures, in both directions.

**How it is driven.** Purely event-driven, on the WebSocket receive path.

**Blocking.** *Into the graph, three times:* the reboot-now press shortens a countdown mid-Act, the
retry press resets budgets under a running walk, and the firmware answer approves a write that then
holds the reboot boundary. The remark at `AgentHost.cs:576-580` notes the retry "runs on the receive
loop and touches nothing but the journal … the worst case is one extra pass before it takes effect."

**Why it is outside — (i).** It is a transport. `LocalChannel.cs:6-11` states it is deliberately not
a second control protocol.

---

### B3 — Array flash progress pump

**What and where.** `ArrayFlashProgressPump.Start` (`Firmware/ArrayFlashProgress.cs:441`), which
launches a discarded `Task.Run` at `:454`.

**What it does.** Repaints the firmware write's progress screen — percent, bytes, elapsed seconds —
once a second (`DefaultBeat`, `:354`) so a still bar reads as a wait rather than a hang.

**How it is driven.** Started by the flash, on its own task, for the duration of one write.

**Blocking.** *Deliberately isolated in one direction.* The class remark (`:423-440`) records four
structural properties that keep a hung hub, a dead network or a screen that will not take a repaint
from ever reaching the write — including that the writing thread never calls `AgentStatusHub.Publish`
at all, because publish runs subscribers on the caller's thread.

**Why it is outside — (i).** It is a rendering detail of A11.

---

### B4 — The `gpiomon` child process

**What and where.** `GpioMonLines` (`Hosting/IGpioLines.cs`), `Process.Start` at `:166`, killed with
`entireProcessTree: true` at `:237`. `ClaimGrace` 2 s (`:86`).

**What it does.** Holds the kernel GPIO line claim on behalf of A12 for as long as the claim lasts;
one line of its stdout per edge is a button press.

**How it is driven.** Started per claim attempt; long-lived.

**Blocking.** Neither. It is the one long-lived child besides Immich Kiosk.

**Why it is outside — (i).** It is A12's implementation. The claim it produces is the resource.

---

### B5 — SIGUSR1 simulated button press

**What and where.** `AgentHost.SimulatedPress` (`:865`), registered at `:383`.

**What it does.** Guide 11 step 4's test affordance: `systemctl kill -s SIGUSR1 fl-agent.service`
runs the same broadcast a real press does.

**How it is driven.** A POSIX signal. Fires `button.SimulateAsync` on a discarded task.

**Blocking.** Neither.

**Why it is outside — (i).** A signal handler cannot be a resource. Note the failure mode is
recorded and benign: on a platform where it cannot be registered the agent "says so once and carries
on, because a missing test affordance must never be a reason for a frame not to start"
(`:859-862`).

---

### B6 — SIGTERM and Ctrl+C handlers

**What and where.** `Program.CreateSignalHandler` (`Program.cs:88`) and `Console.CancelKeyPress`
(`:27`).

**What it does.** Cancels the one shutdown token every loop shares.

**How it is driven.** Signals.

**Blocking.** They end everything, which is correct.

**Why it is outside — (i).** Process host.

---

### B7 — The hub subscription that persists the last authoritative answer

**What and where.** `AgentHost.cs:163-170`, `hub.Subscribe(...)` → `memory.RememberAnswer`.

**What it does.** Every time the hub publishes a `LastAuthoritative`, it is written to
`AgentMemory` so §2.6's "a frame that was fully green when contact dropped carries on" survives a
reboot.

**How it is driven.** Synchronously, inside every `Publish` — i.e. on the reconcile loop's thread
whenever the loop publishes.

**Blocking.** *It performs a file write on the publisher's thread.* Same shape as A1's console
write. Small, but it is a disk I/O on the hot path of every status change.

**Why it is outside — (i).** State persistence; there is nothing here a resource could assert that
`agent.adoption` does not already assert.

---

### B8 — The console stage's repaint subscription

**What and where.** `ConsoleStage.cs:94`.

Covered under A1 and carries A1's classification — **(iii)** — because it is the same unrecorded
decision. Listed separately because it is a *second* thing the process starts (a subscription, not a
loop) and because it is the specific mechanism by which a terminal write lands on the reconcile
loop's thread.

---

## 4. One-shots before any loop, and the second verb

Seven things happen before the fourteen loops start, in this order. Everything in this group runs
exactly once per process and none of it is reachable by the graph.

---

### C1 — Device keypair load-or-create

**What and where.** `DeviceKeyStore.LoadOrCreate` (`Identity/DeviceKeyStore.cs`), called at
`AgentHost.cs:123`.

**What it does.** Loads the device's permanent Ed25519 identity from `/var/lib/fl-agent`, or
generates it on first boot.

**How it is driven.** One-shot, before anything else.

**Blocking.** ***It can end the process before a single loop starts.*** A key that cannot be read
returns `ExitCodes.Unrecoverable` (`:129-134`) with two log lines and **nothing on the screen** —
the console stage is not constructed until `:182`. See §7.

**Why it is outside — (ii)/(i) split, recorded.** The *observable* half is the resource
`agent.keypair` (`Resources/AgentRootResources.cs:168`), which compares against what the process is
running as. The *generation* cannot be a resource: identity is permanent (§3.3), and
`AgentHost.cs:131-132` records the refusal — *"Refusing to generate a new identity — that would
silently orphan this frame from its Fleet Manager."*

**If it moved in.** No gain. A resource that regenerated a key would be the failure the refusal
exists to prevent.

---

### C2 — Journal read, memory read, and the resumed condition

**What and where.** `ReconcileJournal` and `AgentMemory` constructed at `AgentHost.cs:139-141`;
`memory.ResumeCondition(journal.Read().FirstInSyncUtc is not null)` at `:141`.

**What it does.** Decides what the frame believed about itself before this process started: the
last authoritative Fleet Manager answer, the settings map, the device name, the operator contact,
and whether this frame has *ever* been `InSync` (which decides countdown versus provisioning pace).

**How it is driven.** One-shot.

**Blocking.** Neither. But it seeds the hub before the first paint, which is why a power cut does
not spend its first half-minute showing a repair screen (`:148-151`).

**Why it is outside — (i).** It is the graph's memory.

---

### C3 — The interrupted-flash latch

**What and where.** `ArrayFlashWindow` constructed at `AgentHost.cs:228`; `Interrupted` checked at
`:236-243`.

**What it does.** Reads a durable marker exactly once, at construction. A marker still present at a
new process start means a flash began and the process that began it did not live to finish it. It
latches, is reported loudly, and **is cleared only by a person deleting the file**.

**How it is driven.** One-shot at construction, deliberately before anything could have written one.

**Blocking.** *Into everything.* While `flashWindow.Reason` is non-null the reboot boundary refuses
(`RebootHold`, `AgentHost.cs:468-471`) and the update service stands down (`:262`).

**Why it is outside — (ii), recorded** (`ArrayFlashWindow.cs:39-49`): the marker is *"durable and
deliberately not self-clearing … An agent that cleared it itself would be free to start a second
flash onto an array whose Upgrade partition is in an unknown state."*

**If it moved in.** A resource whose only remedy is a human deleting a file is `Escalated` on
arrival — which is arguably exactly right and would give the operator a census row for a frame in
this state. Today the only surfaces are a log line and a firmware screen.

---

### C4 — The startup screen handover

**What and where.** `await screen.ReconcileAsync(shutdown.Token)` at `AgentHost.cs:219`, before
endpoint resolution.

**What it does.** Puts the panel on the right terminal once, before anything slow happens, so the
first paint is on a screen somebody can see. `AgentHost.cs:213-217` records why it is *reconciled*
rather than *taken*: grabbing the panel on every agent restart "because a service restarted would be
a fault of its own."

**Why it is outside — (iii), same as A8.**

---

### C5 — Endpoint resolution

**What and where.** `AgentHost.ResolveEndpointsAsync` (`:887`), called at `:221`. Three sources in
priority order: `InstallFlagEndpointSource`, `BootFileEndpointSource`, `MdnsEndpointSource`
(`Discovery/`).

**What it does.** Works out which Fleet Manager this frame belongs to, once, and publishes it. mDNS
listens for up to 2 s (`Discovery/MdnsEndpointSource.cs:53`).

**Blocking.** *It delays the console's first useful paint by up to ~2 s.* The code knows and works
around it — `stage.Paint()` is called at `:208`, before this, precisely so the panel is not blank
during the mDNS window.

**Why it is outside — (ii), by reference.** §4.3's "never rediscover": the answer is persisted and
authoritative. `Program.cs:52-57` records the same rule for the install path.

---

### C6 — The `install` verb, and the agent's own systemd unit

**What and where.** `Program.InstallAsync` (`Program.cs:60`) → `UnitInstaller.InstallAsync`
(`Systemd/UnitInstaller.cs:36`). The unit text is an embedded resource
(`Systemd/fl-agent.service`).

**What it does.** Writes `/etc/systemd/system/fl-agent.service`, runs `daemon-reload`, and
`systemctl enable --now`. Idempotent by construction.

**How it is driven.** A human running `fl-agent install --control-url=…`, once, ever.

**Blocking.** It is the thing that starts the agent.

**Why it is outside — (iii). No recorded reason, and this is the largest silent gap in the
inventory.** The catalog has resources for `unit.chromium-kiosk.content`,
`unit.cpu-performance.content`, `unit.framelink-camera.content` and `unit.xdg-desktop-portal.dropin-desktop`
— every systemd unit the agent installs is reconciled **except its own**. The unit carries
load-bearing, easy-to-break settings that the file itself documents at length: `StartLimitIntervalSec`
must be under `[Unit]` or it is silently ignored (a trap the mule hit on 2026-08-15, unit lines
36-58), `KillMode` must stay at its default, `TTYPath=/dev/tty8` is what makes systemd export `$TERM`
to the console stage, and `User=root`. **Nothing on the frame checks any of it after install.** An
update replaces the binary (§2.8) and never the unit, so a unit written by an older `install` run
persists for the life of the card.

**If it moved in.** It would gain: drift detection on the one file whose corruption makes every
other honesty mechanism unavailable, and — critically — a check that the running unit matches the
unit this binary carries, which is the same shape as `unit.chromium-kiosk.running-matches-content`.
What breaks: the Act would have to `daemon-reload` and could restart the agent mid-pass, so it needs
the same stand-down the update service has; and §2.4's reboot after the Act would land on a frame
whose supervising unit had just changed. Both are tractable. **I would put this at the top of the
operator's review list.**

---

### C7 — The `version` verb

`Program.cs:41-43`. Prints and exits. Listed for completeness; nothing else.

---

## 5. Shared machinery that gates the pass without being in it

Six objects are not loops and not resources, but sit between the things that are, and every one of
them can change what a pass does.

---

### D1 — Supervision interlock

`Supervise/SupervisionInterlock.cs:49`, constructed at `AgentHost.cs:336`, shared by the supervisor,
the browser stage and the kiosk child. Holds the reconciler's locks and supervision's windows.
Consumed mid-walk at `ReconcileLoop.cs:854-874`.

**Blocking: both directions, by design.** **(i)** — it is the mechanism §2.10 clause 1–3 describes.

---

### D2 — The reboot boundary stack

`RebootHold` → `RebootFloor` → `SystemRebootBoundary`, all in `Reconcile/IRebootBoundary.cs`,
assembled at `AgentHost.cs:468-478`.

Three layers, outermost first: a firmware write refuses the reboot; then decision 79's floor refuses
past 120 reboots in a rolling 6 h; then the real `systemctl reboot`.

**Blocking: it is the only thing that can refuse a resource's reboot.** A refusal is an ordinary
outcome — the change is written, cannot be proven, spends an attempt and reaches a person.

**Why outside — (ii), recorded** (`IRebootBoundary.cs:285`): the floor *"is deliberately not a
resource, and can therefore never be the thing that triggers a"* reboot. Its own argument is in
§2.4: the ladder counts failures, and the cycle this bounds is made of successes.

---

### D3 — `XvfHost.Conversation`, the process-wide array semaphore

`Resources/AudioArrayResources.cs:670`, a `static SemaphoreSlim(1,1)`.

**What it does.** Serialises every `xvf_host` invocation in the process — one from the audio block
inside the graph, one from A10 outside it, one from A11 outside it — because the tool has no device
selector and a second overlapping invocation loses the USB claim and reads as *"the array did not
answer"*, which is drift, which costs an attempt and a reboot.

**Blocking: outside-the-graph → inside-the-graph, unbounded.** The remark (`:664-669`) says the
unbounded wait is on purpose, on the grounds that `HostProcessRunner` already awaits with no timeout
anywhere, *"so a hung tool wedges the caller today, with or without this gate."* That is true and it
is also the thing to fix (§7).

**Why outside — (i) as a lock, but its existence is evidence for the operator's suspicion.** A
process-wide static shared between the graph and two loops beside it is exactly the coupling that
becomes invisible when the two live in different places.

---

### D4 — Telemetry outbox and uplink

`Telemetry/TelemetryOutbox.cs:41` and `Link/AgentUplink.cs`, constructed at `AgentHost.cs:270-271`.
Bounded disk ring for events (500), single-slot for reports and the package inventory. Drains on
handshake (`AgentHost.OnVerdictAsync`, `:958`).

**Blocking: neither.** **(i)** — reporting path.

---

### D5 — The status hub

`State/AgentStatusHub.cs`, constructed at `AgentHost.cs:143`. Nine distinct publishers, of which
**eight are outside the graph**: the host itself, `ArrayFlashApproval`, `ArrayFlashProgress`,
`ConnectionAttempt`/`ControlLink`, `TouchRetry`, `BrowserStage`, `ConsoleStage`, `Supervisor`,
`UpdateService` — plus `ReconcileLoop`.

**Blocking: publish is synchronous on the publisher's thread.** See A1 and §7.

**(i)** — it is the graph's own reporting surface.

---

### D6 — The reboot countdown

`Reconcile/RebootCountdown.cs`, constructed at `AgentHost.cs:345`, wired to the local channel's
reboot-now press. Repaints every 200 ms (`:33`).

**(i)** — part of the loop's own reboot path.

---

## 6. On the frame, outside the agent binary

Six things run on a frame that the agent does not run. This is where I bounded the search; §10 says
what that excludes.

---

### E1 — The embedded product app

`app/frame-app.js` (401 lines), `app/frame-stage.js` (443), `app/livekit.js` (210), `frame-grid.js`,
`frame-tile.js`, `index.html`, `vendor/`. Ships inside the binary (`Local/EmbeddedApp.cs`), served
by B1.

**What it does.** Renders the slideshow iframe and the call UI; sends the liveness heartbeat every
15 s; renders the agent's repair narration and the firmware screens; reports its own document age
so `PageFreshness` can decide staleness.

**Driven by:** `requestAnimationFrame` and its own WebSocket, with eleven timing constants of its
own (§9).

**Blocking:** *it holds a deciding vote the agent does not.* The page refuses a reload it is in a
call for and takes it when the call ends (§2.10) — the agent's copy of "a call is in progress" is up
to one heartbeat old.

**(i)** — it is the product.

---

### E2 — `chromium-kiosk.service` and `framelink-camera.service`

systemd **user** units with their own `Restart=always`. Their *content* and *enablement* are
resources; their *running* is supervised (A6) and their teardown is driven by A7. Nothing owns their
lifetime except systemd. **(ii)** — recorded at `KioskProcess.cs:205-219`, contrasting them with the
kiosk child.

---

### E3 — `getty@tty1`

Deliberately left running. `version2.md` §2.7: *"That getty is left untouched, so the physical login
§5.5 depends on is still there while the agent narrates."* It repaints its login prompt over
whatever shares its terminal, which is the entire reason A8 exists. A7 stops it during a teardown
and starts it again after the cool-off. **(ii)** — recorded, and the unit file records the
`Conflicts=getty@tty1.service` that was considered and rejected (`fl-agent.service`, ~`:115`).

---

### E4 — `unattended-upgrades` and the `apt-daily` timers

Configured by two resources (`apt.auto-upgrades-enabled`, `apt.unattended-upgrades.allowed-origins`)
and driven by systemd timers the agent does not touch. **This is the only thing on a converged frame
that moves a package**, which is the sizing argument for A9's 6-hour cadence
(`PackageInventoryReporter.cs:62-68`). **(ii)** — recorded there.

---

### E5 — WirePlumber and PipeWire

Run inside the login session. Their *configuration* is resources (`wireplumber.conf.camera-monitors-disabled`,
`audio.wireplumber.playback-volume`); their *behaviour* is a second owner of the mixer, which is why
`MediaGraphGate` (`Resources/MediaGraphGate.cs`) exists inside the graph to stop a pass reading a
graph WirePlumber has not built yet. **(i)** — OS components.

---

### E6 — The workstation bench harness

`tools/harness`. Decision 91 names its power-cut refusal as *"the one interlock that lives on the
workstation"* (`ArrayFlashWindow.cs:31-37`), reading the durable marker C3 writes. It is outside the
frame entirely and therefore outside the graph by construction. **(i)**, noted so the operator's
list of firmware interlocks is complete: one of the six is not on the frame.

---

## 7. Which of these can leave a frame with nothing on the screen changing

The ordering document found one. There are **six**, and the original is worse than it looked.

### 7.1 The untimed process runner — and its blast radius is seven loops, not one

`IProcessRunner.RunAsync` has no timeout (`Hosting/IProcessRunner.cs:55-101`); it awaits
`WaitForExitAsync` with only the shutdown token. The ordering document recorded that this hangs the
reconcile pass. It also hangs, independently:

| Loop | The call | What freezes |
|---|---|---|
| A6 Supervisor | `ps -eo rss=,comm=` (`Supervise/IMemoryProbe.cs:65`), `systemctl --user restart` (`Supervisor.cs:538,661`) | **All five supervised behaviours** — they share one tick. The memory watchdog is the frame's last defence against an OOM kill. |
| A7 Browser stage | `systemctl --user is-active/stop`, `systemctl stop/start getty@tty1` (`BrowserStage.cs:425,460,464,511`) | §2.7's fallback rule — **the mechanism whose entire job is to prevent a blank screen**. |
| A8 Screen handover | `pgrep -x labwc` (`ScreenHandover.cs:292`), every 2 s | The panel stops switching terminals. |
| A9 Package inventory | `dpkg-query` | Reporting only. |
| A10 Array firmware reporter | `xvf_host VERSION` (`ArrayFirmwareReporter.cs:275`) | **Also the reconcile pass**, via D3's semaphore. |
| A11 Array flash | `dfu-util` (`ArrayFirmwareFlash.cs:808`), `xvf_host` (`:988,1004`) | Same, plus the reboot boundary stays held. |
| A5 Reconcile loop | everything | The pass, as already recorded. |

Two of those — A7 and A8 — are the machinery that exists specifically to guarantee something is on
the screen. A hung `systemctl` in the browser stage produces the exact state §2.7 forbids: a broken
desktop, no teardown, no console fallback, and no escalation. **`IProcessRunner.cs:83-86` already
contains the sentence that condemns this** — *"a hung pass is worse than a failed one, because
nothing on the screen ever changes to say so"* — and guards only the pipe-buffer case.

### 7.2 The console stage demotes itself permanently on one failed write

`ConsoleStage.CanWrite` goes false on the first write that fails and **never returns**
(`:113-124`), and `RunAsync` then returns outright (`:190-198`). The recorded reasoning is that a
console answering `EIO` "is not coming back without the panel overlay, and the overlay only takes at
a reboot". That is right for the case it was written for. It is not right for a transient: one
`EIO` on a working panel silently ends console narration for the life of the process, and the
process can run for days. Nothing retries and nothing escalates.

### 7.3 A loop that *returns* is never noticed

`WhenAllOrFirstFaultAsync` (`AgentHost.cs:779`) deliberately triggers on a **fault and only a
fault**: *"a loop that returns is left alone"* (`:756-762`), because A8 returns by design on any
machine with no consoles. The consequence is that **any** of the fourteen exiting its `while` — A1
on demotion, A8 on `!Switchable`, or any future early return — is indistinguishable from one that is
working. There is no liveness check on the fourteen and no count of how many are still running.

### 7.4 The systemd start limiter can leave the unit `failed`

`fl-agent.service` sets `StartLimitIntervalSec=5min` / `StartLimitBurst=10`. The unit's own comment
calls this §2.5's "the loop must be willing to give up" and prefers it to a silent loop — but the
observable result on the panel is nothing at all: the console stage dies with the process, the
browser is showing whatever it last had, and the frame is `failed` with one journal line. This is
also the state `ConsoleStage.cs:26-29` names: *"with the restart limiter now honoured the second such
crash leaves the unit `failed` and the frame silently dead."*

### 7.5 An unreadable keypair exits before the screen exists

C1 runs at `AgentHost.cs:123`; the console stage is constructed at `:182`. A frame whose keypair
cannot be read exits `Unrecoverable` having painted nothing, then does it again on every systemd
restart until the limiter stops it — arriving at 7.4 with no narration at any point.

### 7.6 The synchronous hub, in the other direction

Because `Publish` runs subscribers on the publisher's thread, a subscriber that blocks stops the
publisher — and the publisher may be the reconcile loop. The three subscribers that do real work are
A1 (a character-device write), B7 (a file write), and A3's semaphore release (which cannot block).
`ArrayFlashProgress.cs` treats this as a known hazard and routes around it; nothing else does.

**Not on this list, and worth saying so:** the escalation path, the backoff, the `Blocked` rows and
the consent screen's 30 min / 6 h duty cycle all change the screen. §4.2 of the ordering document
was right that those schedule waiting rather than end it.

---

## 8. The seven items outside the graph with no recorded reason

The operator's category (iii), gathered — seven items, six subjects (A1 and B8 are one decision).
Ordered by how much I think each is worth an argument.

1. **C6 — the agent's own systemd unit** (`Systemd/UnitInstaller.cs`). Every other unit is a
   resource. This one is written once by a verb and never checked again, and it carries four
   settings the file itself documents as easy to get silently wrong.
2. **A7 — the browser stage** (`Stage/BrowserStage.cs`). Level-triggered, acts on two systemd units,
   opens an interlock window over four named resources, retries unboundedly, escalates never.
3. **A8 — the screen handover** (`Stage/ScreenHandover.cs`). Describes itself as reconciliation
   (§2.2, twice) and is not in the reconciler; a graph resource's convergence depends on its
   behaviour.
4. **C4 — the startup handover** (`AgentHost.cs:219`). A8's one-shot half.
5. **A1 and B8 — the console stage** (`Stage/ConsoleStage.cs`). The circularity argument is strong
   and is **not recorded anywhere**; and the permanent demotion (§7.2) has no reportable surface at
   all today.
6. **A13 — the touch reader** (`Local/TouchRetry.cs`). A genuinely observable device fact with a
   clean delta and no resource; there is a circularity argument for it, but nobody wrote it down.

**Two more that are not category (iii) but belong on the same review**, because their justification
is thinner than it looks:

- **D3 — `XvfHost.Conversation`** (`AudioArrayResources.cs:670`). Not a candidate resource, but an
  unbounded process-wide lock spanning the graph boundary whose recorded justification rests
  entirely on a defect (§7.1) rather than on a property: *"a hung tool wedges the caller today, with
  or without this gate."* Fix §7.1 and this argument has to be rewritten.
- **B7 — the remembering subscription** (`AgentHost.cs:163-170`). Disk I/O on the publisher's
  thread, with no recorded consideration that `Publish` runs subscribers synchronously.

Everything else is (i) — 19 items — or (ii) with a quoted reason — 15 items: A4, A6, A9, A10, A11,
A12, A14, B1, C1, C3, C5, D2 inside the binary, and E2, E3, E4 in the environment.

---

## 9. Which of these carry their own timing constants

The operator has just rejected a duty cycle nobody asked for. Here is every other place the same
pattern was applied — a number chosen inside a component, with no setting and no way to change it
short of a release.

**Fleet-settable (12 values, one component).** Only A6's supervision block. All twelve
`supervision.*` values resolve through `Supervise/SupervisionSettings.cs` and are genuine §3.4
settings.

**Partially settable (3 components, 4 values).** A7's browser stage — `stage.browserCheckInDeadline`
60 s and `stage.browserRetryDelay` 2 min (`BrowserStage.cs:156,159`). A9's package inventory —
`packages.reportInterval` 6 h (`PackageInventoryReporter.cs:59`). A12's button —
`button.gpioPin` (a value, not a duration). Plus the reconcile loop's two: `repair.countdownSeconds`
and `provisioning.paceSeconds`.

**Compiled-in, no setting, no override — this is the pattern the operator is looking for:**

| Item | Constants it owns alone | Where |
|---|---|---|
| **A11 consent screen** | `ApprovalHold` 5 s, `DismissHold` 3 s, `AskWindow` 30 min, `RestWindow` 6 h, `CompletionLinger` 15 min | `ArrayFlashApproval.cs:452-471` |
| **A11 flash loop** | `DefaultInterval` 1 min, `PromptInterval` 5 s, `ReEnumerationTimeout` 90 s, `ReEnumerationPoll` 2 s | `ArrayFirmwareFlash.cs:319-338` |
| **B3 progress pump** | `DefaultBeat` 1 s | `ArrayFlashProgress.cs:354` |
| **A8 screen handover** | `PollInterval` 2 s, `SwitchDeadline` 5 s, `Settle` 3 s, `CoverAfter` 5 s, `ConfirmInterval` 100 ms | `ScreenHandover.cs:123,126,135,146,412` |
| **A1 console stage** | `TickInterval` 120 ms | `ConsoleStage.cs:130` |
| **A7 browser loop** | tick 5 s | `AgentHost.cs:839` |
| **A10 array reporter** | `DefaultInterval` 6 h — **and it explicitly refuses a setting** | `ArrayFirmwareReporter.cs:74` |
| **A13 touch reader** | `HoldDuration` 3 s, `PollInterval` 50 ms, `RediscoverDelay` 30 s | `TouchRetry.cs:101,109,118` |
| **A12 button** | `Debounce` 50 ms, `RetryDelay` 30 s; B4 `ClaimGrace` 2 s | `ButtonWatch.cs:92,100`; `IGpioLines.cs:86` |
| **A14 kiosk child** | `StopGrace` 5 s, relaunch 5 s, output budget 60 lines / 10 min | `KioskProcess.cs:254,689`; `ChildOutputBudget.cs:72,80` |
| **A2 link** | backoff 1 s → 30 s, jitter 0.2, handshake 20 s ×2, polite close 2 s | `Backoff.cs:24,27,46`; `ControlLink.cs:119`; `ConnectionAttempt.cs:58`; `WebSocketControlTransport.cs:81` |
| **A4 update** | `DefaultInterval` 1 h (init-only, not fleet-wired) | `UpdateService.cs:65` |
| **B1 local origin** | WS keepalive 20 s | `LocalOrigin.cs:368` |
| **C5 discovery** | mDNS window 2 s | `MdnsEndpointSource.cs:53` |
| **shared** | `HttpClient.Timeout` 5 min for **all four** downloads | `AgentHost.cs:246` |
| **E1 the app** | eleven constants (8 s iframe, 4 s probe, 3–30 s probe backoff, 60 s recheck, 15 s heartbeat, 0.5–5 s reconnect, 600 s auth sleep, 60 s cap, 2 s post-disconnect) | `app/frame-app.js:24-34`, `frame-stage.js:36-38`, `livekit.js:118,155` |

**The pattern, stated plainly.** Of the nine components outside the graph that own a duration
governing *how long a person is given*, **exactly one is configurable** — the repair countdown. The
consent screen's five, the touch hold, and the button debounce are all compiled in. §3.4 says every
setting is fleet-managed; for everything in this table it is not, and the answer to *"30 minutes is
too short for my household"* is currently "ship a new binary". That is the same finding the ordering
document reached for the consent screen alone (§8.4), generalised: **it is not one component that
was given an unrequested duty cycle, it is the house style for everything outside the graph.**

---

## 10. What this covers, and what it does not

**Covered exhaustively:** every task in `AgentHost`'s `running` list (14 of 14); every `Task.Run`,
`Process.Start`, `PosixSignalRegistration` and hub subscription in `src/FrameLink.Agent` (searched by
pattern, not sampled); every `public Task RunAsync`/`TickAsync` in the agent (26 declarations, all
accounted for); every constructor call in `AgentHost.RunAsync` between lines 115 and 724; both verbs
in `Program.cs`; the agent's own systemd unit; and `app/`.

**Bounded deliberately, and named:**

- **`src/FrameLink.Control` (the Fleet Manager) is out of scope.** It has its own loops — LiveKit
  supervision, alerting, ping/pong deadlines — and none of them runs on a frame.
- **`deploy/` holds v1 artifacts** (`docker/`, `gpio/`, `systemd/`, `wireplumber/`). The v2 catalog
  retires all of them; nothing there runs on a v2 frame. Not enumerated.
- **`tools/` is workstation-side** (harness, parity, diagram, upstream). Only E6 touches a frame, and
  only by refusing to cut its power.
- **Stock Raspberry Pi OS daemons are not enumerated** beyond E4 and E5. `systemd-timesyncd`,
  `systemd-logind`, `NetworkManager`, `journald` and the rest run on every frame, are configured by
  no resource, and are outside the graph in the trivial sense. I judged that outside the operator's
  question, which is about things *this product* placed there. If that judgement is wrong, the list
  to add is short and I can produce it.
- **Test doubles and `tests/` are not enumerated.** Nothing there runs on a frame.
