# FrameLink v2 — Build Specification

**Status: building.** M0 is closed on hardware, M1 and M2 are built, and the agent runs on the
development mule. This document remains the specification the build follows; §5 is the execution
plan, and Appendix A preserves every decision with its reasoning.

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
  It also removes the loopback restriction Docker's port publishing was performing for the
  slideshow port, which is a real cost of this decision and is recorded as one (§3.6,
  decision 56).
  Fetching from upstream rather than redistributing keeps AGPL source-offer obligations off
  this project and off every self-hoster; a Fleet-Manager mirror stays available as a later
  operator setting.
- **The reSpeaker control tool is the sharpest exception to the first bullet, and it is named
  rather than smuggled in** (decision 63). `xvf_host` is a helper executable *and* it loads three
  shared libraries from its own directory, which is precisely what "no helper executables, no
  shared libraries" forbids. There is no alternative today: the firmware version, the speaker
  amplifier pin and every DSP parameter are reachable only through that binary, and it may not be
  rebuilt or redistributed. So the agent fetches six files pinned at a commit SHA, verifies each
  by digest, and installs them under `/var/lib/fl-agent` — the Immich Kiosk shape applied to a
  worse-behaved upstream. **The exception is meant to end**: implementing the XVF3800 wire
  protocol natively in the agent would delete all six files and make this bullet literally true,
  and it is recorded in `TODO.md` as the work that retires this.

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
attempts)` · `Blocked(dependency)` · `Escalated(admin-notified)`.

**`Escalated` is the terminal status. There is no rung below it** (decision 66). A resource that
has given up is either retried by a human — from the Fleet Manager or from the frame's own screen,
which resets its budget to a fresh three — or it stays escalated for as long as it takes somebody
to arrive. Nothing in the agent promotes an escalation into a second, deader state on its own.

### 2.4 Reboot and retry discipline

- **Every resource reboots. No exceptions, no per-resource cleverness.** Change one thing,
  reboot, verify it stuck. Some reboots are unnecessary; that is accepted deliberately, because
  deciding *which* settings need one is precisely the reasoning that produced v1's governor bug
  (the kernel parameter landed in `/proc/cmdline` and the governor still came up wrong).
  "Applied" is never claimed from a successful write — only from an observation after the
  setting had to survive a boot.
- **Exponential backoff per resource.** Backoff exists to stop a reboot loop from wearing the
  hardware; an unbounded retry cycle is more damaging than a stalled provision.
- **A device-level reboot floor, underneath everything** (decision 79). Past **120 reboots in a
  rolling six hours** the frame stops rebooting and says so, whatever any resource, ledger or
  status claims. This is deliberately not part of §2.5's ladder and shares no state with it: the
  ladder counts *failures*, and the cycle this bounds is made of successes — a change that applies,
  verifies, and is undone afterwards leaves nothing for a failure counter to count. The floor
  counts the only thing that is certainly happening, which is the reboots themselves. The number is
  sized against a whole first provision rather than against a rate, because the rates do not
  separate: a bare provision of the 80-resource catalog runs at ~2.6 reboots per minute and the
  measured livelock ran at ~2.3. A refused reboot is an ordinary refused reboot — the change is
  written and cannot be proven, so it spends an attempt and reaches a person on §2.5's schedule, at
  no further cost in reboots. A **retry** grants a fresh window, exactly as it grants a fresh
  attempt budget.

### 2.5 Escalation ladder

The loop must be willing to give up.

1. Resource fails post-boot verification → retry with growing delay.
2. **Attempt budget exhausted — three attempts** (decision 67) → stop touching it, mark
   `Degraded` with the exact expected-vs-observed delta and attempt count, notify the Fleet
   Manager.
3. Fleet Manager notifies the operator (Home Assistant; SMTP for self-hosters without it) and
   offers **retry** — which resets that resource's budget to a fresh three. A **remote shell** is
   offered here too, and the interface should suggest one whenever there is an error, but its
   availability is not conditional on one: the shell is an ordinary operator action available at
   any time on any adopted frame, error or no error (§3.6, decision 69).
4. **An escalation stops the pass** (decision 68). Not just that resource: the agent performs no
   further Act and takes no further reboot, on any resource, until a human retries. It holds the
   failure on screen and waits. Stopping means stopping *acting*, not stopping *looking* — the
   observation sweep the pass was already making is completed and published, because Observe is
   side-effect-free (§2.3) and the `Blocked(dependency)` rows behind the failure are how an
   operator sees what is queued up behind it. An empty list would be less informative, not safer.
   **Stopping the pass is not stopping the loop** (decision 75). The loop keeps ticking on its
   ordinary interval, doing nothing but looking and reporting, because that tick is the only thing
   that can ever notice the budget a retry has reset — and because a frame that goes silent is
   indistinguishable, from the one surface still reachable, from a frame whose agent has died.
5. **Retry is also pressable at the frame** (decisions 72 and 77). Whoever is standing in front of
   it can ask for another go on the touchscreen once a resource has given up, without reaching a
   Fleet Manager. It is the same command over the same reset path as rung 3's, not a second
   mechanism. **Both rendering stages offer it**: the browser stage draws a button, and the console
   stage — which has no layout it can hit-test against — takes a three-second hold anywhere on the
   panel. A frame whose panel is not up yet has no touchscreen at all and says exactly that,
   naming the Fleet Manager instead — see §2.7.
6. `Escalated` is terminal (decision 66). Either a human retries after fixing the underlying
   cause, or the resource stays escalated indefinitely. There is no second-strike device death:
   escalated already means *stopped, waiting for a person*, and a second state below it added
   nothing but a way for a frame to become unrecoverable while nobody was looking.

**A change to the budget is retroactive, by design** (decision 74). Attempt counts are persisted
(§2.1) and the budget is not, so a frame provisioned under an older, larger budget carries counts
that the current one cannot express. Those counts are **clamped on read** — every place that
compares a count against the budget or shows it beside the budget uses `min(stored, budget)` — and
nothing rewrites the stored counters to fit, because a reset would silently un-escalate frames an
operator has already been told about. The consequence is stated rather than hidden: a resource that
has already spent more attempts than the new budget allows escalates on its next failure instead of
receiving a fresh allowance. That is the new policy applied. The budget was lowered because attempts
cost card wear, and a resource that has already spent five of them is exactly the case that argument
is about; the recovery from being wrong is unchanged and is one press, since a retry grants a full,
fresh budget.

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

**`Reconciling` is the one row above that is a field rather than a rung** (decision 82). The other
five are values of `DeviceState`, and every one of them is resolved from what the *Fleet Manager*
said — a handshake outcome, or silence. `Reconciling` is what the frame has observed of *itself*,
which is orthogonal to that rather than ordered among it: a frame can be unreachable-but-was-green
and locally drifted in the same instant, and one enum would force that to be reported as one or the
other. So it is carried as `AgentStatus.Drifted` beside the rung, and the row's behaviour is met by
composition — `ProductRuns` is `Condition.ProductRuns && !Drifted`, which is this row's **No**, and
the repair screen's headline is composed from both halves (`c3116bc`, which fixed a frame headlining
*"Everything is working"* over its own failed resource).
It was also a member of the enum until decision 82, reachable by nothing, with an accent in the
console palette that nothing could ever select. **That accent now has a path to it, and it is the
composition rather than the enum** (decision 83): `StagePalette.For` takes the whole status and
switches on `ReconcileVoice.Voice`, the one place the conjunction is made, so this row is amber on
both stages for the same frames whose headline says it is being put right.

**There is no rung below `Reconciling`, and there is not going to be one** (decision 66). A frame
holding an escalation is `Reconciling`: it has stopped acting and is waiting for a person, which
changes what the screen says and changes nothing about the ladder, because the ladder answers one
question — does the product run? — and the answer was already no. The `Halted` rung §2.5 used to
end on is gone from the design outright; it was the one state nothing recovered from on its own,
and it existed only to stop a loop that now stops at the first escalation anyway (decision 68).

**A stopped frame must never render as a working one.** The repair screen distinguishes *this is
attempt 2 of 3* from *this gave up after 3 tries* in words, and only the first of the two animates
(§2.7). A frame that redraws a live-looking attempt counter for a resource it has permanently
stopped touching is telling the person in front of it that something is still happening, which is
the specific failure that made a frame look like it was rebooting for ever.

**The rule is about every property the screen has, not only the moving ones**, and it took three
findings to say so. Decision 70 caught the animation, `c3116bc` caught the wording, and decision 83
caught the colour — a frame that had given up, and a frame putting one of its own settings back,
were both painted in the green of a frame that is working, because the accent read §2.6's ladder
while the headline above it read the composition. **A frame that is not running the product must not
be rendered as though it is: in wording, in animation, or in colour.** All three now come off one
classification (`ReconcileVoice.Voice`), so a fourth property added later inherits the rule instead
of having to rediscover it.

**And a working frame must never render as a broken one** (decision 76). This is the converse, and
it was missing: the rule above forbade one direction of dishonesty and said nothing about the other,
so a frame that had converged 76 of its 79 resources reported **0 in sync** the moment one of them
gave up, with all 77 remaining rows claiming to wait on a resource most of them had never depended
on. Overstating damage is exactly as misleading as understating it, and it is worse in one respect:
it sends whoever reads it looking for a fault that is not there. So a stopped frame reports what it
*is*. The pass keeps observing after it stops acting (§2.5 rung 4), every row is established by
looking at this frame on this pass, and **a row may only name a dependency the graph actually
contains** — the DAG already answers that question and no other answer may be invented for it. The
two rules together are one rule: what the frame says about itself is what it observed, in both
directions.

**A frame that has given up names who to call.** The operator's name and contact details are
pushed by the Fleet Manager and persisted on the frame (decision 71), so the sentence "tell
&lt;name&gt;" is on screen whether or not the Fleet Manager is reachable at the moment it is needed —
which is exactly the moment it may not be.

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

**The loop implements that sentence, and the two halves of it are separated rather than conflated**
(decision 78). A resource that has converged and drifted again is not the same as one that has
never converged, and until this the loop had no concept for the difference — a successful
post-reboot verify cleared the ledger outright, so the next pass began from nothing. What is
remembered now is one counter per resource: the number of **consecutive reversions**, where a
reversion is a value this frame observed correct and later observed wrong *against an expectation
that has not moved*. An expectation that **has** moved is the other half of §2.6's sentence — a
desired value pushed from the Fleet Manager — and it never counts towards a give-up, because an
operator tuning a setting would otherwise stop their own frame for using the product as designed.
**Three consecutive reversions is a conflict**; the resource is not acted on again, and it goes
straight to §2.5 rung 2 with the delta naming the cause rather than only the symptom. A value that
holds for a whole drift-detection interval forgives the reversions before it, so an ordinary one-off
repair — a package postinst rewriting a file — is repaired silently however often it recurs over a
frame's life. **A supervision window (§2.10) is never a reversion**: it is the one drift whose cause
the frame already knows.

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
5. **One item at a time, with its attempt count** — `item x attempt 1 of 3`, then `item y attempt
   2 of 3` (decision 70). The frame narrates the resource it is working on, not a list, and the
   count is the one from §2.5 rung 2 rather than a second counter.
6. **Backoff state** including remaining wait, so a pause never looks like a hang.
7. **Stopped state, worded and rendered as stopped** (decision 70). When a resource has given up
   the screen says `item z failed after 3 tries, expected a but got b` — the delta §2.5 rung 2
   already recorded, rendered rather than re-derived — and it stops animating. An attempt counter
   that keeps painting for a resource nothing is touching is a lie the console repeats every boot.
8. **Who to contact**, on any error screen: the operator's name and contact details as the Fleet
   Manager last pushed them, read from the frame's own state so an unreachable server cannot take
   the answer away (decision 71).
9. **A retry the person at the frame can press** once a resource has given up (decisions 72 and
   77), on **both** stages. It resets the budget through the same path the Fleet Manager's retry
   uses — one reset in the agent, now with three callers. The browser stage draws a button. The
   console stage reads the panel's evdev node directly and takes a **three-second hold anywhere on
   the screen**: it has no compositor, no browser and no way to hit-test a drawn rectangle against
   a digitiser reporting unrotated panel pixels through a console the kernel has turned 90°, and a
   button that answers somewhere other than where it appears is worse than no button. A hold needs
   no coordinates. It is a hold and not a tap because the screen is at eye level in a living room
   and a brush past it must not start a frame rebooting; the hold is counted down on screen while
   the finger is there, so nobody lets go at two seconds thinking the frame is dead. **A frame with
   no touchscreen — every frame before the panel overlay is applied — says so and names the Fleet
   Manager**, which is then a true statement rather than a hedge.

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
arithmetic: 80 resources at 60 s is 80 minutes of countdown against ~30 minutes of measured reboot,
so three quarters of a bare provision would have been spent pausing for nobody.

**A provisioning pace gives the watching back, as an option** (decision 53). What decision 51 also
removed is the only thing that let a human *see* a provision happen — 79 screens now paint at
machine speed — so `provisioning.paceSeconds` is a fleet setting (§3.4) that inserts a pause before
each provisioning reboot, exactly as `repair.countdownSeconds` does for a repair. **It defaults to
0**, so an unconfigured fleet provisions precisely as it does today; an operator standing in front
of a frame raises it, watches, and puts it back. It is the same pause through the same code — one
function, `CountdownScope.ForReboot`, takes both durations and the durable "has this frame ever
been green" decides which one applies — so the "Reboot now" skip works on a paced provision
without a second implementation. The two settings are siblings and read as siblings: same unit,
same fleet-default-plus-per-device-override, and opposite fallbacks for a mistyped value, because
a typo must never silently remove the one pause a person has to read a repair and must never
silently add an hour and a half to a provision nobody is watching. `--development` is unaffected
and forces 0 for both — it is a binary switch, not a setting.

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
   `/dev/tty8` — a virtual terminal of its own, not the one the panel boots on (decision 57) — a
   designed terminal interface with colour, box drawing and animated progress, not log spew. No
   login session, no dependencies, works from the first second of the first boot. **The panel has
   to exist first.** On a stock image there is no DSI connector, no `/dev/fb0` and no backlight,
   and a write to the console succeeds while producing no pixels — a stage that trusted its own
   write would report success and show nothing. That was measured on `/dev/tty1`, and it is a
   property of the missing framebuffer rather than of any particular terminal, so it holds
   identically on `/dev/tty8`: with no `/dev/fb0` there is no virtual terminal that produces
   pixels. The display overlay and the console rotation are therefore reconciled **first**,
   right after the agent-version root and ahead of adoption: a deliberate carve-out from §5.5's
   brick-capable-last ordering, decided in favour of this section (decision 46).
2. **Browser stage.** As soon as the reconciler has brought up the kiosk stack, that same
   browser renders the agent's page. Bringing the stack up is therefore front-loaded in the DAG.
   **The page composes nothing about the frame's state, including its colour** (decision 83): the
   accent is chosen once by the agent, from the whole status, and sent by name — `green`, `amber`,
   `blue`, `red`, `grey` — for the page to look up. Whichever stage the panel happens to be showing
   is therefore the same colour as the other one, and a page that does not recognise a name renders
   its ordinary headline rather than guessing at a state.
3. **Stage handover.** Two stages on two terminals means which one the panel shows is decided
   rather than assumed, and it is decided level-triggered on **compositor presence** (§2.2,
   decision 57): the console keeps the panel while no compositor is running and hands it back
   the moment one is — which is precisely the instant labwc used to draw over the console back
   when both stages shared `/dev/tty1`. A converged frame therefore behaves exactly as it did,
   down to the second, and §2.6's not-green states are still rendered on the browser surface
   from the same status hub. What is new is the hour the shared terminal never covered: through
   provisioning the console has the panel to itself, instead of being repainted within a second
   by the login prompt `getty@tty1` puts on the same screen. That getty is left untouched, so
   the physical login §5.5 depends on is still there while the agent narrates, and Ctrl+Alt+F8
   reaches the narration by hand whenever the agent has deliberately *not* taken the screen.
4. **Fallback rule.** After starting the GUI the agent requires the page to check in over the
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
- **An update that changes the app reaches the screen, and until decision 84 it did not.** The app
  travels inside the binary (§2.1), so replacing the binary replaces what the agent *serves* while
  the browser goes on running the document it fetched from the agent that has just exited. §2.10's
  page-refresh behaviour closes that: the served app carries a digest of its own bytes, the running
  page says how old its document is, and a page that predates the change is told to reload itself.
  The digest is over the app rather than over the agent version precisely so that an update which
  touches no page does not touch the screen either.

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

**The five supervised behaviours.** The first four are carried over from v1, all measured on this
hardware. The fifth could not have existed in v1 — the fault it answers is created by §2.1 and §2.8
together, and both are new — and the *fault* is measured on this hardware while the behaviour that
answers it has not yet run there (decision 84).

| Behaviour | Trigger | Action |
|---|---|---|
| **Memory watchdog** | Chromium process tree over 1.8 GB RSS, **or** system `MemAvailable` under 350 MB, sampled every five minutes | Restart the browser |
| **Daily restart** | 03:00 local, catching a missed run up once after an outage | Restart the browser |
| **Kiosk liveness** | The app's local channel silent for 90 s, evaluated every 15 s, five-minute cooldown | Restart the browser |
| **Camera recycle** | Every call-end | Restart the camera node |
| **Page refresh** | The running document is older than this agent process and the app it loaded is not the app this agent serves | Tell the page to reload itself |

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
- **The page refresh exists because §2.1 and §2.8 together create the fault, and neither of them
  notices it.** The app is inside the agent binary and the agent replaces that binary hourly, so
  every update ships a page the running browser has no reason to fetch — measured 2026-08-16, where
  the agent served a new stage correctly, the server-composed headline updated over the live
  channel, and the half the page draws never appeared at all. Nothing else covers it: kiosk liveness
  watches the *channel*, and the channel came back — the page's WebSocket reconnects on its own
  backoff, so the journal recorded "The page checked in after 0 s" about a document ninety minutes
  old. A reconnect and a load are the same event at the socket layer.
  `unit.chromium-kiosk.running-matches-content` compares the running process's argv against the
  unit's `ExecStart`, and an app change moves neither. The daily restart would fix it, up to
  twenty-four hours later and by accident.
- **Deciding it needs one fact from inside the document, and the page is the only party that has
  it.** The page reports how long ago its own document began loading; the agent compares that
  against how long its own process has been running. A document younger than the agent process was,
  by construction, served by *that* process, so the app it is running is the app this binary carries
  — and that build is recorded, durably, as what the running page loaded. A document older than the
  agent process is running whatever was recorded last, and if the record disagrees with what is now
  served, the page is stale. Both sides of the comparison are monotonic, which matters on a machine
  with no RTC: `performance.now()` counts from the document's navigation and `Environment.TickCount64`
  from system boot, so neither moves when `systemd-timesyncd` steps the clock seconds after boot.
- **Reloading the document, not restarting the browser, and the distinction is the whole point.** A
  unit restart tears down a renderer, a compositor connection and a GPU context, and blanks the
  panel for seconds, to fix what `location.reload()` fixes in about one. It is also the one
  supervision action that opens no interlock window, because it makes no resource transiently
  wrong — the process, its command line, the compositor and the display transform are untouched.
- **It never fires during a call, and that guard is in two places because one is not enough.** The
  agent's copy of "a call is in progress" is up to one heartbeat old, so the page holds the deciding
  vote: it refuses a reload it is in a call for, remembers the request, and takes it the moment the
  call ends. That is the daily restart's stand-down, applied to the one behaviour that would
  otherwise interrupt a conversation over a cosmetic difference.
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

Two consequences follow directly. **Supervision never reboots the device**: its whole vocabulary is
restarting a supervised process or reloading a page, and a reboot blanks the frame for a minute,
which is exactly the product-stopping behaviour it must not have. When restarting is not enough, the
handoff below gives the problem to reconciliation, which may reboot with the full §2.7 narration. And
**supervision defers only what can wait**: the daily restart and the page refresh stand down while a
call is active and run at the next opportunity, exactly as v1's `Persistent=true` catches a missed
run up, while the memory watchdog defers for nothing — the alternative to acting during a call is an
OOM kill or a hardware-watchdog reset, which ends that call anyway and takes the frame with it.

**The interlock with the reconciler.** Both actors can touch the same unit and each can misread the
other's work, so one rule covers both directions: **the reconciler holds a lock on what it is
applying, and a supervision action opens a window on what it touches.**

1. Supervision does not act on anything the reconciler is `Progressing`, `AwaitingReboot` or
   `Blocked` on. Restarting a browser the reconciler is deliberately holding down, or racing an
   apply, produces exactly the interference that makes "which change broke it" unanswerable (§1.2
   principle 5).
2. While a supervision window is open, the transient wrongness it causes — a kiosk process that is
   briefly not running — is expected rather than drift, so it never trips §2.6. **An action that
   makes nothing transiently wrong opens no window**, and the page refresh is the one such action:
   a reload leaves the process, its command line, the compositor and the display transform exactly
   as they were. Opening a window there would excuse a wrongness that cannot occur, which is a hole
   in drift detection rather than an interlock.
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
| `supervision.pageRefreshCooldown` | `5m` | Minimum spacing between page refreshes |
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
  report what it is and display "waiting to be adopted". **The operator's contact details are not
  an exception to this** (decision 71): the registration endpoint below is open to the internet
  and answers anything that connects with `pending`, so a contact frame on that path would hand
  the operator's name and telephone number to every anonymous caller that found the URL. A
  pending frame therefore shows the generic "ask whoever looks after your Fleet Manager", which
  costs almost nothing — §3.2 records that the operator is usually the first person to connect a
  frame, so the person standing in front of an unadopted one is normally the operator themselves.
- **The registration endpoint is fully open**, with mandatory abuse controls because the server
  is internet-exposed: rate limiting, a hard cap on pending records, auto-expiry of
  unadopted rows, and pending records that allocate no resources. The rate limit is keyed on the
  **proven keypair**, with the per-address window kept as the bound on traffic that has not
  proved an identity yet — an address is what a whole household shares, so charging a frame for
  one is charging it for its neighbours (decision 92). An attacker can create noise
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
schedule), slideshow (album, interval), locale and time zone, countdown duration, provisioning
pace, call room, and whatever comes later.

**Two of them are about the operator rather than about the frame** — `operator.name` and
`operator.contact` (decision 71). They are stored and edited exactly like every other fleet
setting, and they are the only ones delivered on a channel of their own, because §3.3 withholds
settings from a pending device and these have to reach one.

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
- **It is available at any time, on any adopted frame, error or no error** (decision 69). §2.5's
  escalation surface *suggests* a shell, and should; it does not own it, and nothing about a frame
  being healthy withholds it. **Not built yet:** `shell` is a reserved channel name with no
  implementation on either side, so this is the requirement the implementation must satisfy rather
  than a description of what runs today.
- **One port on the frame is open to the LAN, knowingly.** Immich Kiosk v0.42.0 binds `:{port}`
  on every interface and its configuration carries no host or bind field, so the loopback
  restriction v1 had was **Docker's port publishing, not Kiosk's**, and it left when Docker did,
  under decisions 40 and 41. The property above is untouched: nothing outside the household
  reaches this port either. What changed is *inside* the household — a read-only slideshow endpoint
  showing the household's photos is now reachable from any device on the same LAN. **Accepted
  rather than filtered** (decision 56) — one LAN hop of blast radius, content already visible on
  a screen in the room, against a packet-filter resource to own forever. `kiosk.listen-address`
  reports the actual bind set on every pass so the exposure stays visible, and an upstream bind
  setting (Appendix B item 6) closes it.
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
- **Advertised IP comes from the signalling URL:** when `FRAMELINK_LIVEKIT_PUBLIC_URL` names an
  address a frame can dial rather than a host name, that literal is written into the generated
  `livekit.yaml` as `rtc.node_ip`, so a container publishing its media ports one-to-one
  advertises the address frames dial rather than its own bridge. `use_external_ip` stays false,
  and with `node_ip` set that is load-bearing rather than merely preferred (decision 85).
- **A person joins through a route of their own:** every other token in the fleet is minted for
  an adopted *device*, so `POST /api/livekit/guest-token` is the one a human uses — a
  short-lived token for a named participant in the `guest:` namespace no frame may occupy,
  behind §3.2's operator session, storing nothing. A web client and a mobile app are
  **explicitly out of scope for v2**; this is the seam they would mint through, not the first
  half of one (decision 86).
- **Deferred within v2:** TURN/TLS, and an advertised address for frames in other households.
  LAN calling is validated first.
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

### 3.9 Image generation

The Fleet Manager turns a pinned stock Raspberry Pi OS image into a **ready-to-flash FrameLink
image**: flash it, put the card in a frame, and the frame appears in the adoption queue with
nobody having typed anything. It sits logically *before* §3.3 — it is how a frame comes to be
pointed at a control URL in the first place — and is numbered last only because renumbering five
sections and their cross-references costs more than it explains.

**The assumed blocker does not exist, and that is what moved this out of v3.** The reason to
defer was always that writing into an arm64 ext4 filesystem meant privilege, loop devices and
emulation, none of which belongs in a self-hoster's container. Measured against the real
`2026-06-18-raspios-trixie-arm64-lite.img` in a plain `debian:trixie-slim` container with **no
`--privileged`, no `--cap-add` and no device mapping**: `debugfs` writes the binary, sets its mode
and owner and creates the enable symlink; `mcopy` writes the boot-partition file; `e2fsck -fn`
calls the result clean. `mount -o loop` **fails** in that same container, which is the proof that
loopback was never involved. It is `e2fsprogs` (1537 kB installed) and `mtools` (400 kB) editing a
file. Because it is file manipulation and not execution, an **amd64** Fleet Manager writes an
**arm64** image with no qemu, no binfmt and no emulation anywhere in the path.

**What a generated image carries**, and the list is exhaustive:

| In the image | Where | Why |
|---|---|---|
| The `fl-agent` binary | `/usr/local/bin/fl-agent`, **0755 root:root** | The one the Fleet Manager serves, so §1.2 principle 4's version-lock holds from the first boot |
| Its systemd unit | `/etc/systemd/system/fl-agent.service`, 0644 root:root | Byte-identical to the agent's own embedded copy — the Fleet Manager embeds *that file*, not a copy of it |
| The enable symlink | `/etc/systemd/system/multi-user.target.wants/fl-agent.service` | Enablement is not a database, it is this symlink; it lands beside the stock `userconfig.service` |
| The discovery seed | `/boot/firmware/framelink.conf` with `control-url=` | §4.3's boot-partition candidate, parsed by the agent's own `BootFileEndpointSource` |

**And what it must never carry.** Decision 17 — "generic image, no secrets" — is the constraint
this capability is most likely to violate by being helpful. Pre-seeding a device token so the
frame arrives already adopted is not a shortcut through enrollment, it is the destruction of it:
identity is the keypair the agent generates on its own first boot, and adoption is a human
pressing **Adopt**. The request type therefore has exactly two fields and both are URLs, so there
is nowhere to put a credential — widening it is a review a person performs, not a line somebody
adds. The two ways a secret could still ride in on a URL are refused explicitly: **user
information** (`https://token@host/`) and **a query or fragment** (`?adopt=…`). Control characters
are refused too, because the seed is a `key=value` file and a newline in a value does not corrupt
it, it appends a line to it. One image serves the whole fleet, which is also what keeps the
storage cost to one artifact rather than one per frame.

**The base image is an upstream dependency and §7.1 freezes it.** Its URL, the digest Raspberry Pi
Ltd publishes beside the archive, the digest of the decompressed image, and its exact byte length
are all pinned in source where changing them is a diff somebody reviews, and the generator
verifies length and digest **before it touches the file**. Silently building on whatever the
mirror serves today would mean shipping cards made from an image nobody looked at. The pin also
records the measured partition offsets — never used as an input, since the real ones are read from
the image's own partition table every time, but cross-checked against it, so a pin updated with a
new digest and stale geometry is caught by the server rather than by somebody holding a card.

**`e2fsck -fn` before the artifact is offered, and the reason is measured.** `debugfs -R` **exits
0 when the request fails.** A missing parent directory, an existing target, a forgotten `-w`, an
offset past the end of the file — every one of them prints a message and exits 0. A generator that
trusted the exit code would write the seed file, silently fail to install the agent, pass its own
checks and hand somebody a card that boots into stock Raspberry Pi OS. So success is read from the
output against a whitelist of the two benign lines, an unrecognised message is a refusal, and the
read-only filesystem check is the last step of every build — the artifact filename is unreachable
except through it. Worse than a wasted call: `debugfs mkdir` on a directory that already exists
allocates the inode *before* noticing the name is taken and abandons it, leaving a filesystem
`e2fsck` reports as corrupt while `debugfs` exits 0. The generator therefore contains **no
`mkdir`** at all; all three directories it writes into exist in the pinned image, which is a fact
because the pin is verified first. The failure being designed against is not an untidy server, it
is a person driving to a house with a card that does not boot.

**Why a whole image, and what the cheaper tiers would have bought.** Four shapes were costed.
Decision 32's *literal* wording — "ready-to-flash, with URL, Wi-Fi and settings pre-seeded" — is
already satisfied by tier A, because §2.8 has the Fleet Manager serving the agent binary over
versionless HTTPS and §4.3 already accepts a boot-partition file. That is worth stating plainly so
the choice stays legible.

| Tier | Artifact | Buys | Does not buy |
|---|---|---|---|
| **A** | ~2 KB `framelink.conf` | Satisfies decision 32's wording at zero cost and no new dependency | Operator still flashes a stock image, finds the boot partition and drops a file on it; the agent must still be fetched over the network before anything happens |
| **B** | ~1.4 MB overlay — seed + binary + unit | Removes the first-boot network dependency | Still a stock flash first, still a mounted card, and now a second thing to keep in step with the served binary |
| **C** | **~500 MB compressed / 2.98 GB raw image** ✅ | Flash and go. Nothing to mount, nothing to type, no first-boot fetch, one artifact for the whole fleet. Also retires §5.3's "pre-flash spare cards with SSH enabled" prep and makes §5.5's card-swap recovery a swap rather than a flashing session | Free. ~6 GB of disk on the operator's server at rest and ~9 GB while building (below) |
| **D** | One image per frame | Nothing that C does not | Would put identity in the image, which is where decision 17 dies. Refused on principle, not on cost |

**⚠ The storage requirement is genuinely unbudgeted.** §3.1 specifies one container and one
volume, sized in the reasoning for a SQLite file, and does not account for a 2.98 GB base image
plus a working copy plus an artifact. That is stated here rather than left to surface as a full
disk that takes the database down with it. Three mitigations, all implemented: the image directory
is separately configurable (`FRAMELINK_IMAGE_DIR`) and **both compose files now point it at a
second volume of its own** (decision 88), so neither the base image nor the artifact is ever on the
volume holding the database; a
free-space check runs **before** anything is copied and refuses with the required and available
figures rather than half-writing an image; and exactly one build runs at a time, with the finished
image published by a rename inside the same directory, so peak usage is the base image, one
working copy and one previous artifact — roughly 9 GB — rather than an unbounded pile.

**Operator-facing shape:** three routes under `/api`, so the operator password guards them with no
special case. Read the state, ask for a build, take the file. Builds are asynchronous because they
are minutes of copying and checking, and a request held open for minutes is one some proxy will
eventually drop; a second request while one is running is answered rather than queued.

**⚠ Two things this does not yet do, named rather than implied.**

1. **Wi-Fi is not seeded**, although decision 32's wording included it. The vendor's supported
   channel is `/boot/firmware/custom.toml`, which also governs first-boot user creation, hostname
   and SSH, so a partial one changes first-boot behaviour in ways nothing short of flashing a card
   can confirm. And on Bookworm and later the WLAN interface stays rfkill-soft-blocked until a
   wireless regulatory country is set, so a NetworkManager keyfile alone is a seed that looks
   right and never associates. Adding a `[wlan]` section to a generated `custom.toml` is the shape
   that will work; it needs a card and a boot to prove.
2. **No card has been flashed from a generated image and booted.** Everything above is verified —
   the real image, the real tools, unprivileged, as a non-root user, `e2fsck` clean, the binary at
   0755 root:root, the unit byte-identical, the symlink beside `userconfig.service` — but
   verification stops at the filesystem. The first flash is the acceptance test for M2.5 (§5.1),
   and nothing generated has yet been written to a card. The reader that §5.3 once listed as
   missing has been attached since 2026-08-23; `tools/harness/cards.json` is the register and
   names a blank card sitting on the desk. What is outstanding is the flash, not the hardware.

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
| **M2.5** | **Image generation** | A card flashed from a Fleet-Manager-generated image boots, starts the agent unattended, and appears in the adoption queue (§3.9). |
| **M3…Mn** | **Resource migration** | Guide by guide, lowest-risk first. Each group passes the triple bar: state-diff vs the frozen v1 reference, checkpoint assertions, validation battery on the mule. The array firmware DFU used to be the last group, stopped being one under decision 90, and is not one again under decision 91: the firmware *version* is still observed and reported rather than converged, the pinned *image* is an ordinary resource, and the write itself is an interlocked operation outside the loop — single-use in the sense that one authorisation buys one operation, which decision 93 makes up to three writes. |
| **Mn+1** | **Bundled LiveKit** | Fleet Manager supervises LiveKit and mints tokens at adoption; guide 7 obsolete. |
| **Mn+2** | **Production Fleet Manager** | Deployed as a PortainerCompose stack behind Traefik at `framelink.huisman.io`, with alerting. |
| **Mn+3** | **Parity** | Stock image → adopt → fully green frame, mechanically equal to the frozen v1 reference. Deep, triple-checked verification. Only then do guides retire to the minimum set (§8). |

**Why skeleton before reconciler:** every genuinely unknown risk is an *integration* risk —
AOT on arm64, the update path, the frozen handshake, adoption, socket liveness. None are hard
once proven and all are miserable to discover late underneath a finished reconciler. After M1
the work becomes pleasantly repetitive: add one resource, verify on the mule, repeat.

**Why image generation sits between M2 and the migration, and not at the end.** Its dependencies
are already paid for: it needs an agent binary the Fleet Manager serves (M1), the boot-partition
discovery candidate (§4.3, M1) and the unit (M1). It needs **nothing** from LiveKit (Mn+1) or from
the production deployment (Mn+2), and it does not need the reconciler to be finished — a generated
image's whole job is to get `fl-agent` running and pointed at a Fleet Manager, and whatever the
reconciler can do at that moment is what it does. So it *could* go almost anywhere, and where it
goes is decided by what it pays for rather than by what it needs.

What it pays for is M3…Mn, which is by far the longest phase and the one that wipes and re-flashes
the mule repeatedly — §5.3 already budgets spare cards on exactly that expectation. Ahead of the
migration, every one of those cycles becomes flash-and-walk-away instead of flash-then-provision-by-hand,
§5.3's "pre-flash stock cards with SSH enabled" preparation stops being a manual step, and §5.5's
card-swap recovery path stops requiring a flashing session. Put after the migration, it would
deliver the same capability having saved none of that. The half-milestone number is deliberate: it
renumbers nothing, because M3…Mn, Mn+1 and Mn+2 are referenced from other sections and from other
workstreams' notes.

It is also the smallest milestone here, and the one whose acceptance test is a single physical
act — flash a card, watch a row appear — which is why it is worth doing while the thing it
accelerates is still ahead rather than behind.

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
   is the recovery path for a boot-breaking resource. **What actually exists is three cards and
   the register is what says so** — `tools/harness/cards.json`, read with `fl.py cards list`.
   Do not take an inventory from this paragraph: the requirement above is a requirement, the
   register is the state, and the register is the one that is maintained.
3. **An SD card reader** — attached since 2026-08-23 (§6.1). It reads and writes the cards the
   register in `tools/harness/cards.json` tracks. Note what it cannot do: nothing readable
   through it is unique to a card, so a card is identified by its MBR signature and by where
   the register last recorded it, never by the reader.
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
   the development Fleet Manager. ✅ auto-starting, verified 2026-08-24: a `Docker Desktop`
   entry in `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` and `AutoStart = True` in
   Docker Desktop's own `settings-store.json`. The ⚠ that stood here — "was not running;
   started manually" — was true on 2026-08-15 and had outlived its condition. The development
   Fleet Manager is itself a `restart: unless-stopped` container now (§3.1, guide 15), so it
   comes back with the daemon rather than being started by hand.
4. **GitHub push access.** ✅ working
5. **At Mn+2 only:** Portainer API, PortainerCompose stack authorisation, DNS + Traefik route
   for `framelink.huisman.io`.

### 5.5 Honest limits of autonomy

- **An unbootable mule needs hands.** A malformed `config.txt` or a bad EEPROM write can produce a
  device nothing remote can reach. Mitigations: validate before writing, keep
  and restore backups, boot-count self-repair, and schedule brick-capable resources last.
  **The array DFU is not among them, and decision 91 does not put it back**: the agent writes array
  firmware again, but not from a resource and not inside the loop — the write is a digest-named,
  interlocked operation beside it (`ArrayFirmwareFlash`), and what the loop converges is
  `firmware.xvf3800.image`, one image carried inside the agent binary itself. So the only
  brick-capable *resources* are still the boot-partition and EEPROM writes. The array write is
  brick-capable and is governed by its own interlocks. **Decision 91's binding sequencing is
  withdrawn by decision 93**: the Safe Mode rehearsal is no longer a precondition of any flash,
  because this project no longer supports Safe Mode at all — a board that stops presenting itself
  over USB goes back to the maintainer, and the software has no recovery path for that state.
  Residual risk is covered by pre-flashed spare cards — a swap, not a flashing session.
  **One carve-out from the ordering clause (decision 46):** the display overlay and the console
  rotation are scheduled *first*, because §2.7's narration cannot exist without a lit panel and a
  write to a virtual terminal on a dark frame succeeds silently — measured on `/dev/tty1`, and
  unchanged by the stage's move to `/dev/tty8` (decision 57), since what is missing on a dark
  frame is the framebuffer and not the terminal. They keep every other mitigation in this
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
| Reboot cost | ✅ **22.3 s** relay-on to SSH-ready, measured twice 2026-08-15 (22.3 s and ~20 s) with loss of port 22 confirmed between them. Materially cheaper than the ~57 s per resource the resource catalog's 75–80 minute budget for 79 resources implied. **Re-derived:** the catalog now budgets **~30 minutes** of reboot overhead for its 80 resources, quoting §2.7's countdown as a separate configurable term rather than folding it in. Cost was the main argument against decision 26, and the real cost is under half what was assumed. |
| `POWER_OFF_ON_HALT` | ✅ **set to 1** in this Pi's EEPROM, so `halt`/`poweroff` genuinely cut power rather than leaving the board idling. A silent frame on a live relay therefore has three explanations, not two: booting, hung, or halted and drawing nothing. |
| Own-hostname resolution | ⚠️ **stale `/etc/hosts` after a rename, 2026-08-15.** `hostnamectl set-hostname` does **not** maintain `/etc/hosts`, so `127.0.1.1` still named the shipped hostname; resolution fell through to DNS and the search domain answered `getent hosts framelink-mule` with `217.61.253.65 framelink-mule.huisman.io` — **the frame resolved its own name to a public internet address.** Anything that resolves its own name (a service bind, a certificate, an advertised media address) is pointed at a machine that is not this one. `sudo`'s "unable to resolve host" warning is the only signal, and the harness had been suppressing it as benign; it now reports it once per connection. **This, and not the cloud-init trap Appendix B used to record, is what makes `identity.hostname` worth doing correctly** — the trap did not reproduce on the same session, while this did, twice. The hostname resource must own `/etc/hosts` too. |
| Portainer API / LiveKit | ✅ verified |
| **SD card reader** | ✅ **attached 2026-08-23** — a Realtek RTS USB 3.0 CRW, enumerated and used read-only to take the full 128,177,930,240-byte image of the v1 card. Not yet used to write one. It exposes no per-card identity: a card's serial is the SD CID, which is readable from a Pi and not through this reader, so `tools/harness/cards.json` identifies cards by MBR signature and by recorded location, and every flashing gate must pin the signature rather than the reader. |

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
session and stamped `Verified <date> @ <version>`.

**Fleet synthesis:** aggressive at build, gated at release (mule + test suite), frozen at
deploy — **FrameLink's own** artifacts promote through the Fleet Manager, so a frame never
resolves a FrameLink dependency: the agent binary is served (§2.8), the product app is inside it
(§2.1) and Immich Kiosk is a pinned, checksum-verified fetch (decision 41).

**That clause used to say "frames never resolve dependencies" flatly, and that was false.** Every
`pkg.*` resource runs `apt-get install` on the frame, and apt resolves the transitive set there —
guide 5's five packages alone pull in roughly 215 of them. The frame is also left running Debian's
security-only automatic updates (Appendix B item 4), which move those packages forward with nobody
pressing anything. Both are deliberate and neither is going away: enumerating a 215-package
closure in the catalog would report drift every time Debian re-cut a dependency, and switching the
security updates off to make the sentence true would be a fleet of frames that stop receiving
security fixes in order to preserve a phrase. So the boundary is by *owner*, not by *frame*.

**What that leaves is a visibility problem, and it is answered by reporting rather than by
pinning** (decision 55). The agent reads its whole dpkg database, hashes the canonical rendering,
and sends every installed package and version on the `telemetry` channel **only when that hash has
moved** — at startup, then on `packages.reportInterval` (six hours by default). The Fleet Manager
stores each distinct set once for the whole fleet, keyed by that hash, and computes drift centrally
in two directions: against the reviewed baseline in `reference/v1-state-inventory.txt`, and across
the fleet. The fifteen packages the catalog manages additionally record a reviewed version as a
**floor**: at or above it a package is in sync however far ahead it has moved, and below it — or
absent — is ordinary drift with the ordinary consequences. A literal pin would have read a security
update as drift and, under §2.6, stopped the product until it had been undone.

**The upstream review ledger is detection, and it gates a release rather than a build**
(decision 54). `upstream-review.json` at the repository root records, for each chosen upstream
version, what this project uses, what upstream was serving the last time a person looked, when
they looked, and why they decided what they did. Eight entries: the pinned base OS image, the
pinned Immich Kiosk release, the pinned LiveKit server, the commit-pinned reSpeaker control tool,
the commit-pinned XVF3800 DFU image the fleet converges on (decisions 91 and 93 — pinned at the
commit that last touched *that file* rather than at the directory, because this filename's
sibling has already been republished with different bytes; the fallback and recovery-image
entries that stood beside it were removed with the recovery kit), the two floating NuGet
packages, and the .NET LTS band.
`dotnet run --project tools/FrameLink.Upstream -- check` asks every one of them what it is
serving now — the Raspberry Pi OS image directory, the GitHub release, the newest commit touching
a GitHub path, the NuGet version index, the .NET release channel — and reports each entry as
current, moved or unreachable.

**A probe kind is added only when an upstream genuinely cannot be watched by the kinds already
there**, and `github-path-commit` is the one case so far (decision 63). The reSpeaker tool's
repository publishes no releases and no tags at all, so `github-release` answers 404 for it
forever; registering it under a kind that could never succeed would have blocked every future
release on a permanently unreachable probe, and leaving it out would have made a real dependency
indistinguishable from one nobody has. It watches a *path*, never a branch, because the artifact
moving and the repository moving are different events and only the first one is news.

**That upstream's defects are written down rather than rediscovered every time somebody meets
them.** `reference/upstream-respeaker-xvf3800.md` is the durable record: zero releases and zero
tags in 35 commits, one firmware filename published twice with 43% of its bytes changed, no licence
file in any commit, a firmware source repository that does not exist in public and reports itself
built from a modified tree, nine images in one directory where the suffix names the departure and
the default is unnamed, a prebuilt control tool whose command map has not been rebuilt since
2025-07-04 and is therefore missing four commands the firmware has since gained, and what the three
upstream issues this repository cites actually say as against how they are usually summarised. It
carries the rules those findings force — pin by commit and digest, probe the file path, treat the
version string as a label rather than an identity, corroborate before believing a filename — and
the places where this repository's own notes claim more than they measured. Every fact in it was
re-measured against the live upstream on 2026-08-24.

**No build, test or publish invokes it, and that is the point.** An upstream publishing something
overnight must not interrupt ordinary work, so the probes live outside every automated path and a
test asserts that no build file mentions the tool. What *does* run on every build is the offline
half: the suite checks that the ledger is structurally sound and still describes the pins in
source — that the base image pin, the target framework band and every `PackageReference` agree
with it. That fails when a human changed one and forgot the other, which is a different failure
from the one the operator refused.

**Cutting a release**, therefore, is four steps and no ceremony beyond them:

1. `check` — every entry current. Unreachable counts as not current: a release waits for an
   answer rather than assuming one.
2. Anything moved gets a decision. Re-pin deliberately, or upgrade the pin and validate against
   the suite. Both end the same way, with `review <id> --seen <version> --note "<why>"`, which
   stamps the day and rewrites the one entry.
3. `dotnet run --project tests/FrameLink.Tests` — green, with nothing skipped (§7.2).
4. Build and publish the version. The commit carrying the ledger *is* the release record; there
   is no second store of what was reviewed when.

**Debian package versions are deliberately out of scope.** The apt resources are meant to move
forward on their own under security updates, and the Fleet Manager records the versions it finds
on each frame as inventory. Inventory is not a chosen version, and an entry per Debian package
would put a value that changes weekly in front of a gate that runs once per release — which is
how a gate stops being performed. The ledger has no probe kind that could answer for one, so
widening it is a deliberate edit rather than a convenience somebody reaches for.

**The base OS image is a dependency like any other, and a stricter one.** §3.9's generator builds
on `2026-06-18-raspios-trixie-arm64-lite.img`, and "everything floats" does not extend to it: the
artifact is 2.8 GB of somebody else's filesystem, it is written to a card, and it boots. So it is
pinned by URL, by the digest Raspberry Pi Ltd publishes beside the archive, by the digest of the
decompressed image and by exact byte length, all in source where a change is a reviewable diff,
and the generator verifies the file before touching it. Verified 2026-08-15 @ 2026-06-18, and that
review is now the ledger's `raspios-lite-arm64` entry, tied to the pin by a test so the two cannot
part company. Upstream publishes each image under a directory dated a day after the image itself,
so the entry's two versions differ by that day permanently rather than by accident.

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

**§3.9 makes every self-hoster a redistributor of Raspberry Pi OS**, and that is worth naming
rather than assuming. A generated image is Raspberry Pi OS with four files added, and the
redistribution is performed by each operator's own Fleet Manager, not by this project — nothing in
this repository contains a byte of it. Raspberry Pi OS is freely redistributable, and it is a
Debian derivative: the great majority is GPL/LGPL and other free licences whose source obligations
Debian and Raspberry Pi Ltd already discharge through their own archives, alongside a small set of
non-free firmware and the `raspberrypi-sys-mods` licence terms that permit redistribution of the
image as supplied. The practical consequence for FrameLink is narrow and is what the notice
records: the base image is named, its origin URL and digests are pinned (§7.1), and it is listed in
`THIRD-PARTY-NOTICES.md` as software this project neither contains nor relicenses.

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
  the screenshots are not, and re-shooting them is the expensive part — and
  [guide 7](docs/7-livekit-server.md) installs the LiveKit CLI with a `winget` line, the only
  Windows-only command left in any guide. Guide 8 carried the identical line until it stopped
  needing the CLI at all: §3.7 made the API secret internal to the Fleet Manager, so the guide's
  credential half became impossible rather than merely Windows-bound, and what replaced it — a
  soak of a real call between the household's own frames — runs entirely over SSH. The harness under
  `tools/harness/` is already portable and no operator runs it, so it is the smallest part of
  this. **Image generation is no longer part of this item** — it moved into v2 as §3.9 and
  milestone M2.5 (decision 52), and it delivers most of the rest on its own: hand someone a
  ready-to-flash file and their workstation's OS stops mattering, since flashing it needs no
  Imager customisation and no mounted boot partition. What is left here is the guides themselves —
  guide 2's thirteen Windows screenshots and guide 7's one `winget` line. ⚠ Until those are re-shot, a
  household with no Windows machine still has no supported path through the *guides*, even though
  it now has one through the product.

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
| 7 | Failure escalation | Budget → `Degraded` → notify + retry/shell choice → `Halted` — **its last rung superseded by decision 66 and its budget by decision 67**, kept here as the record of what was decided first |
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
| 32 | Image generation | v3 — **superseded by decision 52**, kept here as the record of what was decided first |
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
| 49 | Halt scope | **Superseded by decision 66, which removes `Halted` from the design outright**; kept here as the record of what was decided first, and as the evidence that the state was never coherently placed. `Halted` is **device-level, not resource-level**. One resource exhausting its escalation budget stops the loop touching *everything* on that device — including resources ordered ahead of the halted one, and across process restarts. Continuing to reboot a frame an administrator has been told about twice is the same damage under another resource's name |
| 50 | Display granularity | The display is **two resources**, panel overlay and console rotation, so a dark panel and a sideways one are different diagnoses (§2.2). The dependency runs one way only — rotation depends on the overlay, never the reverse — so a failed cosmetic rotation can never keep the panel dark or mark it `Blocked`. A sideways console is a strictly better state than a dark one, which is what makes the split affordable under decision 46's early scheduling |
| 51 | Countdown scope | The countdown applies to **drift repair, not to initial provisioning** (§2.7). It does not supersede decision 48 — that chain still decides *how long*; this decides *whether at all*. §2.7's reason for the pause is a viewer in front of a working frame reading what a repair is about to do before it takes their photos away, and initial provisioning has no viewer and no product to interrupt: the frame has never displayed anything and nobody is standing there. At decision 48's 60 s that pause costs 79 minutes across 79 resources against 29 minutes of measured reboot — three quarters of a bare provision spent waiting for nobody. So a frame that has never reached `InSync` reboots as soon as a resource is applied; once it has been green, every later repair pauses in full. The condition is durable state — first-green is written to the progress journal beside the attempt ledger (§2.1), never inferred from the link, the hub or anything else that resets at boot — and it is never cleared. `--development` is unaffected and still forces 0, which is what covers the mule *after* its first convergence, since by this decision it is then a frame that has been green. One function decides it, so reverting is one edit |
| 52 | Image generation, again | **In v2, as §3.9 and milestone M2.5. Supersedes decision 32**, which stays above as history. What deferred it was an assumed blocker that does not exist: writing into an arm64 ext4 filesystem was taken to need privilege, loop devices and emulation, and measured against the real `2026-06-18-raspios-trixie-arm64-lite.img` in a plain `debian:trixie-slim` container with no `--privileged`, no `--cap-add` and no device mapping, `debugfs` and `mtools` do the whole job and `e2fsck -fn` calls the result clean — while `mount -o loop` fails in that same container, which is what proves loopback was never involved. It is 2 MB of packages editing a file, so an amd64 Fleet Manager writes an arm64 image with no emulation at all. **Tier C — a real ~500 MB image — over the cheaper tiers**, even though decision 32's literal wording ("URL, Wi-Fi and settings pre-seeded") is already satisfied by a ~2 KB boot-partition file, because §2.8 serves the binary over versionless HTTPS and §4.3 already accepts that file. The cheaper tiers all leave the operator flashing a stock image and mounting a card; tier C is flash-and-go, and it is what makes the milestone pay for the migration phase behind it. **Tier D — one image per frame — is refused on principle**, not on cost: a per-device image is exactly where identity gets pre-seeded and decision 17 dies. The image carries the binary, the unit, the enable symlink and `control-url=`, and it carries **no token, key or adoption credential** — the request type has two fields and both are URLs. The base image becomes a pinned upstream dependency under §7.1, verified by digest before the generator touches it, and `e2fsck -fn` gates every artifact because `debugfs -R` exits 0 on failure and `debugfs mkdir` on an existing directory corrupts the filesystem while doing so. **Not delivered and named as such:** Wi-Fi seeding, which needs `custom.toml` and a wireless regulatory country to work at all, and a card flashed from a generated image and booted, which is M2.5's acceptance test |
| 53 | Provisioning pace | A fleet setting, **`provisioning.paceSeconds`, default 0** (§2.7, §3.4). Decision 51 cut a bare provision from ~108 minutes to ~30 by taking the countdown away from it, and removed with it the only thing that let a person *watch* one happen — 79 screens now paint at machine speed. This gives the watching back as an option and never as a default: at 0 the behaviour decision 51 left is exactly unchanged, and raising it inserts before each provisioning reboot the same pause `repair.countdownSeconds` inserts before a repair's. **The sibling of that setting, not a second mechanism beside it** — one function, `CountdownScope.ForReboot`, takes both durations and the durable first-green field decides which applies, so the "Reboot now" skip, the narration and the screen work on a paced provision with no new code, and reverting is still one edit. Opposite fallbacks for a mistyped value, deliberately: the countdown falls back to 60 s because a typo must not silently remove the one pause a person has to read a repair, and the pace falls back to 0 because a typo must not silently add an hour and a half to a provision nobody is watching. `--development` is unaffected and still forces 0 for both — a binary switch, not a setting |
| 54 | Upstream review ledger | **Detection at release time, never a build failure** (§7.1). §7.1 had described `upstream-review.json` as a marker test that fails the build when it resolves something newer than the last review. It was never built, and the operator changed what it should do before it was: *"I do not want the build to fail. I just want a detection and then a pin decision or an upgrade + test validation before we cut a release."* So the ledger records, per chosen version, what this project uses, what upstream was serving when somebody last looked, and why they decided what they did — five entries covering the pinned base OS image, the pinned Immich Kiosk release, the two floating NuGet packages and the .NET LTS band — and `tools/FrameLink.Upstream` probes all four upstreams on demand. **No build, test or publish invokes it**, which a test asserts over the build files; what runs on every build is only the offline agreement between the ledger and the pins in source, so a red test means a human changed one and forgot the other, never that somebody else shipped a release overnight. **Cutting a release** is four steps: `check` reports every entry current (unreachable counts as not current — a release waits for an answer rather than assuming one), anything moved gets re-pinned deliberately or upgraded and validated, both recorded by the same one-line `review` command; the suite is green with nothing skipped; then the version is built, and the commit carrying the ledger *is* the release record, because a one-operator project does not need a second store of what was reviewed when. **Debian package versions stay out of scope**: they are meant to move forward on their own under security updates and the Fleet Manager records what it finds as inventory, so a ledger entry per package would put a weekly value in front of a once-per-release gate — there is deliberately no probe kind that could answer for one |
| 55 | Fleet package visibility | **Report every package's version; let the Fleet Manager compute drift; pin nothing** (§7.1). The operator's frame sits behind NAT with no open ports, so Debian's security-only automatic updates (Appendix B item 4) are the one sanctioned source of package change on a live frame — which puts a literal version pin in direct collision with §2.2's level-triggered auto-correction: the pin would read a security update as drift, **downgrade the package back**, and under §2.6 stop the product until it had. So the pin is a **record, not an enforcement**. A resource asserts a package is *present*; the installed version is *reported*. The reviewed version is a **floor** — at or above it a package is in sync however far ahead it has moved, below it or absent is drift, and the repair is `apt-get install`, which moves the package *up* to whatever the archive offers rather than down to the record. **All ~930 packages are reported, not the fifteen the catalog manages**: the fifteen are what FrameLink installs, the ~930 are what a security update can move, and a frame whose `openssl` is a month behind its neighbour is exactly the fact this exists to surface. **Reported on change, never on a tick** — the agent hashes the canonical rendering and stays silent while it matches, so the steady-state cost is four `dpkg-query` runs a day and no traffic; §2.4's reboot-per-resource means every change the agent itself makes is followed by a process start, which is what makes a startup read plus a six-hourly tick sufficient. It buffers offline like every other picture on the `telemetry` channel (§4.1). **Storage is content-addressed**: one row per *distinct* set across the whole fleet, so ten converged frames share one blob, history is one ~60-byte row per actual change, and §3.5's month rolls off with an unreferenced-blob sweep behind it. **Drift is computed centrally and in two directions** — against the reviewed baseline, and across the fleet — because "these two frames differ in fourteen packages" is a question only the server can answer, and because "newer" and "older" are not the same thing: forward movement is expected, reported and never acted on; backward movement and absence are what mean something is wrong |
| 56 | Kiosk network exposure | **Accept the LAN exposure, do not firewall it, and ask upstream for a bind setting** (§3.6). Immich Kiosk on a v2 frame listens on **every interface**, and that is a regression in exposure introduced as a side effect rather than a property anybody chose: v1's `ports: "127.0.0.1:3000:3000"` was **Docker's port publishing performing the loopback restriction, not Kiosk binding itself there**, and decisions 40 and 41 removed Docker from the frame, which removed the restriction with it. Nothing inside Kiosk can put it back — v0.42.0's `main.go` starts its server with `Address: fmt.Sprintf(":%v", baseConfig.Kiosk.Port)` and its configuration struct carries a `port` field with no host or bind field at all — so the real choice was a packet filter or acceptance. **What is exposed:** a read-only slideshow endpoint rendering the household's Immich photos, reachable by anything on the same LAN. **What is not:** the internet — §3.6's and §4.1's zero-inbound-ports property belongs to the household router, not to this port, and it is unchanged. Accepted because the blast radius is one LAN hop, the content is already visible on a screen in the room, and a filter would be one more resource to converge, drift and diagnose for the life of the fleet — an ongoing cost disproportionate to that. So `kiosk.listen-address` asserts what it genuinely can, the port listening and answering on loopback, and **writes the real bind set into its observation on every pass, in sync or not**, so a wildcard binding is a reported fact rather than a silence. **Provisional on the upstream request** (Appendix B item 6): a bind setting upstream turns this back into an ordinary catalog value and closes the exposure |
| 57 | Console stage terminal | **The console stage gets a virtual terminal of its own, `/dev/tty8`, and a level-triggered handover moves the panel between it and the product's `/dev/tty1`** (§2.7). Both stages used to share `/dev/tty1`, and §2.7 called that a *reveal* rather than a switch — the console never stopped painting and labwc simply drew over it — which worked from the moment a compositor existed and not one second before. Until then the other program on `tty1` was `agetty`, and it won: measured on the frame, `agetty` and `fl-agent` both held `/dev/tty1`, the repair screen appeared for **under a second**, and the login prompt repainted over it. The fault window is exactly the provisioning hour, which is the one hour in a frame's life when §2.7's narration is all it has to say for itself; on a converged frame the fault hides completely, because labwc covers the shared console anyway. **Why eight specifically:** one is the product's and getty's; two through six are claimed on demand by `systemd-logind`'s `NAutoVTs=6`, which spawns an `autovt@` getty on any of them the moment somebody switches there, with `ReserveVT=6` holding one permanently; seven is where an X server or a display manager lands by convention. Eight is the first VT nothing else wants, and it is still inside the twelve function keys, so **Ctrl+Alt+F8** reaches the narration by hand on the occasions the agent has deliberately *not* taken the screen. **`Conflicts=getty@tty1.service` was considered and rejected.** It would have handed the console stage the panel outright, but it takes the terminal by *removing the physical login* — the recovery path §5.5's "an unbootable mule needs hands" depends on — and it would switch the console getty off on every frame as a side effect of installing the agent, which is a fleet-visible setting masquerading as a unit-file detail. The fix for two programs on one screen is to stop them sharing a screen, not to delete one of them. **The handover rule reproduces the old reveal exactly and adds the hour it never covered:** the console keeps the panel while no compositor is running and hands it back the moment one is, which is precisely when labwc used to draw over the shared console — so a converged frame behaves as before down to the second, and §2.6 is satisfied where it always was, on the browser surface rendering the same narration from the same status hub. Yielding to a live compositor is **not politeness**: a compositor whose terminal is backgrounded has an inactive `logind` session, holds no DRM master and fails every output commit, so it can neither present a page nor apply `display.dsi2-transform` — and a resource that fails on every boot is a reboot loop under §2.6. The one exception is a session §2.7's fallback rule has already condemned, which is seconds from being stopped anyway. Level-triggered per §2.2, and confirmed by reading `/sys/class/tty/tty0/active` rather than trusting `VT_ACTIVATE`'s return code, with `VT_WAITACTIVE` deliberately unused because it blocks forever on a compositor that never answers `VT_RELDISP`. **What this does not change, and the distinction is the whole point:** the *stage* moved, the *session* did not. labwc still starts from the tty1 autologin, so `boot.autologin.getty-tty1` and the `"$(tty)" = "/dev/tty1"` guard in `session.bash-profile-exec-labwc` are correct exactly as they stand, and the browser stage's teardown still stops `getty@tty1` — killing labwc alone has the getty respawn the login that execs it — only now it takes the screen before it stops anything, so the compositor dies on a terminal nobody is looking at |
| 58 | Media reachability | **A Fleet-Manager-side check that reports what this host can see of the media path, and names what it cannot** (§3.7). §3.7 splits the network exposure in two — signalling can ride Traefik as a WebSocket over TLS, WebRTC media cannot and is published directly — and nothing anywhere in the product checked the second half. A frame whose socket connects, whose token verifies and whose room is created has demonstrated that port 7880 is reachable and *nothing whatever* about UDP 50000–50059, so a household where media never arrives presents as a call bug on the frames. **The other two placements were considered and rejected.** A catalog resource cannot do it: §2.3 requires Observe → Compare → Act, and a frame that finds media blocked has no act available — the fix is on a router — so it would converge on nothing and, under §2.6, stop the product for a fault it cannot repair. A diagnostic cannot either: decision 28 fixes the allowlist at screenshot and journal tail, and a third button would be the duplicated surface §3.6 refuses. So it sits where the deployment is owned. **What it checks, all of it side-effect-free:** the signalling and TCP-media ports are read from the listening-socket table rather than connected to; the UDP range is counted for exhaustion, which is what turns into calls that connect for some participants and not others; and the address frames are told to dial is compared against the addresses this process actually holds — the one inference, and worth it, because `use_external_ip: false` (§3.7) plus this container's own /24 bridge (§3.8) is exactly the arrangement that advertises ICE candidates no frame can route to while signalling keeps working. Nothing binds, connects or sends: an earlier shape sampled the range by binding ports, which is a probe that occasionally causes the fault it looks for. **What it deliberately does not claim.** It is not proof that media works and must never be read as one — a real end-to-end test needs two participants exchanging RTP and agreeing that they did, and everything past this container's network namespace is invisible to it: published ports, host firewall, the household router, the frame's own stack. So it never gates `Ready`, which reads as certainty, and its findings are appended to the status route's plain sentences *after* that flag is decided — reporting rather than repair, the same discipline §3.7 already gives an exited child. Silence from it means nothing obvious is wrong on this side, never that a call will carry picture and sound. **The address half of this check is amended by decision 85**, which gives the deployment a way to advertise the dialled address and turns the comparison from *is this an address this host holds* into *will this address reach an ICE candidate at all* |
| 59 | Parity harness | **A facet table over the frozen capture's own sections, three kinds of difference kept apart, and an explicit ledger of the differences v2 is allowed to have** (§5.1, Mn+3). Mn+3 is graded on a frame being "mechanically equal to the frozen v1 reference", and until now nothing measured that: `tools/harness/progress.json` recorded the gap as "no state-diff harness exists yet". `tools/harness/fl.py parity` collects and `tools/FrameLink.Parity` judges. **The split is FrameLink.Upstream's, for FrameLink.Upstream's reason** (decision 54): the half that reaches a frame is Python with paramiko (CLAUDE.md §1.3) and stays outside the suite, and the half that decides what an observation *means* is a .NET project the test project references, so the whole verdict path — parsers, differ, ledger, coverage, verdict — runs inside the ordinary suite against fixtures with no hardware. **Packages are not compared by it at all.** They are handed to `PackageDrift`, the code the Fleet Manager already runs against the same 929-package baseline on every inventory report (decision 55), because a second implementation of "is this version newer" would eventually disagree with the product about some frame with nothing to catch it. **Three kinds of difference, not one, and the verdict is separate from the kind.** Present in v1 and absent here is a gap; present here and absent from v1 may be an improvement or a regression and no mechanism can tell which; a value that moved is a third thing, splitting again for versions into forward — the security update decision 55 tolerates and never acts on — and backward, which nothing legitimate does. A difference nobody has explained is a *finding*; the same difference with a recorded reason in `tools/FrameLink.Parity/expected-differences.json` is *expected*, still reported and still counted but no longer failing parity. That separation is what lets the diff converge to empty and stay there instead of stabilising at four hundred lines nobody reads. An entry matching nothing is reported as **stale** and asked to be deleted, because an excuse nobody removes is sitting ready for a regression that happens to look the same. **Coverage is reported in both directions, and the second one is the half a state diff hides by construction.** Every one of the capture's twenty-nine sections is accounted for — twenty-four compared, five declared uncovered with the reason, and a test asserting the two sets are equal so a section added to the capture cannot vanish from the comparison. Against that, every one of the catalog's seventy-nine resources is mapped to the facet holding its v1 evidence *or* to a recorded statement that the capture never took any: twenty-one of them, which no state diff can ever verify and which are exactly what the triple bar's other two rungs exist for. That map is total and a test enforces it, because without it "the diff is empty" and "the harness was not looking" are the same output. **The probes reproduce the capture's own shape rather than improving on it** — one parser reads both sides, so a v1 reader and a v2 reader cannot generate differences out of a whitespace convention — and where the capture is defective the facet says so instead of comparing anyway: `SYSTEM_DROPINS` truncates `sleep.conf` mid-file so it is compared by presence only, `ALSA_CARDS` stops at the `--- state file ---` marker the dump was cut off after, and `PIPEWIRE`, `CAMERA`, `APP_GIT`, `KEY_FILE_HASHES` and `HOME_TREE` are not compared at all. **Every probe is read-only, and all but one unprivileged.** The exception is the array's firmware version, which is a privileged USB control transfer — the catalog's own Observe for it is written with `sudo` — so it is opt-in behind `--elevate`, and a run without it reports **incomplete** and names it rather than calling a frame at parity on evidence it never looked at. Everything else is world-readable, the app's configuration included, because it is read back off the local origin over loopback rather than out of the agent's root-owned state directory. Incomplete is deliberately not the same verdict as differs: "I looked and they disagree" and "I could not look" are different answers and collapsing them is how a harness starts reporting silence as success. **It has never been run against a frame**, which is the honest state of it: the parsers are exercised against the real captured reference and the verdict against fixtures derived from it, and every probe command is an argument until a Pi answers one |
| 60 | Fleet Manager image | **A delivery image of its own under `deploy/fleet-manager/`, published Native AOT for `linux/amd64`, running unprivileged on `debian:trixie-slim`** (§3.1, §3.8, Mn+2). Deliberately a *second* Dockerfile rather than an extension of `build/Dockerfile`: that one is a **toolchain** image — the stock SDK plus the AOT prerequisites, baked once so the emulated arm64 agent build does not re-pay an `apt-get install` per run — and it produces no artifact of its own. This one is a **delivery** image and what comes out is what gets deployed; merging them would mean either shipping a 1 GB SDK to production or teaching a toolchain image to also be a runtime. What *is* shared is the part that matters: the same `clang`/`zlib1g-dev`/`binutils`/`ca-certificates` set and the same publish flags. **amd64, and it is a decision rather than a default.** The agent is arm64 because it runs on a Pi; the server does not — §3.9 already assumes an amd64 Fleet Manager writing an arm64 card image with no emulation in the path, this operator's Docker host is amd64, and the build is then native rather than the minutes of QEMU the agent pays. The RID is a build argument and `LiveKitReleasePin` carries both Linux assets, so an arm64 self-hoster is one flag away rather than unsupported. **Non-root at a fixed uid 10001**, which is what makes the data volume writable with no `chown` anywhere: Docker propagates the image directory's ownership into a *freshly created* named volume, so the number is load-bearing and renumbering it would silently lose write access to the volume holding every adopted frame. Nothing needs root — the ports are all above 1024, §3.9 measured `debugfs` and `mcopy` working with no `--privileged` and no `--cap-add`, and `livekit-server` is an ordinary process — so `cap_drop: ALL` is stated rather than assumed. **Four runtime packages and no .NET runtime**: `ca-certificates`, `e2fsprogs` and `mtools` for §3.9, and `curl` for the health check, giving 63 MB delivered. **The agent payload is a shell loop, not a `COPY`**, because `build/out` is gitignored and a clean clone has none — an image with no agent in it is a valid image that serves no update (`AgentReleaseCatalog` already answers "no release for that runtime"), whereas a `COPY` of a missing path turns "you have not built the agent yet" into "the Fleet Manager cannot be packaged". **The health check asks whether the process serves, never whether it is configured**, because §3.2 makes unconfigured a designed state and a container marked unhealthy for it would be restarted forever by the orchestrator, taking down the one page that explains why. **Log level is set in the image**: `Microsoft` at Warning, FrameLink's own categories at Information — at the framework default the health check alone writes 5,760 requests a day of four lines each, and decision 62 makes this log a delivery channel in its own right, so a log nobody can read is a channel that does not work. `build-image.sh` mirrors `build.sh`'s conventions — environment inputs, the same git-derived version with `.dirty`, meaningful exit codes — and **refuses to call a build successful until it has started the image and seen §3.2's unconfigured page name its own variable**, because §0.4 forbids asserting a state nothing checked |
| 61 | Production stack | **One Portainer stack, an `external` data volume, two Traefik routers over one service, and the media range published one-to-one** (§3.8, Mn+2). **The volume is `external: true` and that is the single most important line in the file.** Identity is the keypair (§3.3), so losing the database does not lose "some data" — it loses the *binding*, and every adopted frame reappears as a stranger in the queue. An external volume is one Compose will never create, adopt or remove, so `docker compose down -v`, a Portainer removal with volumes ticked and a mistyped stack name all leave it untouched; the cost is one `docker volume create` on first deploy. **Two routers because Authelia cannot front `/agent`** (§3.8): the GUI router carries the middleware, the device router carries none and authenticates by keypair, and both priorities are stated explicitly rather than left to Traefik's rule-length tie-break, because a fleet's connectivity should not rest on a sort order. **The UDP range is published one-to-one and the numbers are load-bearing.** LiveKit runs with `use_external_ip: false` (§3.7) inside this stack's own /24 (§3.8), so it advertises an address no frame can route to; connectivity is established outwards and completes only because the source port a frame observes is a port the host will deliver back. Remapping 50000–50059 to different host ports produces calls that connect for nobody with no error anywhere — the same shape guide 7's standalone stack used and guide 8 validated. **The advertised address is amended by decision 85**, which has LiveKit name the address frames dial rather than its own; one-to-one publishing is untouched and its reason is if anything more direct, because each advertised candidate still carries the port bound inside the container. Signalling (7880) is published directly rather than routed as `wss://`, which is what makes the LAN case work with no name and no certificate; the proxy alternative encrypts the token exchange and not the call, and belongs to Appendix B item 2. **Secrets are typed into Portainer and nowhere else.** Compose's file-backed `secrets:` was considered and refused: it requires writing the credential to a file on the host, which is exactly what repo rule §1.2 forbids, and its Swarm form needs a Swarm. Portainer re-supplies the variables on every redeploy, so rotation is "change it and redeploy" and nothing on disk holds a password. `stack.env.example` is committed as a template with every secret value empty. **`FRAMELINK_TRUSTED_PROXIES` is deliberately not defaulted**: unset, the whole internet shares one budget on the open registration path (§3.3); set too widely, the budget is forgeable by a header. **Redeploy is idempotent by construction and rollback is one edit**, which is §0.1 discharged for a deployment rather than for a command: the volume is external and untouched, the schema is `CREATE TABLE IF NOT EXISTS` throughout, the LiveKit binary is verified by digest and re-fetched only if wrong, its configuration is rewritten only if it would differ, and every frame re-receives its complete settings on reconnect. Rollback works because **the schema only ever grows** — add tables and nullable columns, never repurpose or drop one — so an older binary finds everything it knows about and never reads the rest; that rule is now written into `SqliteDatabase` as the property a future change must defend rather than a happy accident |
| 62 | Alerting | **Four conditions, level-triggered, delivered once when they open and once when they clear, over one HTTP POST** (§3.5, decision 22, Mn+2). The milestone asks for alerting and the post-mortem says what for: on 2026-07-23 a token expired, nothing watched it, and the first person to find out was a family member pressing a button. So the rule set is exactly the two failure classes that describes — *something in contact went quiet* and *something is expiring* — plus the call path being down, which is invisible from every other surface, plus a `Halted` device, which decision 49 makes the one state nothing recovers from on its own. **Nothing else, and the shortness is the design**: no metrics, no scrape endpoint, no time series, no second container. Everything a dashboard would show already streams to the console (§3.5); what did not exist was anything reaching a person who is *not* looking at it. **Level-triggered, exactly like §2.2's reconciler.** A pass computes the complete set of true conditions, compares it against the open set in SQLite, and delivers the difference — so no rule remembers anything, a restart is correct by construction, and a frame away for a fortnight produces one message rather than four thousand. Edge-triggered alerting is what produces alerts nobody can clear, and §2.2 settled that argument already. **`opened_utc` and `notified_utc` survive the upsert while the wording is refreshed**, so "out of contact for 3 hours" becomes "for 4 days" on the console without re-delivering. **A refused delivery is a retry, never a loss** — the row stays un-notified and the next pass tries again — because this is the one component whose whole reason for existing is that a signal went missing once. A clear is delivered only if the open was, since "resolved: something you never heard of" is noise. **A Home Assistant webhook rather than the `notify` service**, because a webhook needs no credential at all: the URL is the whole of it, and §3.7 has just spent a milestone reducing the number of secrets this deployment owns. It is nevertheless a plain JSON POST, so ntfy, Gotify, a mail bridge or a five-line script all work — which is how §3.5's SMTP option is honoured without an SMTP client, a From address and a TLS mode living in this container. The body is **flat and posted with an explicit `Content-Length`**, measured: `PostAsJsonAsync` sends chunked, which Home Assistant handles and a naive receiver does not — it reads zero bytes, answers 500, and the alert is reported undeliverable for a reason neither log names. **Thresholds are environment variables, not fleet settings**, because §3.4 governs values pushed *to frames* and a threshold in the database could only be changed through the console a broken server cannot serve. Thirty minutes offline is set by §2.4: a bare provision takes the socket down some eighty times in half an hour, so anything shorter alerts on a frame doing exactly what it was told. Thirty days of remaining token is set by §3.7: renewal at the last third means a frame in contact carries four months, so reaching thirty days means renewal is *not arriving* — the rule is a check on the machinery rather than a second copy of it. The call-server rule is the only one with a start-up grace, because it is the only one whose state legitimately begins as not-ready while 17 MB downloads |
| 63 | reSpeaker control tool | **Six files, pinned at a commit SHA, fetched from content-addressed URLs and verified by digest before anything is installed** (§2.1, §7.1, resource catalog open question 3). `tool.xvf-host.installed` escalated on a working frame because the catalog claimed a "pinned SHA-256 set" that existed nowhere in this repository, and the agent — correctly — refused to invent a URL rather than fabricate one (§0.4). What made the gap awkward is that **there is nothing to pin in the ordinary sense**: measured 2026-08-16, `respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY` has **zero releases and zero tags**, `GET /releases/latest` answers **404**, and the artifact is loose files on a moving default branch. A `raw.githubusercontent.com` URL carrying a full commit SHA is content-addressed, so it is immutable in exactly the way a release asset is not, and that is what makes a pin possible with no release behind it. Adopted `725f38464e73477a30aba9f5c220f1cfdc66d682`; the six files downloaded at that SHA are byte-for-byte identical to the same six at today's head, so the directory has been frozen for thirteen months while the repository around it moved. The digests are **measured rather than published**, because this publisher publishes none — which is the same weakness a `checksums.txt` has, since nobody signs one of those either. **Six files, not four**, and the correction is the sort that matters: the catalog said the binary and three sibling `.so` files, and Seeed's own `host_control/README.md` lists `dfu_cmds.yaml` and `transport_config.yaml` as required members of the same directory, so a resource verifying four of them was asserting completeness over a directory it had half looked at. Counting `.so` files — the best structural check available while no digests existed — is gone, because it is strictly weaker than hashing the set. The seventh file there, `xvf_i2c_dfu`, is deliberately not fetched: this build does USB DFU through `dfu-util` and never the I2C path, so 3.4 MB per frame would buy a claim nobody makes. **`upstream-review.json` gained a `github-path-commit` probe kind to watch this at all**, because `github-release` reads `tag_name` from an endpoint that 404s here — and an upstream that silently *cannot* be checked is worse than one that is not registered, since §7.1 makes unreachable block a release exactly as a move does. It watches `commits?path=host_control/rpi_64bit`, never the branch head, so it reports the one event worth a human's attention (upstream rebuilt the tool) rather than every unrelated push; a probe of that kind with no `path=` is refused as a structural fault. An empty commit list is reported as a **failure**, not a pass, because it means the directory was deleted or renamed, which is the most consequential thing that can happen to a pin with no release behind it. **`pkg.git` loses its last consumer** and is left in place so its removal can be one deliberate change across the numbering, the ordering table and the parity facet map. <br><br>**Two directions were rejected, and the second one is the reason this entry is long.** *Vendoring the six files into this repository* is what a future reader will reach for first, and it is **unlicensed**: the upstream repository carries **no licence file at all** — 0 of 19 blobs at the pinned commit, 0 of 51 at head — so default copyright applies and nothing grants redistribution; and the tool appears to be built from XMOS's `host_xvf_control`, whose XCORE VOCALFUSION LICENCE clause (b) forbids making the software available to a third party "on a standalone basis" while expressly permitting "distributing or using the Devices with the Software installed therein". Fetching onto a frame and running it there sits inside that permission. A public git repository is the standalone case. *Building `xvf_host` from XMOS source* is the direction that looks like the tidy engineering answer and **is the one irreversible mistake available here.** It fails on engineering grounds first: aarch64 is not an upstream-supported platform (the README lists Raspberry Pi as **32-bit** arm7l), and `libcommand_map.so` **is not built from that repository at all** — it is generated from the *firmware's* command map, needs the XMOS XTC tools and the XVF3800 firmware source, and Seeed's copy carries the GPIO commands theirs does not, including `GPO_WRITE_VALUE`, which is the one command this product actually needs. But the reason it must never be taken is the licence: **downloading that source makes this project an XMOS Licensee**, bound by clause (c) (no derivative works, no reverse-engineering) and clause (j) (create no software "substantially similar to the Software"). Those terms bind the *project*, not the download, and they would permanently foreclose the better long-term answer — implementing the XVF3800 wire protocol natively in the agent, which is recorded in `TODO.md` as someday/maybe and is the thing that would retire this fetch, make §2.1's "no supplemental program files, ever" literally true, survive Seeed deleting the repository, and turn AEC tuning into ordinary agent code. FrameLink is not an XMOS Licensee today. **Staying not one is a precondition of that work, and no convenience is worth trading it for.** <br><br>**What this does not do, stated plainly.** It does not touch §2.6's status vocabulary. The escalation that started this was disproportionate — a missing diagnostic utility halting a frame whose screen, camera, microphone and speaker all work, and `Blocked`ing five downstream resources including both playback volumes — but the fault was the resource having **no Act that could succeed**, not the ladder being wrong about a resource that cannot converge. Giving it a real Act is the fix; inventing a "not applicable" status would have weakened §2.6's absolutism, and that absolutism is why the product is trustworthy. Nothing here has run on a frame: the install path is exercised against a generated pin in the suite, and the six real digests were verified by downloading the files to a workstation |
| 64 | Repair countdown at zero | **`repair.countdownSeconds` is set to `0` as a fleet default for the alpha** (§2.7, §3.4, decisions 48, 51, 53). Decision 51 exempts *initial provisioning* from the countdown by reading the durable `FirstInSyncUtc`, and the mule has that field set — it went green against the **9-resource** catalog long before the catalog grew to 79 — so `CountdownScope.ForReboot` classified every one of its remaining provisioning reboots as drift repair and charged each one decision 48's 60 s. **Measured on the mule, both sides.** Before: 23 drift→boot pairs in the device's own event history, 80.3–82.4 s, mean **81.1 s**. After: 21 of 24 pairs at 20.0–23.0 s, mean **21.0 s**. The difference is **60.1 s per reboot**, which is decision 48's 60 s recovered exactly rather than approximately — the residual ~21 s is the reboot itself. Against the 22 reboots the frame still expected that is ~29.7 min against ~7.7 min. The three outliers, 110.4–112.7 s, are all one resource (`unit.xdg-desktop-portal.dropin-desktop`), whose Act is slow; they are not the countdown and did not move. **What this exposes and does not fix.** A frame that has been green against a catalog a tenth of its current size has not "been green" in the sense decision 51 means, so the durable condition is arguably the thing that is wrong rather than the duration. It is left alone deliberately: `FirstInSyncUtc` survives every reboot and every update by design, invalidating it when the catalog grows is a larger decision than one fleet value, and the setting reaches the same outcome today and is one `PUT` to undo. **The cost, accepted rather than overlooked:** a frame that has been green and later drifts now repairs with no pause for anybody standing in front of it, which is precisely what decision 48 put there. Accepted for the alpha because the fleet is one mule on a bench with no viewer; `provisioning.paceSeconds` (decision 53) is the setting that gives watching back when somebody wants it |
| 65 | Session-readiness gate | **One shared probe in front of every Observe whose verdict lives inside the login user's session; its absence is `Unevaluable`, never drift** (§2.3, §2.6, decision 51). Measured across ten boots: `fl-agent` runs its first reconcile pass at boot+10.0–10.6 s, the console login opens its PAM session at 10.3–10.8 s, and the user manager comes up **0.03–0.7 s after the agent's verdict on every boot** — including the one where the verdict landed after the login. Nine resources were therefore sampling a session that could not exist yet; four were measured doing it, two reached 5 of 5 attempts and escalated, and nothing was actually wrong — the agent contradicted itself in its own log, with `ScreenHandover` finding a live compositor 4.1 s after it had declared labwc absent. **This is `d275689`'s defect a second time and `9b83e81`'s audit named the family in advance**, which is the argument for one gate over five copies of that commit's settle window: the cause is one shared fact, so it is read once. **`Unevaluable` rather than a window, and the distinction is real** — a window is a guess about how long something takes, while this is the thing itself, and the loop already treats silence correctly (no attempt spent, nothing acted on, nothing rebooted, re-read `UnevaluableRecheck` later). **The reboot decision needed no change, and the code says so rather than a judgement**: an unevaluable observation returns before `ActAsync` is reached, so a merely-unsettled resource cannot request a reboot at all. Decision 64 made this bite harder — rebooting at ~10.6 s never lets a session appear — but required no undoing, because the loop stops rebooting for these resources the moment they stop claiming drift. **Two facts in the one probe**: `/run/user/<uid>`, whose absence means no session, and the bus socket inside it, whose absence is the *measured* symptom — "Failed to connect to user scope bus … No such file or directory" is what three of the four reported as drift, so a gate passing on the directory alone would have left them lying. **A uid that will not resolve reports ready**, so a misconfigured `device.user` fails towards a visible fault instead of hiding behind "not settled yet" for ever. **The durable half is never gated** where a resource has one: `session.bash-profile-exec-labwc` and `unit.xdg-desktop-portal.dropin-desktop` compare their file on every observation whatever the clock says, because wrong bytes will never start a compositor. **Two resources that run a session command are deliberately left ungated, with a test asserting it** — `wireplumber.conf.camera-monitors-disabled` and `boot.config.camera-auto-detect` already keep the probe in the observed text and the predicate on the file they own, and gating them would hide a genuinely absent config line for the first ten seconds of every boot for nothing. `CameraNodeResource`'s reserved meaning is preserved intact: the gate excludes a read that *could not be made*, never a read that failed |
| 66 | `Halted` removed | **`Escalated` is the terminal status and `Halted` is gone from the design** (§2.3, §2.5, §2.6). **Supersedes decision 49** and the last rung of decision 7, both kept above as history. The operator's reasoning is one sentence and it is sufficient: *escalated already means stopped, waiting for a human, so a second state adds nothing.* It is written out at length here because a future reader will reach for a second strike again, and there are three reasons not to. **First, it was the only state nothing recovered from on its own.** The action that cleared a halt was the same action that cleared an escalation — an operator pressing retry — so the second state opened no new recovery path and only added a way for a frame to sit in a deader condition while nobody was watching. A ladder whose bottom rung has the same exit as the rung above it is one rung too tall. **Second, it never had one coherent scope, and the contradiction was in this repository rather than in anybody's head.** §2.5 rung 4 said `Halted` was for *that device* and decision 49 made that explicit — one resource exhausting its escalations stops the loop touching everything — while the code defines `ResourceStatusKind.Halted` as a *per-item* status returned by a single resource's own failure path, with `ReconcileLoop.IsHalted` reconstituting a device-level reading from any halted ledger entry. The same word named a resource status and a device state at the same time. Removing it settles that; there was no way of picking a side that did not leave either §2.5, decision 49 or the enum wrong, and the discrepancy is recorded here rather than left unexplained in the history. **Third, decision 68 leaves the second strike guarding nothing.** Rung 4 existed to stop a frame rebooting for ever over a fault nobody had fixed; with the pass stopping at the *first* escalation the frame has already stopped, one rung earlier and for every resource at once. **Nothing is kept as a legacy value, and that was decided against the opposite argument.** The case for keeping `halted` as a dead wire token was that an older agent still emitting it would stay readable, citing the *"frozen once shipped"* comments on the vocabulary. **Nothing has shipped**: there is one frame, on a bench, running a binary built the same night, so there is no fleet, no version skew and no upgrade path to protect — and §4.2's freeze covers **the handshake envelope and the update endpoint**, not the status vocabulary, so the constraint was never engaged. A shim defending against a scenario that cannot occur costs a dead concept in the contract for ever, which every future reader has to ask about. So `ResourceStatusNames.Halted`, `LoopStateNames.Halted`, `DeviceEventKinds.Halted`, `AgentResourceStatus.Halted`, `ResourceStatusKind.Halted`, `PassResult.Halted`, the ledger's `Halted` flag, `ReconcileOptions.EscalationLimit` and the GUI's four label maps are all **gone**, and this record is the only thing left. **The genuinely frozen types are untouched** — `WireEnvelope`, the four handshake payloads and `AgentRelease` are byte-identical to their first commit. **Two consequences carried out with it:** the Fleet Manager's alert was keyed on `LoopStateNames.Halted`, so it is re-keyed onto the terminal escalation and its kind renamed `device-stopped` — leaving it would have deleted the alert the 2026-07-23 post-mortem asked for rather than changed it — and `StoppedDeviceAsync` now re-offers an escalation the server never received, because that promotion used to happen in the walk and decision 68 means the walk is never reached on a stopped frame; without it a frame that gave up during an outage would say "nobody has been told" for ever, long after somebody had |
| 67 | Attempt budget of three | **Three attempts, not five, and a retry grants a fresh three** (§2.5 rung 2, §2.7 item 5). Decision 7's budget of five was chosen against §2.7's own example sentence, "Attempt 2 of 5", which is circular — the screen showed five because the budget was five. What decides it now is the measured cost of an attempt. Decision 64 measured a drift-to-boot cycle at a mean of 21.0 s once the countdown was zeroed, so three attempts is about a minute of trying and five is about a minute and three quarters, on a fault that is usually either transient enough to clear on the second attempt or not going to clear at all. The difference between three and five is therefore not "more chances to succeed", it is forty seconds of extra card wear per resource per fault, multiplied by however many resources share a cause. **The multiplier is the real argument.** Measured on the frame the night this was decided, one 350 ms race condition shared by five resources cost **41 reboots**: five separate budgets, each spent in full, on one fault. Decision 68 removes the multiplication structurally; this decision shortens each of the terms that were being multiplied, and the two are worth keeping separate because either one alone still leaves the other's failure available. **The cost, stated rather than hidden:** a transient that genuinely needed a fourth attempt now escalates instead of clearing itself. That is accepted because under decision 68 an escalation is cheap for the *hardware* and expensive only for the *human* — the frame stops rather than thrashes — and because a retry restores a full budget, so the recovery from being wrong about this is one press |
| 68 | An escalation stops the pass | **When any resource escalates, the whole pass stops — not just that resource** (§2.5 rung 3). The frame performs no further Act and takes no further reboot on anything until a human retries; it holds the failure on screen and waits. This is not the obvious choice and it will be questioned, so the reasoning is recorded in full. **One: the remaining work is invisible.** §2.6 already stops the product for any drift, so a frame is equally unusable at 47 resources in sync and at 68. Converging the other seventy after one has given up delivers nothing to the household — it only spends reboots on a frame that is going to show a repair screen either way. **Two: the budget is per resource, so one shared cause is multiplied by however many resources share it.** Measured on the frame: one 350 ms race condition, shared by five resources, cost **41 reboots**. Stopping the pass makes that multiplication structurally impossible rather than merely bounded — the first resource to give up is the last one that spends anything. **Three: the honest cost, which is a human cost.** A first provision carrying N unrelated faults now takes N round trips through a person: fix, retry, hit the next fault, fix, retry. Nothing batches them any more. The operator accepted that knowingly, in exchange for never again watching a frame reboot dozens of times over one bug. **Stopping means stopping acting, not stopping looking.** The observation sweep the pass was already making is completed and published, because Observe is side-effect-free by §2.3's contract, and because the `Blocked(dependency)` rows behind the escalated resource are how an operator sees what is queued up behind the failure — an empty list would be less informative rather than safer, and the loop already has a one-change-per-pass rule that turns the remainder of a walk into pure observation, so this reuses an existing shape instead of inventing a second one. What must not happen is an Act, a reboot or a spent attempt after the escalation |
| 69 | A shell is always openable | **The remote shell is an ordinary operator action available at any time, never an escalation-only one** (§2.5 rung 4, §3.6). The interface may and should *suggest* opening a shell when there is an error — that is good design and it stays — but availability is not conditional on an error existing: an operator who wants to look at a healthy frame is exactly as entitled to a shell as one staring at a failure. **What was found when the current behaviour was checked rather than assumed:** it is not gated, because **it does not exist**. `ProtocolConstants.ChannelShell` is a reserved channel name in the frozen envelope with a doc comment and **no producer and no consumer anywhere in the repository** — no agent-side session, no Fleet Manager route, no GUI control, no test. §2.5 rung 3 has been offering an operator a choice between one action that was implemented in commit `97862c6` and one that was never written. So this decision is a requirement recorded ahead of the implementation rather than a change to one: when the shell is built it is reachable from the device page unconditionally, and the escalation surface links to the same action instead of owning it |
| 70 | One item at a time, and stopped never renders as running | **The frame narrates the resource it is working on with its attempt count, and a resource that has given up is rendered as stopped rather than as busy** (§2.7 items 5 and 7). The wording the operator asked for is the wording: `item x attempt 1 of 3` while working, and `item z failed after 3 tries, expected a but got b` when it has not. The delta is rendered, never re-derived — §2.5 rung 2 already requires the exact expected-versus-observed text and the attempt count to be recorded, `ResourceObservation.Delta` already produces `expected 'X', observed 'Y'`, and it already reaches the status the screen reads. **The defect this fixes is specific and it is the whole reason the operator looked.** `ReconcileLoop.PublishStatusesAsync` narrates the *worst* status in the pass, and a resource that has given up sorts worst, so its name, its attempt count and its budget were what the screen carried. `StageRenderer` then reached its `Attempt N of M` branch on nothing more than `Attempt > 0` and drew a **travelling marquee** beside it — an animation whose entire purpose is to prove that a pause is not a hang. A frame with one permanently stopped resource therefore painted `Attempt 5 of 5` next to a moving bar on every boot, for ever, which is a picture of work that was not happening and is why the frame appeared to be looping. **The rule that prevents it recurring:** only work that is actually in progress may animate. A stopped resource gets a static line, its delta, and the sentence naming who to contact — and the console and browser stages compose those sentences from one shared function, so the two surfaces cannot drift into disagreeing about whether a frame has given up |
| 71 | Pushed operator contact | **The Fleet Manager pushes the operator's name and contact details, the frame persists them, and any error screen shows them** (§2.7 item 8, §3.4). The point is the unreachable case: the moment a person standing in front of a frame most needs to know who to call is the moment the frame cannot ask anybody, so the value has to be on the frame's own disk and the read path must not touch the network. It is stored in `AgentMemory` beside the other last-known desired values (§2.1) — atomically written, surviving both the reboot every resource takes and the binary swap every update brings — and a test with no server at all asserts that a frame which has been told once still renders the sentence after a restart. **A new `Kind` and a new payload rather than two more keys in `SettingsPush`**, which is the growth path §4.2 sanctions: the settings map is the dictionary §2.2 hands to resources as *values to converge on*, and a person's telephone number is not one — keeping it typed and separate is what stops `FleetValues` serving a human contact detail to a reconciler. **The values themselves are ordinary fleet settings in the Fleet Manager's own store**, so the existing settings page edits them and no second storage shape appears; what is new is only the delivery. **It reaches adopted frames only, and that was decided against the opposite conclusion rather than by default.** The first shape sent it on the pending path too, reasoning that a frame nobody has adopted is exactly the frame whose person has nobody to ask — and it was wrong: §3.3's registration endpoint is open to the internet and answers *anything* that connects with `pending`, so that frame would have published the operator's name and telephone number to every anonymous caller who found the URL. What it would have bought is close to nothing, because §3.2 already records that the operator is usually the first person to connect a frame, so the person in front of an unadopted frame is normally the operator, who does not need to be told their own number. A pending frame gets the generic sentence instead. **Two tests already asserted the property that decided this** — `A_pending_device_is_given_an_answer_and_nothing_else` and `A_blocked_device_is_refused_and_gets_no_configuration` — and they went red against the first shape and green again against this one, which is the whole argument for their existing in that absolute form |
| 72 | Touchscreen retry, and what was actually established | **Retry is pressable at the frame on the browser stage; the console stage names the Fleet Manager instead of offering a button that does nothing** (§2.5 rung 5, §2.7 item 9). A press reuses the same budget reset as the Fleet Manager's retry — the path `97862c6` and `a95958b` built — rather than inventing a second one, so there is one reset in the agent with two callers. **The constraint was established rather than assumed, and the honest answer is a split one.** What is verified from this repository: `ITerminal` has no read of any kind, so the console stage has no input path at all today; the agent runs as `User=root`, so file permissions on `/dev/input/*` would not be the obstacle; the panel overlay is scheduled 2nd and 3rd in the catalog while the kiosk stack is 38th to 47th, so there genuinely is a long window in which the console is the only surface; and **nothing anywhere in this repository has ever captured an input device from a frame** — `reference/v1-state-inventory.txt` is the only real capture and it contains no `/proc/bus/input/devices` section, no `/dev/input` listing and no evdev evidence of any kind. What is therefore *not* established, and is not claimed: whether the Waveshare overlay instantiates a touch device node at all, what its event protocol and coordinate ranges are, and whether a compositor holding it would conflict with the agent reading it. The catalog's own `labwc.rc-xml.touch-map` entry says as much about the existing path — whether taps land on the right pixels "is only observable by a human touching the screen". **So the console stage gets no touch retry**, because shipping an evdev reader, a hit-test and a drawn button against a device nobody in this project has ever observed would be exactly the write-only optimism §2.4 exists to refuse, and a button that does not respond is worse than a sentence telling somebody where to go. **What would change the answer** is one capture from a frame with the panel attached: `/proc/bus/input/devices` plus a few seconds of `evtest` output. If a touch device is there, the console stage can grow the same affordance later and this decision is the record of what to measure first. **Nothing here has run on hardware** |
| 73 | Settings catalog drift | **Every key the settings screen suggests must be a key something actually reads, and a test now asserts it** (§3.4). The GUI's catalog is documented as a nicety — §3.4 makes settings "not a fixed list but a generic mechanism", any key can be typed, and an unrecognised one still works — which is true of a *missing* entry and quietly false of a *wrong* one. A suggested key nothing reads is worse than no suggestion: the operator types the name the interface offered, the server stores it, the push carries it, and no resource ever asks for it. Nothing fails, nothing logs, and the setting simply has no effect for ever. **Nine of the nineteen entries were in that state.** Four were near-misses of a real key — `immich.url` against the agent's `immich.serverUrl`, `audio.volume` against `audio.playbackVolume`, `slideshow.intervalSeconds` against `slideshow.interval`, `locale.timezone` against `locale.timeZone` — and five named features nothing implements: `call.autoAnswer`, `display.backlightOn`, `display.backlightOff`, `display.brightness` and `update.enabled`. **The agent's spelling wins in every case**, because it is the side that is deployed and converging against it right now; the catalog is presentation and can be corrected freely. The five orphans are replaced by keys the agent genuinely reads — `display.rotation`, `updates.osSecurityAuto`, `updates.osUpgradePolicy`, `logging.journalMaxUse`, `power.cpuGovernor`, `device.hostname`, `slideshow.url`, `slideshow.offlineMode`, `slideshow.offlineAssetCount`, `locale.keyboard`, `locale.wifiCountry`. **The guard is a text search rather than a curated list**, deliberately: the keys live as string literals beside the resource that reads them, and a third place to register them would be a third place to forget — which is the failure being guarded against. `ControlSettingsCatalogTests` searches the agent's and the server's own sources for each catalogued key, needs nothing but the repository, and gives the same verdict on a fresh clone. **What it does not do is reject an uncatalogued key**, which would be exactly the hard-coding §3.4 rules out |
| 74 | Budget changes are retroactive | **Attempt counts are clamped on read to the budget in force, the stored counters are left alone, and a budget reduction therefore applies to history as well as to the future** (§2.5, decision 67). Attempts are persisted in `/var/lib/fl-agent/reconcile-journal.json` (§2.1) because a counter that reset at every boot could never exhaust a budget; the budget is a compiled value that changed from five to three overnight. Nothing reconciled the two, and the result was measured on the frame: a resource carrying `Attempts=4, Escalations=0` is **not** caught by `HasGivenUp` — that predicate needs an escalation on the record — so it is walked, acted on, and escalates on its single next failure while the live report shows the pair **`att=5/3`**. **The escalation is correct; the pair is not.** Those are two separate findings and only the second is a defect. A resource that has already spent four attempts *should* escalate under a policy allowing three — that is the policy applied, and the reason decision 67 gave for lowering it (forty seconds of extra card wear per resource per fault, multiplied by however many resources share a cause) argues for exactly this resource, not against it. What was wrong is a frame asserting a count its own budget cannot express, which an operator reasonably reads as a counter that has run away — the same misreading decision 70 was written to end. **So the fix is `min(stored, budget)` at every read**, and the word *read* is load-bearing: `AttemptsWithin` is consulted by the ladder's comparison, by the narration, by the per-resource rows, by the escalation event and by the stopped-frame path, and by nothing that writes. **Resetting the stored counters was considered and refused.** It is the obvious alternative — zero the ledger when the budget shrinks, and every resource genuinely gets its three — and it would silently un-escalate frames whose operator has already been notified, turning a stopped frame back into a working one behind their back. That is a worse failure than an incoherent number, and it is the failure §2.6's ladder exists to make impossible. **The ledger keeps one counter, not two.** A second, unbounded "true attempts" field beside the clamped one would be a second truth about the same failure with no reader; the unbounded history already exists on the `events` channel, where §3.5 keeps a month of it and where each escalation records the count as it stood. The clamp can therefore only ever *understate* what a frame has spent, never overstate it, which is the safe direction to be wrong in. `Terminal` gained the budget field it never set, for the same reason: a row carrying attempts and no budget renders as a bare "attempt 5" with nothing to read it against. **Nothing here has run on hardware** |
| 75 | Stopping the pass is not stopping the loop | **`PassResult.Escalated` schedules another pass; only `Restarting` and `Cancelled` end the loop** (§2.5 rung 4, decisions 66 and 68). `ReconcileLoop.RunAsync` returned on `Restarting`, `Escalated` or `Cancelled`. Two of those are the process going away — the machine is going down to prove a change, or the agent is shutting down — and the third was **`Halted` under a new name**: decision 66 removed the state and the enum member that replaced it inherited its slot in the terminal pattern. For `Halted` returning was right, because it was device-level and terminal by definition. `Escalated` is **rung 3, the rung that exists so an operator presses retry and the frame tries again**, so a loop that ends on it deletes the recovery path the rung was built for. <br><br>**Measured on the frame, with the alternatives excluded rather than assumed.** Thirty-three minutes of agent log holding exactly one startup pass, then the retry lines, then silence — against a five-minute `PassInterval`. The retry arrived and *was* handled: both budget-reset lines are present and the on-disk ledger reads `attempts: 0` with `escalations: 1` preserved, mtime at the reset instant. The journal's `telemetrySequence` was still 833, identical to the server's last report, and that counter increments before the network is touched — so nothing had been produced since, which rules out a publish failure. The process was sleeping, `NRestarts=0`, socket `ESTAB`. Not wedged, not dead, not failing to publish: the loop had simply ended, while the nine other loops `AgentHost` awaits together kept the agent alive. **So the Fleet Manager reported the device online and permanently inert, with a retry button that visibly did nothing.** <br><br>**The consequence compounded through `StoppedDeviceAsync`, which runs before anything is observed.** Any process starting with a given-up ledger entry returned `Escalated` at its first instruction and the loop died there, so under that build the frame had never observed a single resource — which is why it reported `0 of 79` in sync while being almost entirely configured. A reboot bought one no-op pass and died again, and **a retry alone could therefore never recover a frame**: only retry *then* reboot could, because the reset had to clear the ledger before the next process reached that check. Decision 76 takes that half. <br><br>**What is recorded here beyond the fix is why it happened: removing a terminal state left a non-terminal state sitting in the terminal position, and the position was an inline list of three enum members with nothing attached to it.** So the decision is now a named predicate, `EndsTheLoop`, carrying this reasoning where the next such edit will read it. **The suite was fully green at 1127 with this in it**, because every existing test drives `RunPassAsync` one pass at a time — which is what makes the ladder assertable and is exactly why nothing ever asked whether there would *be* a next pass. `AgentLoopLifetimeTests` drives `RunAsync` itself against a manual clock and asserts the three things that were false: that the loop does not return on its own, that a retry pressed afterwards is acted on, and that a stopped frame keeps publishing. All three fail against the old predicate. **Nothing here has run on hardware** |
| 76 | A stopped frame reports what it is | **The walk keeps going after an escalation, observing everything and acting on nothing, and the fabricated census is deleted** (§2.5 rung 4, §2.6, decision 68). Decision 68 already said this in words — *"stopping means stopping acting, not stopping looking… the loop already has a one-change-per-pass rule that turns the remainder of a walk into pure observation, so this reuses an existing shape instead of inventing a second one"* — and the implementation did the opposite: it returned at the first escalation and then filled the rest of the catalog in from the graph, labelling **every** unreached resource `Blocked` with the escalated resource as its `blockedBy`. <br><br>**Measured in the live payload from the frame**, all 77 remaining rows carried `blockedBy=tool.xvf-host.installed`, including `boot.config.dtoverlay-waveshare-panel`, which had been `InSync` since M2 and has no dependency on it whatsoever, and `agent.version`, `boot.cmdline.fbcon-rotate` and `agent.keypair`. The device reported **0 of 79 in sync** while being almost entirely configured. That is two fabrications, not one: a dependency the DAG does not contain, and a census of a pass presented as a census of a frame. <br><br>**Observing instead of inventing answers both, and needs no new mechanism.** The stop is applied by starting the walk with its one change already spent, so every branch is the branch it always was: `Blocker` computes the DAG-true `Blocked(dependency)` for anything genuinely downstream of the failure — because that resource is not `InSync` — and everything else reports what it observed. Nothing is carried forward from an earlier pass and nothing has to be marked stale, because every row is established by looking, this pass, at this frame. **A backoff is skipped on a stopped frame** rather than reported: *"trying again in 30 s"* is a promise nothing will keep, and the resource is observed instead. <br><br>**The alternative was considered and is strictly worse.** Retaining each resource's last-known verdict with the time it was established — carried in the ledger, marked visibly stale — would also stop a working frame rendering as a broken one, and it needs a durable per-resource verdict, two new wire fields, a staleness rule and a rendering convention, all to reproduce information the frame can simply go and read. Observe is side-effect-free by §2.3's contract, which is the same guarantee decision 68 already leans on, so the sweep costs a stopped frame exactly what it costs a converged one: one pass every five minutes, no attempt spent, nothing written, no reboot. <br><br>**Two consequences carried out with it.** `StoppedDeviceAsync` and `Complete` are gone; what they did that still had to happen — re-offering an escalation the Fleet Manager never received, and naming every resource that gave up rather than only the first — the walk now does, and a ledger entry for a resource this build's catalog no longer contains is still reported, because it still holds the frame stopped. And `PublishStatusesAsync` now asks explicitly for the resource that gave up rather than taking the highest enum value: `Blocked` is declared after `Degraded`, so a frame that gave up while its server was unreachable headlined one of the blocked rows behind it and lost the name, the attempt count and §2.7 item 7's *"has anybody been told"* sentence. Decision 70 states that a resource which has given up sorts worst; it now does. **What is not changed is §2.10's interlock**, which still holds every `Blocked` and `Progressing` row against supervision — on a stopped frame that set is now the genuine one rather than the whole catalog, which is strictly better, but whether a frame that has stopped reconciling should hold anything at all is a §2.10 question and is left alone rather than answered here. **Nothing here has run on hardware** |
| 77 | Touch retry on the console stage | **The console stage reads the panel's evdev node directly and takes a three-second hold anywhere on the screen; the false sentence decision 72 shipped is corrected** (§2.5 rung 5, §2.7 item 9). **Supersedes decision 72's console half and keeps its browser half unchanged.** Decision 72 was honest about what it did not know — *"nothing anywhere in this repository has ever captured an input device from a frame"* — and named the one capture that would settle it. That capture has been taken. The panel exposes `Goodix Capacitive TouchScreen` on `/dev/input/event4`, stable alias `/dev/input/by-path/platform-1f00080000.i2c-event`, with `PROP=2` (`INPUT_PROP_DIRECT`, so a touchscreen and not a touchpad), `EV=b` (`SYN`\|`KEY`\|`ABS`), `BTN_TOUCH` in its key bitmap, `ABS_X`, `ABS_Y` and the multitouch position and slot axes, `ID_INPUT_TOUCHSCREEN=1` from udev, a node that opens read-only cleanly and answers `EAGAIN` when idle, and an agent that runs as root. So *"This screen has no buttons"* was a claim about hardware, printed on a screen a person may well be touching while they read it — §2.7's own failure mode, pointing the other way. <br><br>**A hold rather than a button, and the reason is coordinates that nothing here observes.** The console paints a character grid on a framebuffer `boot.cmdline.fbcon-rotate` turns through 90°, while the digitiser reports positions in the panel's own unrotated pixels; hit-testing a drawn rectangle against that needs the console font's cell size and the rotation, and this repository measures neither. A button that appears in one place and answers in another is worse than no button. A hold needs no coordinates at all — only `BTN_TOUCH` going down and coming up. **A hold rather than a tap** because the screen is at eye level in a living room: a brush past the frame or somebody wiping it clean would otherwise start a reboot. The hold is counted down on screen while the finger is down, because three seconds of nothing is how a person concludes the frame is dead and lets go — and that indicator is **not** an exception to decision 70, which forbids animating work that is not happening: what moves is the person's own finger, it is determinate, it is measured against the instant being rendered rather than a tick counter, and it is gone the moment they lift. <br><br>**Found by capability, not by path.** Neither `event4` nor the by-path name is hard-coded: the event number moves with probe order and the path is one board's I²C address. The kernel publishes what each device can do, so the discovery reads `/proc/bus/input/devices` and takes the device with `INPUT_PROP_DIRECT`, absolute axes and `BTN_TOUCH` — which on the captured frame excludes the reSpeaker array's three input devices and both HDMI CEC receivers, all of which have keys. The bitmaps are printed **most significant word first**, which is the one thing about that format easy to get backwards and which fails silently when it is; a test asserts `BTN_TOUCH` at bit 330 in the leftmost of six words, against the real capture. **`evtest` and `libinput` are not installed on a frame** — measured — and neither becomes a dependency, because §2.7's console stage exists to work with none. The node is opened **non-blocking** and drained on a 50 ms poll rather than read blocking: a blocking read on an idle digitiser parks a thread until somebody touches the screen and nothing short of a touch releases it. <br><br>**What is deliberately not depended on:** the axes' ranges. The brief that commissioned this reports `ABS_X 0–799` and `ABS_Y 0–1279`, exactly the panel's own 800×1280, and that measurement is **not reproducible from the artifacts on this workstation** — the script that would have taken it left no output file. Nothing here reads a coordinate, so nothing has to be true about them, and the claim is not repeated as established. **One reset, three callers**: the Fleet Manager's retry, the browser stage's button and a completed hold all call `ResetExhaustedBudgets`, so a press at the frame and a press two hundred kilometres away cannot come to mean different things. **Nothing here has run on hardware:** the discovery is exercised against the real captured `/proc/bus/input/devices`, the hold against a fake digitiser, and no evdev node is opened by any test |
| 78 | Conflict drift is remembered | **The loop remembers that a resource has converged and been undone, three consecutive reversions is §2.6's conflict drift, and a resource in that state is not acted on again** (§2.6, §2.5 rung 2, decisions 67, 68 and 74). §2.6 has named conflict drift — *"a change that keeps returning after correction (something is actively fighting the desired state)… treated as maximally serious"* — since the specification was written, and nothing implemented it. <br><br>**What is measured, on `audio.mixer.pcm0-playback-volume`.** The resource sets `PCM,0=60` and reboots to prove it; the observed value afterwards is `Front Left=37 -23.00dB on, Front Right=37 -23.00dB on [wireplumber active, 1 stored device files]`. Roughly **25 reboots in eleven minutes**, and then — measured later the same night, at `seq=1102`, `inSync=74/80` — `escalated att=3/3 esc=1` on that resource, with the cycle stopped on its own. `repair.countdownSeconds` was raised from 0 to 120 partway through, so **wall-clock attribution is confounded** and no claim is made from elapsed time. The reboot *count* is unaffected by that setting, which pauses before a reboot rather than changing anything after one. <br><br>**What the accumulation mechanism actually is, established by reading the loop rather than from either account of the symptom.** On a frame the reboot kills the process, so the post-boot verify runs in the *next* process, in `ResumePendingAsync`, at boot+10.0–10.6 s — and decision 65 measured the login session's user manager coming up **0.03–0.7 s after that verdict, on every boot, including one where the verdict landed after the login**. So every verify is a race. **A verify that wins reads the value the agent wrote, passes, and clears the ledger — attempts back to zero, however wrong the value becomes a fraction of a second later. A verify that loses reads the value the session has already put back, and spends an attempt.** The budget is therefore only ever exhausted by **three *consecutive* lost races**, and one won race in the middle sends the counter back to nothing. That is why the counter looked stuck at `1/3` for a long time and then escalated: both observations are the same mechanism sampled at different points, and neither account of the symptom was the mechanism. <br><br>**The arithmetic reproduces the measured count with one free parameter.** Expected trials to see three consecutive events of probability *q* is `1/q + 1/q² + 1/q³`; at *q* = 0.4 that is **24.4 reboots**, against ~25 measured. *q* is **fitted, not measured** — the only independent evidence for it is decision 65's 0.03–0.7 s margin, which is consistent with a near-even race. What the fit establishes is not the number but the *shape*: time-to-stop is a geometric tail, unbounded on the right and swinging by an order of magnitude on a sub-second margin nobody controls. A frame that stops after 25 reboots and a frame that stops after 250 are the same code. <br><br>**So the loop remembers a second counter, with a different lifetime from the attempt counter, and that is the whole of the fix.** Attempts are about a repair that *never worked* and must clear on success — decision 74 settled that and it is untouched. **Reversions** are about a repair that *did* work and did not last, and must survive success. One counter cannot be both: keeping attempts across a passing verify — the cheaper alternative, and the one considered first — would make a resource that legitimately converged on its third attempt escalate on its next unrelated failure for ever, which is exactly the incoherence decision 74 removed. A reversion is counted at all three places a drift can be discovered — the post-boot verify, the in-process verify, and the ordinary walk — because which of them finds it is the coin flip above. <br><br>**Three, and the discriminator is what makes three safe.** A reversion requires the *expectation to be unchanged*, so a desired value pushed from the Fleet Manager — §2.6's other kind of conflict drift — is excluded before the count is reached, and an operator tuning `audio.playbackVolume` cannot stop their own frame. What is left is genuine drift, which §2.2 makes the ordinary job: the first must be repaired silently and so must the second, because a package postinst can plausibly rewrite `config.txt` twice in one unattended-upgrade night. The third cannot plausibly be a coincidence, and it is cheap — decision 64 measured a drift-to-boot cycle at a mean of 21.0 s, so three is about a minute. **A value that holds for one drift-detection interval forgives the reversions before it**, which makes the counter a run length rather than a lifetime total; without that a frame would accumulate unrelated legitimate repairs over months and eventually escalate for nothing. **A supervision window (§2.10) is never a reversion** — it is the one drift whose cause the frame already knows. <br><br>**The bound this buys is a proof rather than an average.** Every cycle ends in a verify that either won or lost; a loss advances the attempt counter and a win advances the reversion counter; and neither is reset by the other's advance, because a win clears attempts and leaves reversions while a loss touches neither. An adversary scheduling the race can therefore spend at most two consecutive losses without escalating on the ladder and at most three wins without escalating on this rule, so the worst schedule available to it is lose–lose–win, three times over: **at most nine reboots, guaranteed, against a geometric tail**. In the measured shape — the verify usually wins — it is **three**. A test drives that adversarial schedule and asserts the bound. <br><br>**Recovery is unchanged and is one press.** A retry clears the reversions with the attempts, and it has to: a resource carrying its conflict count into the next pass would re-escalate before it was ever acted on, which is decision 75's failure in a new place. **Nothing here has run on hardware:** the pre-decision behaviour is kept executable in the suite — the same scenario with the rule switched off, asserting the measured `att=1/3` signature and thirty reboots — and every race schedule is driven through the shipping loop against a manual clock |
| 79 | A device-level reboot floor | **Past 120 reboots in a rolling six hours the frame stops rebooting and says so, whatever the per-resource state claims** (§2.4). The operator proposed something of this shape and was steered away from it, towards decision 68's *stopping the pass on escalation*, on the reasoning that an escalation already stops the frame. **That was wrong, and decision 78's mechanism is why:** decision 68 fires when a resource *fails*, and the cycle this bounds is made of *successes* — the apply works, the verify passes, the ledger clears — so there is nothing for a failure-triggered protection to trigger on. Decision 78 is the diagnosis and it is strict; this is the floor and it is dumb, and the two are worth having separately because either alone leaves the other's failure available. <br><br>**It shares no state with §2.5's ladder — it counts reboots.** A durable list of when the recent ones were requested, in the same journal as everything else that has to survive the event it is counting, pruned to the window on every write. It sits in front of the reboot boundary rather than inside the loop, so it holds for every caller and needs to know nothing about resources, attempts or escalations. <br><br>**The rates do not separate, which is why the bound is a total inside a window rather than a frequency.** A bare provision of the 80-resource catalog is 80 reboots at decision 64's measured 21.0 s mean — ~30 minutes, **2.6 reboots per minute**; the measured livelock ran ~25 reboots in eleven minutes, **2.3 per minute**. Any frequency bound tight enough to catch the second stops the first. A total inside a window long enough to contain a whole provision separates them cleanly, because a provision ends and a livelock does not. **120** leaves 40 reboots of headroom over the catalog's 80 — enough for half of it to need a second attempt — and a test asserts the shipped number against a full 80-resource provision rather than against a comment. **Six hours** contains any legitimate provision with better than 2× margin: 30 minutes measured, and 2.4 hours even if every resource were as slow as the slowest Act anybody has measured on the frame (110–112 s, `unit.xdg-desktop-portal.dropin-desktop`). <br><br>**A refusal is an ordinary refused reboot**, which is already a first-class outcome: the change is written and cannot be proven, so it spends an attempt and reaches a person on §2.5's ordinary schedule — at no further cost in reboots, because none of them happen. What travels with it is a whole sentence, because that string becomes the delta on the frame's own screen and in the operator's notification. **A retry grants a fresh window**, on decision 67's reasoning that a person has arrived; without it the retry would be visibly powerless on exactly the frame it was pressed for. <br><br>**It fails open, deliberately.** A clock that has jumped — a Pi up before NTP answered — can only make it *forget* reboots, never invent them, because entries dated in the future are dropped along with entries older than the window. A floor that broke a provision would be worse than no floor at all, and decision 78 is the mechanism that is allowed to be strict. **Nothing here has run on hardware** |
| 80 | The mixer has a second owner, and both halves of that are fixed | **WirePlumber is confirmed as a second owner of the ALSA mixer; every `audio.mixer.*` Observe now sits behind the session gate, and `audio.wireplumber.playback-volume` owns the value at the layer that sets it** (§2.4, §2.6, decisions 65 and 78; resource-catalog suspected-revert item 4). <br><br>**What is measured.** `audio.mixer.pcm0-playback-volume` set `PCM,0=60`, rebooted, verified — and the value read afterwards was `Front Left=37 -23.00dB on, Front Right=37 -23.00dB on [wireplumber active, 1 stored device files]`. The catalog had carried this as *suspected* since it was written, reasoned from documented upstream behaviour plus a boot ordering; it is now the list's second confirmed revert. Three of its siblings — hostname, timezone, locale — were disproved by measurement, so the entry's *mechanism* was not adopted on the strength of its conclusion being right. <br><br>**37 is a number, not noise, and that is what identifies the mechanism.** This control is **one step per decibel**, which three independent readings agree on: 60 = 0.00 dB in the v1 inventory, 40 = the −20 dB `PCM,1` ships at, and 37 = −23.00 dB here. So 37 is a *requested gain of −23 dB* by something that is not the agent. WirePlumber 0.5's own default sink volume — `device.routes.default-sink-volume` = `0.064` linear — is 20·log₁₀(0.064) = **−23.88 dB**, whose nearest representable step at or above is exactly **37**. **This is arithmetic on a documented constant and is labelled a hypothesis, not a measurement**; it is kept executable as a test so a future reader can re-derive it rather than take it on trust. What it buys is a *falsifiable* reading of the mechanism, which the catalog's original one — "WirePlumber restores its stored per-device volume" — did not have. <br><br>**If the hypothesis is right, the catalog's proposed fix repairs nothing.** Owning `~/.local/state/wireplumber/` corrects a *stored* value, and on this reading there is no stored route volume to correct — the frame is getting a *default* applied to a route WirePlumber has never saved one for. The frame's own report of "1 stored device file" is consistent with a directory holding a profile or default-node record and nothing about volume. **So the agent now names the files rather than counting them**, on every mixer observation: that is the one read-only reading which separates the two mechanisms, `tools/harness/progress.json` has carried "list that directory on the mule" as a blocked next action since the catalog was written, and nobody has been able to take it. It now arrives in ordinary telemetry without anybody touching the frame. <br><br>**The fix is chosen to be correct under both readings, because the mechanism cannot be settled from a desk.** `audio.wireplumber.playback-volume` sets the volume *through* `wpctl`, which overrides a default **and** is what makes `restore-device` persist a stored value — so whichever mechanism is really in play ends up agreeing with the frame. **`wpctl` rather than a configuration fragment** was decided on blast radius: a `~/.config/wireplumber/wireplumber.conf.d/` file setting the default to unity, or `api.alsa.soft-mixer` taking the hardware mixer away from WirePlumber entirely, are both plausible, neither is testable here, and a malformed fragment stops WirePlumber starting — which takes `wireplumber.conf.camera-monitors-disabled` down with it and leaves the frame with no audio at all. A refused `wpctl` call is ordinary visible drift that escalates. <br><br>**It does not replace the ALSA resources and could not.** `PCM,1` is a second hardware gain stage that no PipeWire route volume reaches — the −20 dB stage guide 4's loudness fix found, and `TODO.md` records that the fix required **both** controls — so the hardware mixer stays agent-owned whatever WirePlumber does. The two owners are made to *want the same thing* instead: both derive from `audio.playbackVolume`, and they are compared in **decibels with a half-step (0.5 dB) tolerance**, which is the quantisation they share. An exact compare would report permanent false drift the moment either side rounded, and two copies of one desired value are two things that can disagree — which is the fault this whole entry is about. `audio.alsa.stored-state` gains the new resource as a dependency, because `alsactl store` records whatever is live and must not run until both owners have written. <br><br>**The second half is timing, and it is the half that made this invisible.** The post-boot verify runs at boot+10.0–10.6 s and the login user's manager comes up 0.03–0.7 s later (decision 65's measurement), so an ungated mixer verify was a **coin flip on whether it read the agent's value or WirePlumber's** — and a verify that won *passed*, cleared the ledger, and left a frame about to be wrong again looking entirely healthy. §2.4 claims "applied" only from an observation the setting had to survive a boot for, and a reading taken before the other owner of that value has started is not that observation. So all five `audio.mixer.*` resources are now behind `UserSessionGate`. <br><br>**This reverses a remark that was in the code, and the remark was wrong on its own terms.** `SessionAudio` argued that a resource refusing to conclude until the session was up "would act — and therefore reboot — on a frame whose session is broken", which decision 65 had already disproved in writing: an unevaluable observation returns before `ActAsync` is ever reached, so it cannot request a reboot. The paragraph is rewritten rather than left standing beside its own contradiction. The distinction decision 65 drew is preserved intact: *is there a session to ask* gates, and *is WirePlumber running inside it* remains evidence in the observed text, because a frame whose session is up and whose WirePlumber is stopped has a mixer value that is exactly as true as any other. <br><br>**An untested prediction, recorded so the next hardware run tests it:** if what moved was a PipeWire route volume, then only `PCM,0` reverts and `PCM,1` stays where the agent put it. The measured delta names `PCM,0` only, which is consistent with that and does not establish it. **Nothing here has run on hardware:** the gate is exercised against a fake session, the `wpctl` paths against scripted output, the step scale against the three real readings, and the falsification — the mixer reporting **in sync** on a frame whose session has not started — was run against the pre-decision code and observed failing |
| 81 | The stall that nothing announced, and the rescue that ran too late | **Three defects found by one measured stall on the frame: a journal that could not be read after an upgrade, a host that could not see its own loop die, and decision 80's rescue ordered behind the resource it rescues** (§2.4, §2.6, decisions 68, 79 and 80). <br><br>**The measurement.** The frame sat at `loop_state=awaiting-reboot`, report sequence 1112, for twenty-nine minutes: online, socket up, Fleet Manager reporting it green-adjacent and healthy, the console stage still painting, and not one further report. The leading suspicion was that decision 79's floor had declined a reboot and stranded the loop in a state only a reboot can leave. **It had not, and the journal proved it twice over:** the floor's ledger read `reboots: []`, and the journal file's mtime was identical to the pending-apply write that *precedes* the reboot request — so the floor's record-the-reboot write had never run and its refusal branch had never been taken. The floor's refusal is not silent; it logs, and there was no log. What settled it was restarting the service and reading what the dying process printed: `System.ArgumentNullException: Value cannot be null. (Parameter 'reboots') at RebootFloor.Within(…) at RebootFloor.CrossAsync(…)`. <br><br>**Defect one: an upgrade seam that could not heal.** The frame carried a journal written before decision 79 existed, so it had no `reboots` key. An absent key deserialises to **null**, not to the empty list the property's initialiser appears to promise — and `DefaultIgnoreCondition = WhenWritingNull` then omitted it again on every subsequent write, so the omission survived every rewrite the new build made. Every reboot request on such a frame died. The fix normalises **every** list on read rather than the one that broke, because the next field added to that record inherits the identical defect. The floor's own tests could not have caught it: they all start from a journal this build wrote. <br><br>**Defect two, and it is the one worth keeping.** The exception did not crash the agent and did not reach a person for twenty-nine minutes. `AgentHost` awaits its ten loops with `Task.WhenAll`, which surfaces nothing until *every* task has finished — and the other nine run for the life of the frame. So the fault sat inside a completed task with nothing waiting to read it, and was finally printed by a shutdown a person triggered by hand. **This is the same shape as decision 75's escalation defect** (a completed `RunAsync`, a live process, a live socket, a server still reporting the device online); that one was fixed in the loop, and this is the host-level half of the identical hole. A first-completion wait would be wrong — `screen` returns by design on every frame the moment it learns there are no consoles to switch between — so the trigger is a **fault and only a fault**: a loop that throws cancels the other nine so they unwind through their ordinary shutdown paths, and the original exception comes out of the `WhenAll` it was always going to come out of, into `Restart=always`. <br><br>**Defect three: decision 80's edge set was inverted.** `audio.wireplumber.playback-volume` exists to stop WirePlumber holding `PCM,0` at −23 dB, and it was declared **depending on** `audio.mixer.pcm0-playback-volume` — so it could not be acted on until that resource was `InSync`, which is precisely the state it exists to make reachable. Measured: `PCM,0` spent all three attempts, escalated, the escalation stopped the pass (decision 68), and the rescue never executed once. Applying the catalog's own dependency test — *would this resource have to guess?* — neither mixer edge was ever warranted: `wpctl` reads and writes WirePlumber's sink and touches no ALSA control, and the desired level comes from the shared `audio.playbackVolume` setting rather than from a sibling's converged state. The real edge runs the other way and now exists. **`PCM,1` takes no such edge** — it is a second hardware stage no route volume reaches and was never observed away from 60, which is decision 80's own untested prediction holding so far. <br><br>**And decision 80's blocked question is closed by measurement.** `~/.local/state/wireplumber/` on the frame holds exactly one file, `stream-properties` — no `restore-stream`, no `default-routes`, no `default-nodes`. WirePlumber is applying a **default**, not restoring a stored value, so the catalog's original proposed fix (owning that directory) repairs nothing and must not be implemented. Decision 80 chose `wpctl` precisely so it would be right under either reading; it is right under the one that turned out to be true. <br><br>**Measured on hardware:** the stall, the journal state, the stack trace and the WirePlumber directory listing. The two loop fixes are covered by tests that were run against the pre-fix code and observed failing |
| 82 | `Reconciling` is a field, not a rung | **`DeviceState.Reconciling` is removed; §2.6's row of that name is carried as `AgentStatus.Drifted` beside the ladder, and the ladder's five remaining rungs are asserted against their producers** (§2.6, decisions 66 and 70). The member existed from the first cut of the ladder and **nothing ever set it.** `DeviceStateLadder` is the only thing that constructs a `DeviceCondition`, and it resolves exactly two inputs — a handshake outcome and silence — neither of which can yield it; the two static conditions, `Starting` and `Remembered`, are `NoContact` and `InSync`. `StagePalette` nonetheless gave it an accent, so the console carried an amber nothing could ever paint with. <br><br>**Wiring it up was the other option and it is not available, for a reason already written down.** `AgentStatus.Drifted` states it: *"It is a separate field from `Condition` and not a rung on it. The ladder is about what the Fleet Manager has said; this is about what the frame has observed of itself, and a frame can be authoritatively adopted and locally drifted at the same instant."* The two axes are orthogonal, not ordered. A frame whose server has gone silent while it was green **and** which is repairing a resource is both `NoContact` and reconciling; one enum field can hold one of those, and whichever it held it would be suppressing the other. That is the §1.2.3 failure — a state that is not named — arrived at from the opposite direction. <br><br>**Nothing is lost by removing it, because the row's behaviour was never coming from the enum.** *Product runs? No* is `ProductRuns => Condition.ProductRuns && !Drifted`, and the narrated repair screen is `ReconcileVoice.Headline`/`Detail`, composed from both halves since `c3116bc`. The row stays in §2.6's table because it describes what a frame does; what changes is that the table now says which of its six rows is a rung and which is a field. <br><br>**One consequence is named rather than smoothed over.** `StagePalette.For` takes a `DeviceCondition`, so a frame the Fleet Manager has cleared and that is repairing itself keeps the **green** accent while its headline says it is fixing something. That was already true — the amber arm was unreachable — so this changes no pixel, but it is the one thing the dead member looked like it was doing. Painting it amber means composing the accent the way the headline is composed, which is a change to what the screen does, and is left for the operator to ask for rather than smuggled in with a deletion. <br><br>**The pin is the enum against its producers, not against a list.** `Every_rung_the_ladder_declares_is_one_something_can_actually_reach` builds the reachable set from every `HandshakeStatus`, an unknown status, silence, `Starting` and `Remembered`, and asserts set equality with `Enum.GetValues<DeviceState>()` — so a member added without a path to it turns the suite red. Measured: re-adding `Reconciling` fails it. **Nothing here has run on hardware**, and nothing here needs to |
| 83 | The accent is composed the way the headline is | **`StagePalette.For` takes an `AgentStatus` and switches on `ReconcileVoice.Voice`; the rung-only mapping is private and the browser stage is sent the same value by name** (§2.6, §2.7, decisions 70 and 82). This is the third finding in one family and the third rendering to make the same false claim. Decision 70 took the animation off a frame that had stopped; `c3116bc` took the words off one that was headlining *"Everything is working"* over its own failed resource; this takes the colour off both. A frame the Fleet Manager had cleared and that was repairing itself kept the **green** accent — the box title, the spinner beside the headline, every progress bar under it — while the headline said it was being put right, and a frame that had *given up* kept it too, beside a still red glyph and a red stopped bar the same screen was already painting. <br><br>**Decision 82 named this and declined to fix it, correctly.** It surfaced while `DeviceState.Reconciling` was being removed, and painting it amber changes what the screen does rather than deleting a dead branch, so it was left for the operator to ask for. It is pre-existing and not a regression of that change: the amber arm had never been reachable. <br><br>**The fix is a shape, not an arm.** A second switch mapping status to colour beside the switch mapping status to words is how they came apart in the first place, so the conjunction §2.6 specifies is made exactly once — `ReconcileVoice.Voice` returns `Ladder`, `Repairing` or `Stopped` — and `Headline`, `Detail` and the accent are all switches over its answer. `StagePalette.For(DeviceCondition)` is **gone from the public surface**: a caller able to ask for the rung's colour alone is a caller able to reintroduce this without noticing. The two cases where the rung is still right are unchanged and are the same two the headline keeps — a frame with nothing wrong on it stays green, and one the Fleet Manager has not cleared keeps the blue or red that says *adopt this frame* or *it has been blocked*, which is more actionable than *it is fixing itself*. <br><br>**Both stages, because a fix on one is half a fix.** The page had no accent at all before this, so nothing green was on the panel — but the only field it could have painted one from was `StageMessage.Condition`, which says `InSync` for a frame that is repairing itself, so the defect was waiting there rather than absent. The composed accent now travels as a name, the page looks it up and draws it beside the headline as a still dot — a dot and not a spinner, because decision 70 forbids anything on that screen suggesting motion that is not happening — and an unrecognised name falls through to the ordinary headline colour rather than to a guess. <br><br>**The pin is a painted frame, not a function call.** `The_accent_says_what_the_headline_says_rather_than_what_the_ladder_says` renders the console with colour on and asserts the escape sequences that are and are not in it, which is what made it fail against the code before the change rather than merely fail to compile. **Nothing here has run on hardware** — every assertion is over composed text and a rendered string, with no panel in the process |
| 84 | Half of every update was invisible | **A fifth supervised behaviour, `page-refresh`: the agent digests the app it serves, the page reports how old its own document is, and a page a previous agent served is told to reload itself** (§2.1, §2.8, §2.10, decisions 41 and 83). <br><br>**The measurement.** Decision 83 shipped an accent composed in the agent and drawn by `app/frame-stage.js`. After deploying it the agent *was* serving the new page — `curl http://127.0.0.1:8888/frame-stage.js` on the frame returned 12,298 bytes containing the new `ACCENTS` map — the panel showed the repairing state for 31 s and the **headline updated correctly**, because the headline is server-composed text delivered over the live channel. **The accent did not appear at all**: the capture is entirely greyscale, all twelve most-common colours `(n,n,n)`, zero saturated warm pixels. Chromium pid 1246 had run since `09:09:28Z` and `fl-agent` restarted at `10:35:10Z`, so the live document came from an agent that no longer existed. Evidence: `tools/harness/runs/20260816T103511Z-repairing-window/`. <br><br>**The general fault is larger than the accent.** §2.1 puts the app inside the binary and §2.8 replaces the binary hourly, so **every future agent update carries an `app/` the running browser will ignore.** §2.8 makes the agent self-update; this made half of each update invisible — and the visible half, anything the agent composes and sends, kept working, which is what made a broken deploy look like a good one. <br><br>**Nothing already covered it, and one thing actively masked it.** Kiosk liveness watches the *channel*, and the channel came back: the page's WebSocket reconnects on its own backoff, so the journal's “The page checked in after 0 s” is a **reconnect, not a page load** — the two are the same event at the socket layer. `unit.chromium-kiosk.running-matches-content` compares the running process's argv against the unit's `ExecStart`, and an app change moves neither, which is correct for what that resource is about. `BrowserStage`'s fallback rule re-arms on every agent start and is satisfied by the same reconnect, so it promoted a ninety-minute-old document to `Live`. The daily restart would have fixed it — up to twenty-four hours later, by accident, and only because it bounds *age*. <br><br>**Supervision, not a resource, and §2.10's own test decides it rather than taste.** *“If you can name the file, value or unit content that is wrong, it is a resource; if the honest answer is ‘nothing is wrong with the configuration, it just stopped working’, it is supervision.”* Nothing on disk is wrong here: the unit is right, the served app is right, the browser is running the command line it was given. The stale document exists only in a renderer's memory. **Two consequences make the classification load-bearing rather than clerical.** §2.6 holds that any drift stops the product, so as a resource every app change in every update would blank every frame, narrate a repair and reboot — turning §2.8's quietest mechanism into its loudest, for a difference the viewer cannot see. And §2.3 requires Observe and Verify to be the same reading *across a reboot*, which this property cannot survive: a reboot restarts the browser, which reloads the page, which makes it fresh. The resource would report in sync after its own Act whether or not the Act did anything — exactly the v1 governor shape §2.3 exists to forbid. **So the catalog count does not move: 80 resources, 81 in the graph.** <br><br>**Deciding staleness needs one fact from inside the document.** The page reports `performance.now()` — how long ago *this document* began loading — on every check-in, and the agent compares it against its own process uptime. A document younger than the agent process was served by that process, so it is running the app this binary carries, and **that build is recorded durably as what the running page loaded**. A document older than the agent process is running whatever was recorded last, and a record that disagrees with what is now served is a stale page. Recording *what the page loaded* rather than *what changed at startup* is what closes the multi-restart hole: a flag in memory would be false on the next crash or `systemctl restart`, and a page stranded two agents ago would look fine for ever. **Both sides of the comparison are monotonic**, which is not fastidiousness on a machine with no RTC — `performance.now()` counts from the navigation and `Environment.TickCount64` from system boot, so neither moves when `systemd-timesyncd` steps the clock seconds after every boot. <br><br>**The identifier is a digest of the served app, never the agent version.** §2.8 ships a new version hourly whether or not a byte of the app moved, so keying the refresh on the version would blink the product on every release — including the releases that only touch a resource. `EmbeddedApp.BuildId` hashes every embedded asset's path and bytes, so it moves when, and only when, the page a browser would load is a different page. <br><br>**The action is a reload, not a browser restart, and that is what keeps it inside §2.10's promise never to stop the product.** A unit restart tears down a renderer, a compositor connection and a GPU context and blanks the panel for seconds; `location.reload()` replaces the document in about one. It is also the **only supervision action that opens no interlock window**, because it makes no resource transiently wrong — the process, its argv, the compositor and the display transform are all untouched. What it does disturb is the local channel, for about a second, against a rule that allows ninety. <br><br>**It never reloads at a bad moment, and the guard is in two places because one of them is always slightly out of date.** The page reports `inCall` as a **level on every check-in** rather than as an edge, and the agent stands down while it is true; but that reading can be a heartbeat old, so `frame-stage.js` **refuses a reload it is in a call for, remembers the request, and takes it the moment the call ends** — the daily restart's stand-down, in the one place that cannot be out of date about itself. Three further stand-downs precede it: an unknown or fresh verdict, §2.10 clause 1 when the reconciler is working on the browser, and a five-minute cooldown so a page that ignores the command cannot become a frame reloading itself every fifteen seconds. Past the cooldown the repeats continue and the **fault rate** reports them, because §2.10's signal is a rate and never a budget. <br><br>**A latent defect was found underneath it and is fixed by the same signal.** `Supervisor.CallActive` — which §2.10's daily restart has deferred to since it was written — **had no producer.** Nothing in `AgentHost` ever set it; only tests did. On a real frame the 03:00 restart would have ended a call in progress, silently, and the rule forbidding it was a rule with nothing to read. `inCall` is that producer, and the property now believes anything that claims a call is happening rather than only its own field. <br><br>**It is inert for the update that introduces it, by construction, and that is the roll-out safety property.** An agent that has never written the record concludes nothing and reports *unknown*, so the first page this can ever reload is one loaded under a binary that already carries the reporting half — which is to say a page that reports its own call state. No page predating the guard can be interrupted by the half that acts on it. <br><br>**Falsified against code without the fix, in both halves.** Reverting only `app/frame-stage.js` and `app/frame-app.js` fails the page-side pin; neutering the acting half fails five behavioural tests — the reload, the call deferral, the verify, the cooldown-and-fault, and the daily restart's new producer. **Nothing here has run on hardware.** The frame was converged at 81 of 81 and showing photographs throughout, and was deliberately not touched; every assertion is against the local channel, a scripted state store and the embedded page text, with no browser in the process |
| 85 | Media candidates name the address frames dial | **The address in `FRAMELINK_LIVEKIT_PUBLIC_URL` is written into the generated `livekit.yaml` as `rtc.node_ip`, and `use_external_ip` stays `false` — which is now load-bearing rather than merely preferred** (§3.7, §3.8, decisions 58 and 61). The Docker cutover left the call server in exactly the shape decision 58 was written to catch. Frames are issued `ws://10.20.30.200:7880` and signalling arrives, because the port is published, so every surface that watches the WebSocket reads healthy — while the ICE candidates carried `172.18.0.2`, this container's own bridge address, because `use_external_ip: false` has LiveKit advertise what it is locally on and what it is locally on is a `/24` nobody on the LAN can route to. A call would connect and carry nothing, which is the most confusing failure available. <br><br>**The key is `node_ip`, and it is pinned to 1.13.5 three ways rather than to a blog.** The v1.13.5 config sample's own note that `use_external_ip` *"takes precedence, for this to take effect, set use_external_ip to false"*; the pinned binary's `--help`, which documents `--node-ip` as *"IP address of the current node, used to advertise to clients. Automatically determined by default"*; and the pinned binary itself, which carries the literal yaml tag `node_ip,omitempty`. The last two were re-read out of the deployed binary in the running container rather than taken on trust. <br><br>**Why `use_external_ip` must stay false, which is the part somebody will otherwise flip in good faith.** §3.7 already had a reason and it survives: external-IP discovery asks a public STUN server what this host's internet address is, which on a household LAN is both the wrong answer — frames are on the same network and need the LAN address — and a third party in the path of a call that never leaves the house. It is no longer the only reason. In the pinned `rtcconfig` the two settings interact in exactly one direction: `Validate()` **re-determines** the node address whenever external-IP discovery is on, so a configured `node_ip` survives only while `use_external_ip` is false. Switching it on to *also* advertise an address would silently discard the one configured here and restore the fault verbatim. There is no combination in which both are on and both apply, and nothing warns — the file is still valid, the server still starts, and the candidates go back to naming the bridge. <br><br>**The address is not a new setting, and that is a decision rather than an economy.** `FRAMELINK_LIVEKIT_PUBLIC_URL` already carries the operator's statement of where frames should arrive, and the media half has to arrive at the same place, so `LiveKitOptions.MediaAddress` reads the literal out of it and both halves move together by construction. A second variable would be a second thing to get wrong, silently, in exactly the way this decision is fixing. **Three inputs are refused rather than half-honoured.** A public URL naming a *host* writes no `node_ip` at all and leaves the previous behaviour in place, because resolving that name would be a network call on a status read that can hang, can answer differently than a frame's resolver does, and can produce a conclusion about a name this container merely sees differently — that is §3.7's deferred case, still deferred rather than half-implemented from a name. Loopback is nobody's dialling address, so a public URL naming it says nothing about where media should come from. And `0.0.0.0` parses, is not loopback, and is a plausible thing to write, but handing it over would produce candidates pointing at nothing while looking configured — so it is left unset and put back in front of the media check as an address frames dial that nothing will advertise. <br><br>**Decision 58's check moves with it, and decision 61's reasoning is narrowed rather than removed.** The check asked whether the dialled address was one this host *holds*, which was the right question while nothing could advertise otherwise; it now asks whether that address will reach an ICE candidate at all — held here, or named by the configuration. The container-on-a-bridge test flips from *"the silent case is no longer silent"* to *"the address it is told to advertise answers it"*, and `0.0.0.0` is the case that still fires. Publishing the media range one-to-one remains exactly as load-bearing, for a more direct reason than decision 61 gave: the candidates now name a routable address, but each one carries the port LiveKit bound *inside* the container, so a remapped range advertises a port the host will not deliver to. <br><br>**Verified against the pinned binary before anything was deployed, and against the live deployment after.** The rendered document was accepted and answered `ports` with 7880 HTTP, 7881 ICE/TCP and 50000–50059 ICE/UDP, while the one-letter misspelling `node_ipp` was refused with `field node_ipp not found in type config.RTCConfig` — which is what makes the acceptance evidence rather than a formality. On the running Fleet Manager the generated file carries `use_external_ip: false` and `node_ip: 10.20.30.200` on consecutive lines, the server logs `"nodeIP": "10.20.30.200"` where it logged `172.18.0.2` before, and `/api/livekit` answers `problems: []` — which is composed from the media check's findings as well as the options', so it is that check agreeing rather than that check being absent. <br><br>**One participant is not two, and the honest limit is unchanged.** A frame's browser joined the room and its publisher connection went active in 66 ms over UDP, with LiveKit selecting its own `udp4 host 10.20.30.200:50016` candidate — the address `node_ip` writes, on a port the stack publishes one-to-one — against the frame arriving as `172.18.0.1`, which is this Docker network's gateway rather than the frame's own address, because inbound traffic to a published port is NAT'd through it. That is the advertised candidate being reached rather than inferred, and it is one participant's transport establishing. **Two participants exchanging RTP and agreeing that they did is still the only thing that would prove media flows, and no call has been placed** |
| 86 | A call had frames in it and no people | **An operator-gated `POST /api/livekit/guest-token` mints a four-hour token for a named participant in a reserved `guest:` namespace, writes nothing down, and is the only route a person joins through** (§3.2, §3.7, decisions 58 and 85). Every call token in the fleet was minted for an adopted *device*, and `/api/devices/{id}/call-token` does not even hand one back — it writes the token into that device's settings and answers with an outcome. A family member on a phone or a laptop therefore had no way in at all: proving media flowed on 2026-08-19 needed a JWT hand-minted from the server's own configuration, which is not a thing the product supports. <br><br>**Borrowing the device route would have been worse than having nothing.** It uses the *device id* as the LiveKit identity, and `CallProvisioning` already records what a collision does — two participants sharing one identity are read by the server as one participant reconnecting, "so each kicks the other out" — so joining as a frame would end that frame's call. It also runs `ReviewAsync(force: true)`, which rotates the frame's live token and pushes settings, so it is not usable as a read either. <br><br>**Identity is the whole hazard, and it is closed structurally rather than checked.** The caller names a *person*, never an identity: the route prefixes `guest:` itself and refuses a name carrying a colon, so there is exactly one namespace a minted identity can be in. A device's identity is either its device id — Crockford Base32 and hyphens, an alphabet with no colon in it — or a string an operator put in `call.identity`, and that second case is closed from the other side: `ReviewAsync` drops a configured identity inside the namespace for the device id and writes the correction back, so the setting heals. The two halves together are why there is no runtime uniqueness check here — one that can never fire is one nobody maintains. <br><br>**Room, lifetime and gating.** *Room:* naming one is optional and validated against the rooms the fleet actually puts frames in, because the generated `livekit.yaml` sets `room.auto_create: true` — a mistyped room is not an error anywhere, it is a brand-new empty room, a valid token and one participant sitting alone. Unnamed, it resolves to the fleet's effective room rather than the `family` constant, a distinction one of the new tests caught before the route shipped: a fleet that had moved its `call.room` would have been minting into a room its frames had left. *Lifetime:* four hours against a frame's year, and not a parameter. §3.7's argument for a year is that renewal is free; this route has no renewal by construction, since it stores nothing for renewal machinery to find, and the only revocation in the project is rotating the API secret, which re-mints every frame as collateral. Lifetime is therefore the entire policy again and the only bound on a leaked token, and a caller-supplied one is a way to ask for a year. *Gating:* §3.2's operator session like every other `/api` route, never `/agent`, which is internet-exposed and answers strangers. The signing secret comes from the same `LiveKitDeployment` every frame's token comes from, reaches nothing but the HMAC, and appears in no response, log or table. <br><br>**Verified against the running server, not against its own reader.** The route was run in external mode against the live `livekit-server` in `framelink-dev` and its token offered to `GET /rtc/validate`, which answered `200 success`; the same request with no token answered `401 no permissions to access the room` and with one byte of the signature changed answered `401 invalid token … signature is invalid`, which is what makes the 200 evidence that the signature verified rather than that the endpoint is permissive. No frame was joined and none was disturbed. <br><br>**A web client and an Android app are explicitly out of scope for v2.** This is a minting seam and stops there: no participant record, no renewal, no revocation list, no GUI. Anything reading the route as the beginning of a client should read this row first. |
| 87 | The frame's address is not recoverable, and naming the proxy would make the budget forgeable | **`FRAMELINK_TRUSTED_PROXIES` stays empty on the development stack, and the stale comment claiming the container sees real source addresses is corrected** (§3.3, §3.8, decisions 61 and 85). Decision 85 recorded in passing that a frame reaches the containerised Fleet Manager as `172.18.0.1` — this network's bridge gateway — because inbound traffic to a published port is NAT'd through it. The consequence was not drawn there and is drawn here: §3.3's handshake budget is **per address**, so every frame in a household now shares one budget, and one frame reconnecting hard can spend its siblings' allowance. With one frame it is invisible. It is not a fault that appears later; it is a fault that becomes visible later. <br><br>**The setting was tested rather than reasoned about, and it works — on requests that carry a header.** Measured 2026-08-20 against the deployed image, on a throwaway pair of containers with `FRAMELINK_RATE_LIMIT_ATTEMPTS=1`. With `FRAMELINK_TRUSTED_PROXIES` empty, two `/agent` WebSocket upgrades carrying `X-Forwarded-For: 203.0.113.9` were both refused and logged as `Rate limited a device connection from 172.17.0.1` — the header ignored, both attempts charged to the gateway. With the variable set to that same gateway, the identical request was *allowed* and its follow-up refused as `203.0.113.9`. So `UseForwardedHeaders` does reach the WebSocket path, it does rewrite `Connection.RemoteIpAddress` before the limiter reads it, and `ControlApp` installing it only when a proxy is named does exactly what it claims. <br><br>**What it has nothing to work with is the device.** A frame is not a browser behind a reverse proxy: `WebSocketControlTransportFactory.ConnectAsync` opens a bare `ClientWebSocket` and nothing in the agent sets a request header on it, and Docker's published-port path is layer-4 NAT, which cannot add one. There is no `X-Forwarded-For` on the real `/agent` connection to recover an address from, so the setting would recover nothing here. **Worse, it would cost the budget its integrity.** After the NAT *every* client is the gateway, so trusting the gateway is trusting every client on the LAN to declare its own source address — the limit stops being coarse and becomes forgeable by anybody who can reach the port. A setting that looked right and did nothing would have been bad; this one would have been actively harmful, which is why it is refused rather than merely skipped. <br><br>**The production stack is the case the setting was built for and is untouched.** Traefik terminates the request and adds the header, so `${FRAMELINK_TRUSTED_PROXIES:-172.16.23.253}` recovers a real address there and the reasoning already written into `framelink.stack.yml` still holds. **The exact fix for the container case is per-device, not per-address**, keyed on the identity §3.3 already has — the keypair — and it belongs in Appendix B rather than in a variable, because it changes what the limiter counts rather than how it is configured |
| 88 | Base images live on a volume of their own | **A second volume, `framelink-images`, mounted at `/var/lib/fl-images` with `FRAMELINK_IMAGE_DIR` pointing at it, in both compose files — and unlike `framelink-data` it is deliberately NOT external** (§3.1, §3.9, decision 61). §3.9 already named the unbudgeted storage and named `FRAMELINK_IMAGE_DIR` as the mitigation; nothing had applied it. On the containerised deployment the variable was unset, so `ControlOptions.ImageDirectory` fell back to `DataDirectory/images` — a path no volume backs and which the image does not contain — and pressing Generate answered `BaseImageMissing`, naming a directory that had never been created. The generator itself was never at fault; it had already been proven in a throwaway container. <br><br>**Why a separate volume rather than a subdirectory.** A build holds three image-sized files at once — the 2.98 GB pinned base, a working copy and the artifact — so leaving it under the data volume puts nine gigabytes of scratch beside `framelink.db`, and §3.3 makes that file the *binding* rather than merely some data: losing it turns every adopted frame into a stranger in the queue. <br><br>**Why not external, when decision 61 calls `external: true` the single most important line in the stack file.** Because the reason there was irreplaceability, and nothing on this volume is irreplaceable: the base image re-downloads from raspberrypi.com against a pinned digest, and the artifact regenerates from a button. Compose owning it is therefore a feature — `docker compose down -v` reclaims six gigabytes of disposable image and structurally cannot reach the database, which is the same separation decision 61 argued for, stated positively rather than as a warning. <br><br>**The mount point had to be added to the image, and that is the trap.** A named volume mounted over a path the image does not contain is created **root-owned 0755**; this container runs as 10001 with every capability dropped, so the first build would have failed creating its working directory — on a volume that looks perfectly correct in `docker inspect`. `deploy/fleet-manager/Dockerfile` now creates `/var/lib/fl-images` owned by 10001 beside `/var/lib/fl-control`, for the reason already recorded there: Docker propagates a mount point's ownership into a freshly created volume. <br><br>**Verified end to end on 2026-08-20**, against the already-deployed image — which predates that Dockerfile line, so its volume needed once, by hand, the `chown` the change removes for everyone afterwards. The verified base image was copied in from the throwaway `framelink-image-build` volume rather than re-downloaded, `/api/image` then reported `directory: /var/lib/fl-images` with no problem, and a build ran 33 seconds to `Succeeded`, publishing `framelink.img` at 2977955840 bytes carrying the agent binary and the seeded control URL, served back over a 206 range request. `framelink-data` listed byte-identical before and after |
| 89 | Published-and-muted is kept, and nothing may read it as a call | **The frame keeps its camera and microphone published-and-muted between calls; the behaviour is written down at the three places that would tempt a wrong check, and no count anywhere may be read as call activity** (§2.10, §3.7, decision 84). Leaving a call calls `setCameraEnabled(false)` and `setMicrophoneEnabled(false)`, and the vendored `livekit-client` unpublishes only `ScreenShare` — camera and microphone take `else yield o.mute()`, read out of `app/vendor/livekit-client.umd.js` rather than assumed. The publications therefore survive with their track SIDs, which is why the next call re-uses them instead of negotiating new ones. It was measured *not* to hold the hardware open: nothing has `/dev/video*` while the frame is idle, because muting a `LocalVideoTrack` stops the underlying `MediaStreamTrack`. <br><br>**Kept, because the only cost is a number nobody should be reading.** The room reports this frame as a participant carrying two publications from boot until shutdown — a statement that the frame is switched ON, and nothing else. The failure this decision exists to prevent is somebody deriving “a call is in progress” from a participant count, a publisher count, a surviving track SID or a room that is not empty: every one of those is equally true of a frame that has been showing photos to an empty room for a week, and §2.10's daily restart stands down for an active call — so a count substituted for the flag would defer that restart for ever, on every frame in the fleet. <br><br>**What actually answers the question, and it is told rather than counted.** `frame-stage.js` holds an `inCall` flag that `callStarted()` and `callEnded()` set explicitly, and the agent reads it off the page heartbeat as `Supervisor.CallActive` — whose own remarks already record that this producer was once missing and that nothing said so. <br><br>**Verified that nothing reads it that way today, and this is what was checked.** The Fleet Manager never speaks to LiveKit's room API at all — the only `HttpClient` under `src/FrameLink.Control/LiveKit/` is `LiveKitRelease`'s, fetching the pinned binary from GitHub, and `LiveKitMediaProbe` reads only this host's own socket and address tables. On the frame, `Supervisor.CallActive` reads `LocalChannel.InCall` plus a test-only override, and `frame-app.js`'s one count — `this._byId.size === 0`, deciding when the last peer has left — is a **remote-only** map fed by `ParticipantConnected` and `TrackSubscribed`, which livekit-client raises for remote participants, so a frame's own muted publications never enter it. <br><br>**One thing was NOT measured, and is recorded as unknown rather than answered:** whether a peer's *muted* publication raises `TrackSubscribed` on a frame that connects afterwards. If it does, `frame-app.js`'s subscribe-triggered auto-answer means a frame rebooting beside an idle sibling would answer a call nobody placed. It cannot be observed on a one-frame fleet and is asserted in neither direction; the note sits on the auto-answer line, so the second frame's arrival is when it gets tested |
| 90 | Array firmware observed, never flashed — **its product conclusion superseded by decision 91**, its diagnosis of why a firmware version cannot be a resource kept and still binding | **`firmware.xvf3800.version` leaves the resource graph and becomes `ArrayFirmwareReporter`, which reads which firmware the microphone unit runs and never writes one** (§2.3, §5.5, resource catalog open questions 2 and 13). The operator's decision, ratified after new upstream evidence. **The resource had to go rather than be softened, and the reason is §2.3's own contract.** A resource is *Observe → Compare → Act (only on drift) → Verify*, and the only Act that converges a firmware version is a DFU write this product will now never perform unattended. Two softer shapes were considered and both are dishonest. An Act that does nothing leaves the resource drifted, so it spends three attempts and three reboots and escalates, and by decision 68 that escalation stops the whole pass — a frame carrying a factory 2.0.6 array would never converge its screen, its camera or its speaker over a number nobody was going to let it write. An Observe that always answers `InSync` is the **v1 governor shape** the contract exists to forbid: success claimed because the write returned rather than because the world is right. Decision 63 faced the same diagnosis from the other side — *the fault was the resource having no Act that could succeed* — and fixed it by giving the resource a real Act; here there is no real Act to give, so the other exit is taken. The house already has the shape: `PackageInventoryReporter` observes and reports and never acts, for the same reason, and this stands beside it. <br><br>**Why the flash is not worth automating, measured rather than argued.** Both of the operator's boards are revision **V1.1**, and upstream issue #32 — open, opened 2026-08-17 — reports firmware **2.0.10 and 2.1.0 not booting at all on a V1.1 board** (LEDs dark) while 2.0.6, 2.0.7 and 2.0.9 do, and reports that the documented `4mb_all_ff.bin` recovery from issue #8 **also failed** on that unit. Against that downside the payoff is near zero: measured on the bench 2026-08-20, a factory 2.0.6 array and an upgraded 2.0.10 array **both** read `GPO_READ_VALUES 0 0 0 1 0`, so the amplifier is on at boot either way — which is also the answer to catalog open question 13. The remaining benefits are DSP tuning and version consistency, and guide 4's claim that the DAC volume path differs between 2.0.6 and 2.0.10 has **never been measured in this repository**; guide 4 step 3's own EXPECTED OUTPUT is still a `[Pending fresh-flash capture]` placeholder. Making an autonomous flash safe would need exceptions carved into three load-bearing parts of the loop, including a reboot interlock `IResource`'s own doc comment says will never exist. <br><br>**A counterexample is recorded because it complicates issue #32 rather than confirming it.** Frame #1 runs a **V1.1 board on 2.0.10 successfully** — it enumerates, answers `VERSION 2 0 10`, and carried a real call with 1,811 decoded video frames. Upstream has published `v2.0.10` **twice with different bytes** (commits `17bac32` → `237f762a…` and `aeacafa` → `81593709…`, 43% of 933,888 bytes differing, both answering `VERSION 2 0 10` and both `bcdDevice 020a`). Frame #1 was flashed 2026-08-08, after the second publication, so it is *probably* running the newer build — **that is inference from dates and not a measurement**, and it is recorded as such. <br><br>**What replaces it.** Two independent readings, and the cheap one always works: `bcdDevice` out of `/sys/bus/usb/devices/*/`, which needs no control tool, no root, no USB control transfer and no process at all, and `xvf_host VERSION` when the tool is installed. The decode is measured, not assumed — `0206` is 2.0.6 and `020a` is 2.0.10, and `0a` is not a valid BCD digit pair, so the field is hex per nibble, which also means a minor or patch of 16 or more cannot be represented. The reporter emits a `DeviceEvent` of kind `array-firmware` at startup and whenever the reading changes, on the package inventory's cadence and for its reasons; a disagreement between the two readings is reported as a disagreement rather than reconciled. **Board revision is not obtainable in software at all** — not in the USB descriptors, and not in the control tool's command set: the 177 commands in the pinned `libcommand_map.so` carry `VERSION`, `BLD_MSG`, `BLD_HOST`, `BLD_REPO_HASH`, `BLD_MODIFIED`, `BOOT_STATUS`, `SERIAL_NUMBER` and `DFU_GETVERSION`, every one of which describes the firmware or the unit rather than the board. It is silkscreen, so a fleet can never know it, and any future flash logic gated on revision would be blind. <br><br>**What moves with it.** The catalog goes from **80 resources to 79**, the ordering table's audio phase moves to 54–61 and everything after it back by one; `audio.xvf3800.gpo-x0d31-amp-enable` and both playback volumes drop the firmware edge and the amplifier depends on `tool.xvf-host.installed` directly; `audio.firmwareFlashAuthorised` is gone and there is nothing left to authorise, because `dfu-util` is named in no code path in the agent — a test asserts that the only surviving mention is the apt package that puts the program on the frame for a person to run. `pkg.dfu-util` is kept and, unlike `pkg.git`, keeps a real consumer: the attended bench flash. `XvfHost` gained a process-wide gate so the reporter and the loop cannot both hold the array's HID interface at once — `xvf_host` has no device selector, and the loser of that race reads as *the array did not answer*, which is drift, which costs a reboot on a frame whose array was working. **v1 parity is unaffected**: the `audio.xvf3800.firmware` facet still compares `XVF3800_FIRMWARE` against the frozen `2 0 10`, but a difference there is now a finding for a person rather than a value a frame reboots trying to reach. <br><br>**Nothing here has run on hardware.** The reporter, the descriptor decode and the convergence of a 2.0.6 frame are exercised in the suite against a rooted temporary filesystem; the two bcdDevice readings behind the decode are real, captured from two arrays on 2026-08-20, and the `libcommand_map.so` command list was read by downloading the pinned blob to the workstation and confirming its SHA-256 against `XvfHostReleasePin` |
| 91 | The fleet converges on a pinned array firmware, and the flash is an interlocked operation rather than a resource — **four of its parts superseded by decision 93**: the recovery pair, the single-attempt rule, the board-revision gate and the binding Safe Mode sequencing. Everything else stands, the reasoning for the reversal included | **The operator reversed decision 90's product conclusion: the agent flashes the microphone array again, and the fleet converges on a firmware version somebody pinned** (§2.3, §2.4, §2.8, §7.1, decisions 63, 68, 79 and 90). The reasoning for the reversal is worth keeping because it is not an engineering argument: *a fleet that converges on a known firmware version is a better product property than a fleet that avoids a risky operation*, and this is software for hundreds of households rather than for two boards on a desk. Decision 90 weighed the risk against *this* fleet, which is two arrays somebody can reach with their hands; the product being designed has neither property. <br><br>**What the reversal changes, and what it deliberately does not.** It changes whether the agent may write firmware. It does **not** change decision 90's diagnosis, which was never about permission: §2.3's contract is *Observe → Compare → Act (only on drift) → Verify*, and a firmware **version** still has no Act that can always succeed — on a frame nobody has authorised it would drift, spend three attempts and three reboots, escalate, and by decision 68 stop the whole pass, leaving a working frame's screen, camera and speaker blocked behind a number. So `firmware.xvf3800.version` stays out of the graph. The write becomes a **single-use, digest-named, interlocked operation beside the loop** (`ArrayFirmwareFlash`), and what enters the graph instead is **`firmware.xvf3800.image`** — three pinned DFU images on the card, digest-verified, with a real Act that always succeeds. The catalog goes from 79 resources to 80 and the ordering table's positions after 22 each move on by one. **Convergence is then delivered in two halves that are each honest on their own**: every frame in the fleet carries the target image unattended and reports, on every change, whether the firmware it runs is the pinned one; and turning that into a write is one deliberate act per frame. §2.4 is untouched — no per-resource reboot exception was carved, and `IResource` still has no reboot member. <br><br>**"Latest" had to be settled first, and it is genuinely ambiguous upstream.** The repository has zero releases and zero tags, so there is no version to compare against and nothing that could answer *is this newer*; pinning is by commit SHA and measured digest, the pattern commit `173e321` established for `xvf_host`. **The version string is not the identity**: `respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin` has been published **twice under one name with different bytes** — `17bac32a` → `237f762a…` and `aeacafab` → `81593709…`, 402,246 of 933,888 bytes differing, both answering `VERSION 2 0 10` and both presenting `bcdDevice 020a` — so no observable distinguishes them and only a commit plus a digest is a pin. The ledger probe therefore watches the **file path**, never the directory, which is also the only probe that would catch a third republication. <br><br>**Which `v2.1.0`, established rather than assumed.** Upstream published **three** on 2026-08-14 — `v2.1.0.bin`, `v2.1.0_16k6ch.bin` and `v2.1.0_48k2ch.bin` — and flashing the wrong one changes the frame's audio topology underneath every mixer resource in the catalog. The unsuffixed build is the right one, on four independent readings. Seeed's own wiki states it for the 2.0.x line: *"Two firmware variants are available: respeaker_xvf3800_usb_dfu_firmware_v2.0.x.bin, which provides 2-channel audio, and respeaker_xvf3800_usb_dfu_firmware_6chl_v2.0.x.bin, which provides 6-channel audio. Both firmware versions operate at a 16 kHz sampling rate with 32-bit depth."* Upstream's 2.0.8 changelog **adds** the six-channel `ua-io16-6ch-sqr` profile, against the base profile Frame #1's array reports as `BLD_MSG ua-io16-sqr`. The 2.1.0 filenames spell both departures out (`16k6ch`, `48k2ch`), leaving 16 kHz and two channels unsuffixed. And the maintainer states a suffix's profile outright in upstream issue 19 — *"This v2.0.9_48k firmware is indeed built with the ua-io48-sqr configuration"* — which is the suffix-to-profile mapping given by the person who builds them. **A fifth reading stood here and has been withdrawn as falsified** (measured 2026-08-24): it argued that `v2.1.0` and `v2.1.0_48k2ch` differing by 30.03% against 46.17% for `v2.1.0` vs `v2.1.0_16k6ch` was what a rate-only difference looks like. Recomputing all 45 pairwise differences shows the metric does not discriminate — `v2.0.9` vs `v2.0.9_48k`, the maintainer-confirmed rate-only pair, differs by 44.10%, while `v2.0.7` vs `v2.0.9`, same profile and different version, differs by 28.31%, and every pair falls between 28% and 48%. The conclusion is unchanged; a number that looked like physical evidence and was noise is gone. The frame agrees from its own side: `reference/v1-state-inventory.txt` records this array's ALSA `Capture Channel Map` with `count 2` and PipeWire enumerating it as *Analog Stereo*. A test asserts the pinned name carries none of the variant suffixes, because a future bump that reached for one would be a silent hardware change. <br><br>**The interlocks, and which of them are new.** *A digest-verified pinned image* — `Authorise()` used to check only `FileExists`, and a DFU write of an unverified 933 KB file is strictly worse than no flash at all; the image is fetched and hashed the way `xvf_host` is, and **re-hashed again in the instant before `dfu-util` starts**, because a record that an install succeeded outlives the bytes it describes. *A single-use authorisation* — the old `audio.firmwareFlashAuthorised` was a persistent version string that re-authorised on every pass for ever; the new `audio.arrayFirmwareFlash` carries the image's **SHA-256** plus an operator ticket, and the whole string is written to the card with `WriteSecretAtomic` **before** the process starts, so a crash between the two cannot re-authorise and re-authorising means writing a different value. *An update stand-down* — **the interlock nobody's list had, and the one most likely to fire**: `UpdateService` and `ReconcileLoop` share one shutdown CTS and `fl-agent.service` deliberately leaves `KillMode` at `control-group`, so an hourly self-update `SIGKILL`s `dfu-util` through the cgroup; the check is asked twice, at the top of a tick and again on the far side of the download. *No reboot during the window* — done as a **refusal on the boundary** (`RebootHold`) rather than as an exception to §2.4, because a refused reboot is an outcome the loop already treats as first-class, exactly as decision 79's floor does. *A power-cycle guard* — impossible at the device; the agent writes a **durable marker** on the card for the duration of the write, and the harness-side refusal that reads it is the one interlock that lives on the workstation and **is not yet built**. *One attempt, never a retry* — structural, because the authorisation is spent before the write; a failure emits an event and asks for a person. *An event trail* — a new `array-flash` device event carrying the image digest, the version before and after, the elapsed time and `dfu-util`'s output verbatim; a refusal is the same kind, because *which interlock stopped this frame* is as much part of the trail as a write that happened. <br><br>**Five interlocks were added beyond that list.** A **crashed-flash latch**: a marker still present when a new process starts means a write began and nothing knows how far it got — a cgroup kill, a power cut and a crash all leave the same array — so every later flash is refused until a person removes the file, because retrying a partial write is the documented route from a recoverable board to an unrecoverable one. A **verified way back**: the frame refuses to write unless `4mb_all_ff.bin` **and** the v2.0.6 fallback are both present and hash to their pins, since fetching them at the moment they are wanted means fetching them onto a frame that is already in trouble. **Re-enumeration instead of a timer**: the old Act slept five seconds and claimed success; this one polls `bcdDevice` until the array comes back reporting the target and says so honestly when it does not. **Idempotency**: an array already on the target is not written to, and the authorisation is spent anyway so a later array swap cannot be flashed by nobody's decision. And **deferrals that are not spends**: a call in progress and a pending agent restart both wait, with the authorisation still armed. <br><br>**What no interlock addresses, said plainly.** Upstream issue #32 — open, opened 2026-08-17 — reports **2.0.10 and 2.1.0 not booting at all on a board revision V1.1** while 2.0.6, 2.0.7 and 2.0.9 do, and reports the documented `4mb_all_ff.bin` recovery **also failing** on that unit. Both of this project's boards are V1.1. Frame #1 runs 2.0.10 on a V1.1 board successfully, which complicates that report rather than refuting it. **Board revision is not readable in software at all** — not in the USB descriptors and not in the 177 commands of the pinned `libcommand_map.so` — so it is silkscreen, a fleet can never know it, and no gate can be written on it. The largest single risk of this operation therefore has no software mitigation, which is why the sequencing below is part of the decision rather than advice attached to it. <br><br>**Sequencing, which is binding.** Nothing is flashed until the **Safe Mode recovery route has been rehearsed on one of this project's own arrays** — power off, hold Mute, reconnect, red LED blinking, and `dfu-util -l` listing a third alt setting as the proof it was entered. Safe Mode is vendor-documented and lives in the **Factory** partition, which is why an interrupted write to the **Upgrade** partition leaves a way back; but nobody here has ever entered it, and an unrehearsed recovery is a hope. The rehearsal writes nothing and is a complete outcome on its own. **Two procedural details are recorded because one of them is not in the upstream instructions**: the `all_ff` erase **terminates at about 96%** with `dfuERROR status(8) … out of range` and that is the expected outcome, and a **power cycle is required** between the erase and the next write or the download fails at 0%. <br><br>**And the 6 dB to 8 dB default.** v2.1.0 changes the AIC3104 headphone and line-output default gain from 6 dB to 8 dB and adds `AIC3104_HP_LEVEL` / `AIC3104_LINEOUT_LEVEL`, **whose values `SAVE_CONFIGURATION` persists to flash**. Two consequences. The measured ~94 dB close-miked loudness result was taken against the 6 dB default on 2.0.10, so it describes 2.0.10 hardware until it is re-measured — `TODO.md` now carries the firmware version beside the number. And a gain persisted **on the array** would be a third owner of loudness beside the ALSA mixer and WirePlumber, which this project spent a night discovering the cost of: so the agent sends **neither** command, `SAVE_CONFIGURATION` appears nowhere in the repository, and the 2 dB shift arrives as a one-off change in the hardware's own default rather than as a fourth thing that can disagree. <br><br>**Nothing here has run on hardware.** No array was touched, no `dfu-util` was run, and no frame was deployed to. The three digests and the three commit SHAs were measured by downloading the files to the workstation; the variant question was settled from vendor documentation, upstream's changelog, a byte comparison of all nine published USB images and this project's own v1 capture; and every interlock is exercised in the suite against a synthetic pin, a temporary filesystem and a recording process runner, with each guard's test verified to fail when that guard is removed |
| 92 | Six frames, one budget: the handshake limit was keyed on the thing every frame shares | **The handshake budget is keyed on the proven keypair, and the per-address window is kept as the bound on *unidentified* traffic** (§3.3, §4.2, discharging decision 87's deferral). Decision 87 measured the fault and named the fix without building it: §3.3's budget counted attempts per source address, and a household NAT — or a container's published port, where the whole fleet arrives as the bridge gateway — makes that one budget for every frame the operator owns. Invisible with one frame; with six, one frame in a reconnect loop spends its five healthy siblings' allowance and they are refused before they can say who they are. <br><br>**The keying now has two halves and the split is where the reasoning lives.** The pre-upgrade charge stays, because before the WebSocket upgrade the only thing known about a peer is where its packets came from — but it is now *provisional*. The moment the proof binds the connection to a device this Fleet Manager has adopted or blocked, that charge is released and the attempt is charged to the device's own window instead. A fleet of healthy frames therefore spends nothing on the shared window at all. **The release is unconditional and the refusal is not**, and that ordering is load-bearing: leaving an over-budget device charged to its address would have one looping frame drain the shared window instead, at which point its siblings are refused before the upgrade and never reach the proof that would have released them — the same lockout, one layer down. <br><br>**The unknown-device path is bounded by exactly the budget that applied before, and it is deliberately the strict half.** Nothing is released for an attempt that proves no key, forges one, or proves a key this server has never met or has not yet adopted; all of it stays on the address window at the shipped 20 per minute per address, refused before the upgrade for one HTTP response. That also keeps the device window's dictionary bounded by the operator's own fleet rather than by attacker input — a stranger cannot create an entry in it, so flooding the route cannot fill it and lock a real frame out of its own allowance. Minting keypairs mints no budget, and naming another device in the hello spends nothing of that device's, because the charge is against the fingerprint the *proof* established. <br><br>**A throttled frame is answered, and the answer is the one status that is not a verdict.** `HandshakeStatus.RateLimited` is a new value rather than a new shape — §4.2's freeze is on `WireEnvelope`, the handshake records and `AgentRelease`, and those statuses are strings precisely so a word a peer has never heard of is reportable instead of fatal. It says nothing about the device's state, so the agent must not let it become one: `HandshakeExchange` demotes it to a failed exchange carrying the server's own sentence, which puts it on the silence rung and feeds the reconnect backoff. Carrying it through as a verdict would make it the frame's last authoritative condition, and the next dropped connection would compute *was not green* and blank a living room because a server asked for a pause — which §2.6 forbids. <br><br>**The residue is accepted rather than hidden.** An adopted frame in a hard loop still costs an upgrade, one signature verification and one indexed read per attempt, because nothing can recognise it earlier than the proof; what it no longer costs is anything belonging to another frame, and every expensive thing past that point — the row write, the fleet event, the settings resolve, the call-token review, the socket and its ping timer — is skipped. `FRAMELINK_TRUSTED_PROXIES` is untouched and still means what decision 87 says it means |
| 93 | One firmware, one definition of it, three attempts, and no Safe Mode support | **The operator simplified the array-firmware path in four separate decisions taken the same day, each of which removes something decision 91 built** (§2.3, §2.8, §7.1, decisions 90 and 91). Taken together the path is now: *check the hardware, check the running firmware, and if the hardware is right and the firmware is old, write the one image the agent already carries.* <br><br>**1. The recovery kit is dropped.** Decision 91 pinned three images — the v2.1.0 target, a v2.0.6 fallback and Seeed's 4 MiB all-`0xFF` erase image `4mb_all_ff.bin` — and `ArrayFirmwareFlash` refused every write unless the latter two were on the card and hashed to their pins (`RecoveryNotVerified`). All of it is gone: the two pins, the two `upstream-review.json` entries, the harness's copies and the gate. **The evidence is in `reference/xvf3800-recovery-model.md`, measured 2026-08-24 against XMOS's and Seeed's own sources**, and four findings each independently undercut the kit. A DFU download **already erases the whole upgrade section before it writes** — XMOS's `lib_dfu`: *"on receiving the first DFU_DNLOAD command, the device starts to erase FLASH_MAX_UPGRADE_SIZE bytes of the upgrade section"* — so a separate erase step has nothing to do. **Seeed's own documented recovery has no erase step**: enter Safe Mode, flash the firmware; the string `all_ff` appears nowhere in the wiki, the DFU guide or the changelog, and its entire documentation is one GitHub issue comment. The failure it was published for is a configuration corrupted by `SAVE_CONFIGURATION`, which the maintainer says was **fixed in firmware from v2.0.9** and which this repository cannot cause, because it sends that command nowhere. And **"2.0.6 is the fallback" traces to one commit** — `git log -S` finds `XvfFirmwareRole.Fallback` introduced by a single commit whose thirty-line message never mentions 2.0.6, a fallback or a recovery pair; neither upstream, nor Seeed, nor decision 91 ever recommended that version, and the maintainer's own recovery advice tracks the *newest* image. <br><br>**What dropping it costs, named rather than glossed.** A second known-good image on every card was insurance against the pinned target being bad on a board nobody has met. That risk is real and unquantified — v2.1.0 has never run on any board in this project or in any published upstream report — and putting an image back is now a pin bump and a release rather than a visit. The erase image was also the only answer to a corrupted DataPartition, a failure this product cannot reach. **Against that, the gate was doing active harm**: the target is vendored and embedded and the other two were fetched, so refusing without them was the *single* reason a frame with no network could not flash an image it was already carrying — the precise opposite of the reason the target was vendored. <br><br>**2. Three attempts, replacing "one attempt, never a retry".** Decision 91 made the authorisation single-use and spent it *before* the write, explicitly so that a failure could never be retried. The operator has reversed that for consistency with every other repair in the product: one authorisation now buys up to `ArrayFirmwareFlash.MaxAttempts` = 3 writes, inside one operation, sharing one local approval and one event. **What is lost is real and is recorded here because the reasoning deserves to survive the decision**: a write that completed and did not produce the pinned firmware is now followed by another write with no person in between, where before it stopped and asked for one. **Two failures wear the same clothes and only one is retried**, which is what keeps this safe: a *refused* flash wrote nothing and spends no attempt; a *completed* write that did not stick is what the three are for; and an **interrupted** write is neither — the durable marker `ArrayFlashWindow` writes per attempt outlives the process that was killed, and `PreviousFlashUnfinished` then refuses everything until a person removes the file. That latch is untouched and is not one of the three, because how far a half-written partition got is a state this frame cannot measure. Two conditions also end the loop early: an array that has come back on the target is not written again, and an array that is not on the USB bus at all is left alone. §2.5's attempt budget is not involved and does not move — the flash is beside the loop, not in it. <br><br>**3. One definition of the firmware pin.** It lived three times: in the agent, in the Fleet Manager as `ArrayFlashPin`'s four `Target*` constants, and in `tools/harness/flh/flash.py`, with a test holding the first two equal string by string. The agent's `XvfFirmwarePin.cs` is now the only definition and the Fleet Manager `<Compile Include>`s it across the project boundary — the same reach-across the csproj already makes for `fl-agent.service`, and deliberately **not** the protocol assembly, which holds frozen wire contracts and this is not wire data. The namespace differs under a `FRAMELINK_CONTROL` constant, because the two programs are separate AOT binaries and the test project references both. The harness's copy stays and stays deliberate: it runs where there is no .NET SDK, it parses that file at run time, and it refuses to write when the two disagree — a second record that *refuses* is worth more than one that cannot check. <br><br>**4. The completion screen has no timer.** `CompletionLinger` was fifteen minutes, after which a finished screen took itself away, justified by the frame flashed under the operator bypass having nobody to press anything. It stays now, on both outcomes, until somebody acknowledges it. **The cost is the one the old reasoning named**: a frame whose panel cannot be touched offers no affordance, so it shows that screen until the agent restarts. The outcome is in the event trail, the journal and the Fleet Manager regardless of who read the screen. <br><br>**5. The board-revision gate is removed entirely** — not softened to advisory, removed. Rung 4 of `ArrayHardwareGate`'s ladder, the recorded-revision comparison at the last rung, the `IBoardRevisionGate` seam that existed to keep a pending decision cheap, the `audio.arrayBoardRevision` setting and its catalogue entry, and the `BoardRevisionRefused` verdict all go; the ladder is nine rungs and every refusal's `check: N of M` counts the ladder that exists. **The reasoning: the gate refused on no evidence.** Nothing in any published source distinguishes a V1.0 from a V1.1, v2.1.0 changes nothing board-specific, and the single contrary report is one unreproduced unit whose reset button is physically detached and whose own reporter concludes the failure is independent of firmware version. And the shape of the fact was wrong: asked where the recorded value would come from, the honest answer is that a human reads silkscreen and types it — a field with nothing to check it against, absent by default, silently wrong the moment somebody swaps a unit, gating the one operation on this frame that cannot be undone. **What did not go is the sentence in every refusal saying board revision is not readable from the unit at all.** That is still true, it is still the most surprising fact about this hardware, and somebody reading a refusal and wondering why the frame does not simply check the board is owed the answer. It stopped being a gate; it did not stop being worth saying. <br><br>**6. Safe Mode support is dropped, and this is the largest removal on the list.** No runbook (`ArrayFlashRecovery` and its `SafeModeSteps` / `OperatorSteps`), no on-screen recovery instructions (`ArrayFlashVoice.Wedged` and the `Wedged` phase), no wedged-board detection — the marker-present-plus-empty-bus branch now shows the same "a write was interrupted and this frame cannot tell" screen as any other — and no `fl.py array runbook`. Decision 91's binding sequencing goes with it: a rehearsal is a one-time bench test for this project, not something a household does before a flash. **What it costs, said once and not argued.** Safe Mode is firmware in the board's Factory partition that no DFU write can touch, entered by a physical gesture the bootloader samples at power-on, and it is the **only** route back from a board that has stopped enumerating — exactly the state in which the agent's own write cannot help, because that write detaches a *working* device into DFU mode. After this change the software has no recovery path for that state at all; a board in it goes back to the maintainer. The operator was told this before choosing. **The knowledge was not deleted**: `reference/xvf3800-recovery-model.md` is the measured record of what Safe Mode is, what the Factory partition guarantees, what the erase image did and did not do, and what each recovery route costs, and it stands unchanged. Every code site that referenced the procedure carries a line saying the capability was dropped deliberately and pointing at that file, so the next reader finds a decision rather than a gap. <br><br>**One thing was checked rather than assumed**: with the recovery kit and Safe Mode support both gone, nothing left in the flash path requires either. The pre-flight refusals are `DfuUtilMissing`, `ImageNotVerified`, `NoArrayAttached`, `MoreThanOneArray`, `AlreadyAtTarget` and `ArrayNotRecognised`, plus the marker latch and the local approval — none of them consults a runbook, a fallback image or an erase image, so removing them cannot silently refuse a write. <br><br>**Nothing here has run on hardware.** No array was touched, no `dfu-util` was run in any mode, no array was put into DFU mode, and no frame was deployed to or rebooted. The suite is 1,445 tests and green |

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
   - ✅ **The other half of it now exists: the updates are watched rather than merely allowed**
     (decision 55). Every frame reports its whole installed package set with versions whenever it
     changes, and the Fleet Manager computes drift against the reviewed baseline and across the
     fleet. So "security updates flow" is no longer a policy taken on trust — a frame that stopped
     taking them shows up as the one sitting on older versions than its neighbours, which is the
     signal this item's *visible and centrally changeable* was reaching for.
5. **Cross-compile benchmark** — emulated arm64 builds are proven; measure whether the
   cross-compiling container is worth adopting.
6. **An upstream bind address for Immich Kiosk** — an open request to the
   [Immich Kiosk project](https://github.com/damongolding/immich-kiosk) for a host or bind
   setting beside the existing `port`. Decision 56 accepts a LAN-wide bind because v0.42.0 offers
   no alternative, and that acceptance is **provisional on this item**: if upstream adds the
   setting, `kiosk.listen-address` becomes an ordinary catalog value — bind `127.0.0.1`, observe
   it, repair it like anything else — and the exposure closes with no packet filter and no new
   resource. The recorded default until then is the acceptance, unchanged.
