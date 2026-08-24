# Everything outside the DAG, one at a time — 41 items, reviewed for a decision

This is the operator's request answered in the shape it was asked for. For each of the 41 items in
`reference/outside-the-dag.md`: a short primer, why it is not in the DAG, what moving it in would
cost, and an honest opinion.

`reference/outside-the-dag.md` is left exactly as it is. It is the raw inventory — the evidence, the
citations, the counts. This document does not repeat it; it re-shapes it into something that can be
worked through and decided.

---

## How to read this, and how to reply

Every item has a number from **1 to 41**. Reply with the numbers you want moved — *"3, 7 and 22 yes,
the rest no"* — and nothing else is needed.

The items are grouped by **what I recommend**, not by how the inventory classified them, so
everything I think should move is in one place:

| Group | Numbers | Meaning |
|---|---|---|
| **Move in** | 1–5 | Should become a resource or a gate in the graph. |
| **Change shape** | 6–14 | Stays outside the graph, but something about its relationship to the graph has to change before the current answer is defensible. |
| **Stay out** | 15–41 | Correct where it is. Nothing to decide. |

Each entry names which of three things the item would be if it were in, using the operator's own
vocabulary:

- **Resource** — something the agent can *change*. It has an Act, so drift is repaired, verified and
  rebooted for.
- **Gate** — something the agent can only *find out*. It has no Act, so drift means stop and a person
  has to come.
- **Neither** — infrastructure the graph itself stands on. Making it a resource would be circular or
  meaningless.

And each entry says, exactly, whether the reason it is out is **recorded** (quoted, with its
location) or **not recorded anywhere**. That distinction is the thing the operator is hunting for and
it is never blurred below.

---

## What I verified rather than inherited, and what has already moved

The inventory carries a snapshot caveat and it was right to. Five of its facts have moved since it
was written, one of them materially. I re-read the working tree before writing any of this.

**1. The loop count is confirmed: fourteen, and there is a fifteenth nobody is watching.**
`AgentHost.cs` builds a `running` list of exactly **14** tasks — I counted the entries at
`AgentHost.cs:685-724` directly. The comment above that list still says *"Twelve loops now"*, which
is wrong by two. Only tasks in that list are covered by `WhenAllOrFirstFaultAsync`, which is the only
thing on a frame that notices a loop dying. Separately, there are exactly **three** `Task.Run` sites
in the whole agent: `Local/LocalOrigin.cs:160`, `Local/LocalOrigin.cs:254` and
`Firmware/ArrayFlashProgress.cs:467`. Two of those are transient — one per HTTP connection, one per
firmware write. **The third, `LocalOrigin.cs:160`, is a permanent loop that runs for the life of the
process and is not among the fourteen.** It is stored in a field and awaited only inside
`DisposeAsync` (`:184`), which runs at shutdown, so a fault in it is never surfaced while the frame
is alive. That is the fifteenth loop, it is the one nothing is watching, and it is **item 6** below
with a number of its own.

**2. The graph has grown by one since the inventory was written, and the new one is a *gate*.**
The inventory quotes `Assert.Equal(81, graph.Count)`. The test now reads `Assert.Equal(82,
graph.Count)` (`tests/FrameLink.Tests/AgentResourceGraphTests.cs:220`), and the catalog-document
assertion has gone 80 → 81 (`:334`). The new resource is `firmware.xvf3800.recognised`
(`src/FrameLink.Agent/Firmware/ArrayRecognitionResource.cs`), landed today in commit `1df3e07` and
corrected hours later in `8b0b01e`.

**3. The operator's word "gate" is now a real mechanism in the code, not a description.** This is the
single most important change and it reframes a third of this document. `IResource.IsGate`
(`src/FrameLink.Agent/Reconcile/IResource.cs:320`) exists as of today. A resource that sets it
drifts, and the loop then takes it *"straight to §2.5 rung 2 with the budget declared spent: no Act,
no reboot, one escalation, and decision 68 stops the pass around it"* (`IResource.cs:302-311`; the
route itself is `ReconcileLoop.cs:896-925`). Its own remark says why that matters here — the
objection decision 90 raised against putting firmware in the graph *"was an objection about waste
rather than about the verdict"*, and the flag removes the waste. **Every "could be a resource but its
Act cannot succeed" argument in the old inventory has to be re-read against this.** There is
currently exactly one gate in the graph. That gate's own file is uncommitted and being edited as this
is written, so treat its details — not its existence — as provisional.

**4. The hardware ladder is ten rungs, not nine.** The inventory says *"a nine-rung refusal ladder"*.
`Firmware/ArrayHardwareGate.cs` now documents **ten**; rung 10 is the whole-profile allowlist check,
added today. Its own remark states the point: *"The allowlist is the rung that makes 'we only flash
what we recognise' literally true"* (`:239-243`).

**5. The array firmware work is uncommitted and moving under this document as it is written.** `git
status` right now shows modifications to `Firmware/ArrayFirmwareFlash.cs`,
`Firmware/XvfFirmwareRelease.cs` and `Firmware/ArrayFlashProgress.cs`, plus a brand-new untracked
`Firmware/XvfFirmwarePin.cs` that is compiled into *both* the agent and the Fleet Manager across a
project boundary. The `Fallback` and `Recovery` firmware roles were **removed today** along with the
recovery kit. Items **12**, **13** and **24** are flagged in-flight and no part of them is described
below as settled.

Line numbers are from today's working tree and may move again. Where a number matters, the
surrounding identifier is named beside it.

---

## The summary table

Read the shape here, then go to the group that interests you.

| # | Item | Inv. id | Inventory said | Would be | My recommendation | Confidence |
|---|---|---|---|---|---|---|
| **1** | The agent's own systemd unit | C6 | (iii) no recorded reason | **Resource** | **Move in** | High |
| **2** | Screen handover — which terminal the panel shows | A8 | (iii) no recorded reason | **Resource** | **Move in** | High |
| **3** | The startup screen handover | C4 | (iii) no recorded reason | **Resource** (the same one as 2) | **Move in**, folded into 2 | High |
| **4** | Browser stage / the fallback rule | A7 | (iii) no recorded reason | **Resource** | **Move in**, observable half only | Medium |
| **5** | The interrupted-flash latch | C3 | (ii) recorded | **Gate** | **Move in** as a gate | High |
| **6** | Local HTTP server and its accept loop — *the fifteenth loop* | B1 | (ii) recorded | Neither (the loop); Resource (already in) | **Change shape** — put the accept loop under supervision | High |
| **7** | Console stage | A1 | (iii) no recorded reason | **Gate**, if anything | **Change shape** — record the reason, fix the permanent demotion | High |
| **8** | The console stage's repaint subscription | B8 | (iii) no recorded reason | Neither | **Change shape** — with 7 and 9 | High |
| **9** | The status hub | D5 | (i) cannot be a resource | Neither | **Change shape** — publish must stop running on the caller's thread | High |
| **10** | The remembering subscription | B7 | (i) cannot be a resource | Neither | **Change shape** — get the disk write off the publisher's thread | High |
| **11** | Touch retry reader | A13 | (iii) no recorded reason | **Gate**, if anything | **Change shape** — report it, do not gate it | Medium |
| **12** | Array firmware reporter ⚠ in flight | A10 | (ii) recorded | Neither, now | **Change shape** — fold its Observe into the new gate | Medium |
| **13** | Array firmware flash and its consent screen ⚠ in flight | A11 | (ii) recorded | Neither (the write); **Gates** (its preconditions) | **Change shape** — keep the write out, keep moving preconditions in | Medium |
| **14** | `XvfHost.Conversation`, the array semaphore | D3 | (i) cannot be a resource | Neither | **Change shape** — its recorded justification rests on a defect | High |
| **15** | Control link to the Fleet Manager | A2 | (i) | Neither | **Stay out** | High |
| **16** | Agent status reporter | A3 | (i) | Neither | **Stay out** | High |
| **17** | Update service | A4 | (ii) recorded | Neither | **Stay out** | High |
| **18** | The reconcile loop itself | A5 | (i) | Neither | **Stay out** | High |
| **19** | Supervisor — §2.10's five behaviours | A6 | (ii) recorded | Neither | **Stay out** | High |
| **20** | Package inventory reporter | A9 | (ii) recorded | Neither | **Stay out** | High |
| **21** | Call button watch | A12 | (ii) recorded | Neither (already split) | **Stay out** | High |
| **22** | Immich Kiosk child process | A14 | (ii) recorded | Neither (already split) | **Stay out** | High |
| **23** | Local channel and its four handlers | B2 | (i) | Neither | **Stay out** | High |
| **24** | Array flash progress pump ⚠ in flight | B3 | (i) | Neither | **Stay out** | High |
| **25** | The `gpiomon` child process | B4 | (i) | Neither | **Stay out** | High |
| **26** | SIGUSR1 simulated button press | B5 | (i) | Neither | **Stay out** | High |
| **27** | SIGTERM and Ctrl+C handlers | B6 | (i) | Neither | **Stay out** | High |
| **28** | Device keypair load-or-create | C1 | (ii)/(i) recorded | Neither (already split) | **Stay out** — but it has a real defect | High |
| **29** | Journal read, memory read, and the resumed condition | C2 | (i) | Neither | **Stay out** | High |
| **30** | Endpoint resolution | C5 | (ii) recorded | Neither | **Stay out** | High |
| **31** | The `version` verb | C7 | — | Neither | **Stay out** | High |
| **32** | Supervision interlock | D1 | (i) | Neither | **Stay out** | High |
| **33** | The reboot boundary stack | D2 | (ii) recorded | Neither | **Stay out** | High |
| **34** | Telemetry outbox and uplink | D4 | (i) | Neither | **Stay out** | High |
| **35** | The reboot countdown | D6 | (i) | Neither | **Stay out** | High |
| **36** | The embedded product app | E1 | (i) | Neither | **Stay out** | High |
| **37** | `chromium-kiosk` and `framelink-camera` user units | E2 | (ii) recorded | Neither (already split) | **Stay out** | High |
| **38** | `getty@tty1` | E3 | (ii) recorded | Neither | **Stay out** | High |
| **39** | `unattended-upgrades` and the apt timers | E4 | (ii) recorded | **Gate**, for one narrow part | **Stay out** — with one genuinely open question | Low |
| **40** | WirePlumber and PipeWire | E5 | (i) | Neither | **Stay out** | High |
| **41** | The workstation bench harness | E6 | (i) | Neither | **Stay out** | High |

**Where I am genuinely unsure, stated rather than hedged.** Item **39** is the one I have no real
position on — I found a gap and cannot size it. Items **4**, **11**, **12** and **13** are
medium-confidence for reasons each entry gives. Everything else I would defend.

**What changed against the old classification.** The inventory's category (iii) — outside for no
recorded reason — had seven members: A1, A7, A8, A13, B8, C4, C6. Four of those I recommend moving in
(items 1, 2, 3, 4); three I recommend keeping out but writing the reason down (items 7, 8, 11). Two
items the inventory placed in category (ii) *with* a recorded reason I nonetheless recommend acting
on: item **5**, because the gate mechanism that landed today makes its recorded reason cheap to
honour inside the graph rather than beside it; and item **13**, because its recorded reason is being
rewritten as we speak.

---

# Group 1 — Move in (items 1–5)

Five things I think should become part of the graph. Four of the five are in the inventory's
category (iii): outside for no recorded reason at all. The fifth is there because a mechanism that
did not exist when its reason was written now exists.

---

## 1. The agent's own systemd unit — *move in*

*(Inventory C6. `src/FrameLink.Agent/Systemd/UnitInstaller.cs`, `Systemd/fl-agent.service`,
`Program.cs:60`.)*

**What it is.** The agent is a background program that has to start automatically every time the
frame is switched on. On Linux that is arranged by a small text file — a "unit" — which tells the
system what to run, as whom, and what to do if it crashes. The agent writes that file for itself,
once, the first time somebody installs it, and never looks at it again.

**Why it is not in the DAG.** It would be a **resource**: the agent can read the file, compare it
against the text it carries inside itself, and rewrite it. There is a genuine Act. **There is no
recorded reason anywhere.** I searched the unit file, the installer, the host and the design
document; nothing says why this one file is exempt. What makes the silence loud is the company it
keeps — the catalog already reconciles `unit.chromium-kiosk.content`,
`unit.chromium-kiosk.enabled`, `unit.chromium-kiosk.running-matches-content`,
`unit.cpu-performance.content`, `unit.cpu-performance.enabled`, `unit.framelink-camera.content`,
`unit.framelink-camera.enabled` and `unit.xdg-desktop-portal.dropin-desktop`. **Every systemd unit
this product installs is reconciled except the one that starts the reconciler.** The file itself
documents four settings that are easy to get silently wrong, at length and from experience: the
start-rate limit *"must be under `[Unit]`, not `[Service]`"* or systemd ignores it with only a
journal line to say so — *"That is exactly what the mule did on 2026-08-15"* (`fl-agent.service:35-59`);
`TTYPath=/dev/tty8`, which is what makes the console screen work at all; `User=root`; and `KillMode`
at its default. Nothing on a running frame checks any of them. There *is* one check, and it is worth
being precise about because it is not the one you would want: the unit text is committed twice —
once for the workstation harness, once embedded in the binary — and a **build-time** test compares
them byte for byte (`fl-agent.service:3-13`). That catches the two copies drifting from each other in
the repository. It cannot see a frame.

**The downsides of moving it in.** Three, and all three are tractable. First, the Act has to run
`daemon-reload` and could restart the agent in the middle of a reconcile pass — so it needs the same
stand-down the update service already has, which is an existing mechanism rather than a new one.
Second, §2.4's rule that every Act is followed by a reboot would land that reboot on a frame whose
supervising unit had just changed; the ordering has to be reload-then-reboot, not the reverse. Third
and most real: **a bug in this resource is the one bug that can stop a frame from starting at all**,
and unlike every other resource there is no outer loop to repair it, because the thing being repaired
is what runs the loop. That argues for writing the file, verifying it parses, and only then enabling
— not for leaving it unchecked. It is not circular: the agent does not need the unit to be *correct*
in order to run, it only needed it to be correct at the last boot.

**Honest opinion.** **Move it in, and I would put it at the top of the list.** This is the one file on
the frame whose silent corruption disables every other honesty mechanism the product has, it is
written once by a verb that a person runs by hand and then never audited again, and an agent update
replaces the binary but never the unit — so a unit written by an old installer survives for the life
of the SD card. Two resources, mirroring what already exists for Chromium: one for the content, one
for "the unit systemd is actually running matches the unit this binary carries".

---

## 2. Screen handover — which terminal the panel shows — *move in*

*(Inventory A8. `src/FrameLink.Agent/Stage/ScreenHandover.cs`, run from `AgentHost.cs:702`.)*

**What it is.** The frame's screen can show one of two things: the agent's own text display, which
narrates what the frame is doing while it sets itself up, or the graphical desktop that shows the
household's photographs. Linux calls these "virtual terminals" and only one can be on the panel at a
time. A small loop checks twice a second which one should be showing and switches if it is wrong.

**Why it is not in the DAG.** It would be a **resource** — there is a clean observable fact ("the
panel is showing terminal 1") and a clean Act ("switch it to terminal 8"). **There is no recorded
reason anywhere, and the code says out loud that it is doing reconciliation.** The host's comment
above it reads: *"which one the panel shows is state that has to be reconciled like everything else
(§2.2) rather than set once and trusted"* (`AgentHost.cs:698-701`), and the class repeats it:
*"Level-triggered, like everything else here (§2.2)."* It declares itself a reconciler, and it is not
in the reconciler. There is one more reason this is not merely tidiness: the class's own remark
records that a handover which kept the panel while the compositor was running would make the graph
resource `display.dsi2-transform` fail on every boot and turn that into a reboot loop. **A graph
resource's ability to converge depends on the behaviour of a loop that is not in the graph, and
nothing in the graph can see that dependency.**

**The downsides of moving it in.** Two real ones. The 2-second cadence would be lost if the *whole*
thing moved — a reconcile pass runs every five minutes, and there is exactly one moment where that
matters: the instant the compositor dies, when a five-minute wait leaves a blank panel in a living
room. And the loop **returns permanently** when it discovers the machine has no consoles to switch
between (`ScreenHandover.cs:344`, `Switchable = false`), which is every run on a workstation; a
resource would need an honest answer for that, most likely `Unevaluable` rather than drift, or the
whole test suite escalates. Neither is a reason not to, because the fix is the split this codebase
already uses twice: the fast loop stays where it is as the *mechanism*, and the resource asserts the
*outcome*. That is exactly the shape of `gpio.button.line` (item 21), where the claim is the resource
and the daemon holding it is not, and of `app.http.local-origin` (item 6).

**Honest opinion.** **Move the observable half in and leave the 2-second loop where it is.** Today a
panel stuck on the wrong terminal is completely silent — no census row, no escalation, nothing on the
Fleet Manager, and the only symptom is a household looking at a login prompt instead of their
photographs. That is a frame that has failed in the most visible possible way and reports itself
green.

---

## 3. The startup screen handover — *move in, folded into item 2*

*(Inventory C4. `await screen.ReconcileAsync(...)` at `AgentHost.cs:219`.)*

**What it is.** The same decision as item 2, made once at the very beginning — before the agent does
anything slow — so that the first thing painted lands on a screen somebody can actually see.

**Why it is not in the DAG.** It would be part of the same **resource** as item 2. **No recorded
reason**, for the same reason item 2 has none: it is the one-shot half of the same unwritten
decision. What *is* recorded is a subtler and quite good point — why the frame *reconciles* the
terminal at startup rather than simply grabbing it: taking the panel on every agent restart would
mean *"a service restarted would be a fault of its own"* (`AgentHost.cs:213-217`). That is an
argument about politeness, not about graph membership.

**The downsides of moving it in.** Almost none on its own, because it is not a separate thing — it is
item 2 running once. The only genuine consideration is ordering: this runs at `AgentHost.cs:219`,
long before the first reconcile pass, and it needs to keep doing so. If it became *only* a resource
the first paint would land on whatever terminal was already showing, which is the exact problem it
was written to prevent. So it must stay as a pre-pass call and *additionally* be asserted by the
resource — which is what the existing local-origin split already does.

**Honest opinion.** **Move it in with item 2; it is not a separate decision and should not get a
separate answer.** Keep the one-shot call exactly where it is.

---

## 4. Browser stage, and the fallback rule — *move in, observable half only*

*(Inventory A7. `src/FrameLink.Agent/Stage/BrowserStage.cs`, driven from
`AgentHost.BrowserStageLoopAsync:817`.)*

**What it is.** Once the frame is set up, it starts a graphical session and loads the product page.
This loop watches for that page to say "I am here" within sixty seconds. If it does not, the loop
tears the whole graphical session down, explains on the text screen what went wrong, waits two
minutes, and puts the text screen back. Then it tries again, for ever.

**Why it is not in the DAG.** It would be a **resource** — "the product page checked in" is an
observable fact and "restart the session" is a real Act that can succeed. **There is no recorded
reason anywhere.** The file explains a great deal at length: why the handover is a switch rather than
a reveal, why the login prompt must be stopped during a teardown, why the stage arms itself. It never
once says why *"the page renders"* is not a resource. And the code is a level-triggered
observe-compare-act cycle on system units with a named delta and a named list of affected resources —
`BrowserStage.SessionResources` (`:189-195`) enumerates `unit.chromium-kiosk.running-matches-content`,
`session.bash-profile-exec-labwc`, `display.dsi2-transform` and `boot.autologin.getty-tty1` by name.
It is a resource in everything except registration.

**The downsides of moving it in, and this is the item where they bite hardest.** Reaction time: five
seconds today, five minutes as a resource, and this is the mechanism whose entire purpose is to stop
a household staring at a broken desktop. **Ownership collision:** four graph resources already own the
units this loop stops and starts. Its teardown deliberately makes all four of them transiently wrong,
and the interlock window exists precisely to stop the reconciler reading that as drift — the file
records that without it *"the reconciler read the teardown's own act as drift and rebooted for it"*,
a measured defect. If the loop became a fifth resource acting on the same units, that laundering
becomes an Act that makes four peer resources wrong, which is the thing the interlock was invented to
paper over rather than a thing it solves. And there is a **new blocking risk for households**: today
this loop retries for ever and never escalates, which means a frame with a broken graphical stack
keeps trying indefinitely and never stops the pass. As a resource it would escalate after three
attempts and, by decision 68, stop everything — so a frame that cannot render the page would also
stop converging its camera, its speaker and its photograph settings. That is a real regression for
some failure modes and a real improvement for others.

**Honest opinion.** **Move the observation in and leave the teardown out.** A resource — or, more
cheaply, a gate — that says *"the product page has not checked in"* would give the census the one
row it most obviously lacks, without a fifth owner appearing on four units. I am medium-confidence
here, and specifically not confident that the *Act* should move: the teardown is a five-second
reflex, and reflexes do not belong in a five-minute loop.

---

## 5. The interrupted-flash latch — *move in as a gate*

*(Inventory C3. `src/FrameLink.Agent/Firmware/ArrayFlashWindow.cs`, checked at `AgentHost.cs:236-243`.)*

**What it is.** When the frame writes new firmware to its microphone array it first drops a small
marker file saying "a write is in progress". If the frame loses power halfway through, that marker is
still there when it starts again — which means the microphone hardware is in an unknown, possibly
half-written state. The marker deliberately does not clear itself; only a person deleting the file
clears it.

**Why it is not in the DAG.** It would be a textbook **gate**: something the agent can only find out,
with no Act on earth that fixes it. **The reason is recorded**, at `ArrayFlashWindow.cs:39-49` — the
marker is *"durable and deliberately not self-clearing … An agent that cleared it itself would be
free to start a second flash onto an array whose Upgrade partition is in an unknown state."* That
reason is entirely correct and it is an argument for the *marker's* behaviour. It is **not** an
argument about graph membership, and no such argument is recorded.

**The downsides of moving it in.** It would stop the pass on any frame in this state, which sounds
severe until you notice that the frame is *already* half-stopped: while the marker is present the
reboot boundary refuses every reboot (`RebootHold`, `AgentHost.cs:469-477`) and the update service
stands down (`:266`), so no resource requiring a reboot can converge anyway. Making it a gate does not
create a new blockage; it names one that already exists and is currently invisible. The one genuine
cost is that a frame in this state stops *reporting progress* on everything downstream, and today its
photographs keep showing. Weighed against that: a household whose microphone is in an unknown state
has a frame that cannot reliably take a call, which is half the product. This is not circular — the
gate's Observe is a file-existence check that needs nothing the graph provides.

**Honest opinion.** **Move it in as a gate.** Its only remedy is a human deleting a file, which is the
literal definition of the gate shape that landed in the code today, and its only surfaces right now
are a log line and a firmware screen that nobody watching the fleet will ever see. A frame with a
half-written microphone array should appear on the Fleet Manager as escalated, by name, immediately.

---

# Group 2 — Change shape (items 6–14)

Nine things that should stay outside the graph, but where something has to change before the current
answer is defensible: a reason that was never written down, a defect the justification leans on, or a
split that has moved since it was made.

---

## 6. The local HTTP server and its accept loop — *the fifteenth loop* — *change shape*

*(Inventory B1. `src/FrameLink.Agent/Local/LocalOrigin.cs:40`, started at `AgentHost.cs:332`.)*

**What it is.** The frame runs a tiny web server that only it can reach — nothing outside the frame
can connect to it. It serves the photograph page, the repair screen the agent narrates onto, and the
two-way message channel the page uses to say "I am alive", "somebody pressed reboot", "somebody
pressed retry" and "somebody agreed to the firmware write".

**Why it is not in the DAG.** The *server object* is **neither** — it is infrastructure. The
*observable fact* about it is already a **resource**, `app.http.local-origin`. **The reason is
recorded** and it is one of the best-argued in the repository (`AgentHost.cs:325-331`): *"Started here
and not only by its resource, because the server lives in this process and therefore cannot survive
the reboot every resource takes (§2.4)… Started at every process start, the resource becomes what it
should be: an assertion that the origin is answering, with an Act that retries the bind for the one
case that can actually fail, a port somebody else holds."* That is a textbook non-circular split and
I would not touch it.

**What is wrong is something else entirely, and it is why this item is here.** The loop that accepts
incoming connections is started at `LocalOrigin.cs:160` as a fire-and-forget task. It runs for the
whole life of the process. **It is not one of the fourteen tasks the agent watches**, so if it throws,
nothing notices: the fault is parked inside a task object that is only ever awaited at shutdown
(`:184`). The agent goes on running, the Fleet Manager goes on reporting the frame online, and the
page, the liveness heartbeat, the reboot button, the retry button and the firmware answer all stop
working silently. This is the identical failure that cost a frame twenty-nine minutes on 2026-08-16
and produced `WhenAllOrFirstFaultAsync` in the first place — the wrapper was built, and this loop was
not put inside it.

**The downsides of changing it.** Adding the accept loop to the watched list means a fault in it takes
the whole agent down and systemd restarts it. That is a louder failure than today's silence, and on a
frame whose port 8888 is permanently held by something else it could interact with the start-rate
limiter (item 28's neighbourhood) and leave the unit `failed`. The bind failure is already handled
separately and reported by the resource, so the residual risk is small — but it is not zero and it is
the reason this is "change shape" rather than a one-line fix.

**Honest opinion.** **Leave the split exactly as it is and put the accept loop under supervision.**
This is the fifteenth long-lived loop on a frame and it is the only one nothing is watching; that is
an oversight rather than a design, and it silently disables five separate operator-facing behaviours
when it happens.

---

## 7. The console stage — *change shape*

*(Inventory A1. `src/FrameLink.Agent/Stage/ConsoleStage.cs:32`, run from `AgentHost.cs:685`.)*

**What it is.** Before there is any desktop or any photograph on screen, the frame shows a designed
text display that narrates what it is doing — what it has set up, what it is waiting for, what has
gone wrong. It is the entire screen from the first second of the first boot until the graphical
session exists, and it is what a household sees when something is broken.

**Why it is not in the DAG.** If it were in, it would be a **gate** — "the terminal is taking bytes"
is something the agent can find out and cannot fix. **There is no recorded reason anywhere**: not in
the file, not in the host, not in the design document. There is a strong argument that it *cannot* be
a resource — this stage is the surface on which a failed resource reports itself, so a resource
asserting "the console is painting" would have nowhere to report its own failure — and I believe that
argument. **But nobody wrote it down**, and "the terminal takes bytes" is a perfectly ordinary
device fact with a clean delta, so on the operator's own rule this is category (iii).

**The downsides of moving it in.** The circularity is real and it is the strongest possible reason to
leave something out, so it is worth stating precisely: the reconcile loop reports every resource's
status by publishing it, and publishing paints this screen. A gate that said "the console is not
writable" would be reporting that fact *through the console that is not writable*. The only place the
verdict could land is the Fleet Manager — which by §1.2.2 may not be reachable at all, and a frame
whose console has died is exactly the frame most likely to be the one nobody can reach. A gate here
would also stop the pass on every machine that has no console at all, which is every developer
workstation and every test run.

**What has to change, and it is not the classification.** The stage marks itself unwritable after the
**first** failed write and never retries (`ConsoleStage.cs:113-124`), and its loop then returns
outright (`:190-198`). The recorded reasoning — a console answering `EIO` *"is not coming back without
the panel overlay, and the overlay only takes at a reboot"* — is right for the case it was written
for and wrong for a transient. One momentary error on a working panel permanently ends all narration
for a process that may run for days. And by item 6's mechanism, a loop that *returns* is
indistinguishable from a loop that is working, so nothing anywhere records that the frame has gone
mute.

**Honest opinion.** **Keep it out of the graph, write the circularity down, and fix the permanent
demotion.** The classification is right and the reason for it being right is currently nowhere — which
means the next person to review this list will ask the same question again. The demotion is a defect,
not a design, and it deserves a retry and a reported status rather than a resource.

---

## 8. The console stage's repaint subscription — *change shape*

*(Inventory B8. `ConsoleStage.cs:94`.)*

**What it is.** The text screen does not poll for news. It registers a callback so that the instant
anything anywhere in the agent publishes a status change, the screen repaints immediately.

**Why it is not in the DAG.** **Neither** — a callback registration is not a setting and there is
nothing for a resource to assert. **No recorded reason**, because it carries item 7's unrecorded
decision; it is listed separately because it is a distinct thing the process starts and because it is
the exact mechanism by which a write to a character device ends up running on the reconcile loop's
own thread.

**The downsides of moving it in.** There is nothing to move. The real cost sits in item 9: this
subscriber does I/O to a device, and the publishing machinery runs subscribers synchronously on
whichever thread published. So a `/dev/tty8` write that blocks blocks the reconcile loop in the middle
of a pass. The firmware progress code knows this and routes around it deliberately
(`Firmware/ArrayFlashProgress.cs`, class remark) — *nothing else does*.

**Honest opinion.** **Not a graph question; fix it as part of item 9.** The subscription itself is
fine. What is not fine is that the one code path in the product that understood this hazard solved it
locally instead of at the hub.

---

## 9. The status hub — *change shape*

*(Inventory D5. `src/FrameLink.Agent/State/AgentStatusHub.cs`, constructed at `AgentHost.cs:143`.)*

**What it is.** A small in-memory noticeboard. Any part of the agent can post the current state of the
frame to it, and anything that cares — the text screen, the photograph page, the Fleet Manager
reporter, the memory of what was last true — is told immediately.

**Why it is not in the DAG.** **Neither**, unambiguously: it is the graph's own reporting surface, and
a resource asserting the health of the thing through which every resource reports would have nowhere
to report. **The reason is not written as a quotable sentence** but the classification is not in
serious doubt. Nine distinct things publish to it and eight of them are outside the graph.

**The downside is not about moving it in; it is that publishing runs subscribers on the publisher's
thread.** Two subscribers do real work: the console does a character-device write (item 8), and the
remembering subscription does a file write (item 10). Both therefore execute on the reconcile loop's
thread whenever the loop publishes, which is many times per pass. Either one blocking blocks the
pass, and a blocked pass is the one failure mode this product treats as worse than a failed one —
*"a hung pass is worse than a failed one, because nothing on the screen ever changes to say so"*
(`Hosting/IProcessRunner.cs:83-86`).

**Honest opinion.** **Stay out of the graph; change the delivery.** Subscribers should not run on the
publisher's thread when they perform I/O — that is a five-line change that removes two of the six
ways a frame can end up with nothing on the screen changing, and it retires the special-case
workaround the firmware progress pump had to invent.

---

## 10. The remembering subscription — *change shape*

*(Inventory B7. `AgentHost.cs:170`, feeding `memory.RememberAnswer`.)*

**What it is.** Every time the frame learns something authoritative from its Fleet Manager, that
answer is written to disk. It is what lets a frame that was fully healthy when contact dropped carry
on behaving as if it were healthy after a power cut, instead of spending its first half-minute showing
a repair screen.

**Why it is not in the DAG.** **Neither** — it is state persistence, and there is nothing here a
resource could assert that `agent.adoption` does not already assert. **No recorded reason as such**,
and none is really needed for the classification; what *is* missing is any recorded awareness that
this handler performs disk I/O inside a synchronous publish.

**The downsides of moving it in.** None worth discussing — there is no delta and no Act. The problem
is again item 9: this is a disk write on the hot path of every status change, on the reconcile loop's
thread. It is small and usually fast. On a frame with an ailing SD card it is neither.

**Honest opinion.** **Stay out; move the write off the publisher's thread with item 9.** This is the
second of the two subscribers that make the synchronous hub dangerous, and neither of them needs to be
synchronous to be correct.

---

## 11. The touch retry reader — *change shape*

*(Inventory A13. `src/FrameLink.Agent/Local/TouchRetry.cs`, run from `AgentHost.cs:716`.)*

**What it is.** If a frame has given up — tried something three times, failed, and stopped — somebody
standing in front of it can press and hold anywhere on the screen for three seconds and the frame will
start trying again. It is the only way to un-stick a frame without the Fleet Manager. Since the
firmware work it also carries the five-second hold that means "yes, write the firmware".

**Why it is not in the DAG.** If it were in, it would be a **gate**: "there is a working touchscreen
on this frame" is something the agent can find out and cannot fix. The observable fact is genuinely
clean — the input device exists, opens, and reports touch events. **There is no recorded reason
anywhere.** The file argues at length why a hold rather than a button and why a hold rather than a
tap; it never says why the touchscreen's readability is not a resource.

**The downsides of moving it in — and this is where the circle is.** A gate that fails stops the pass;
a stopped pass is un-stopped by holding the screen; and the gate would only have failed because the
screen cannot be held. So the gate's escalation removes the only affordance that clears the
escalation. It is not fatal — the Fleet Manager's retry also clears it — but on a frame with no
network, which is precisely the case §1.2.2 designs for, it is exactly fatal. Weigh against that what
moving it in would *cost a household*: a frame with a dead touch digitiser still shows photographs
perfectly well, and a gate would stop it converging anything at all over an input device nobody in
the house has tried to use.

**Honest opinion.** **Keep it out of the graph, but stop being silent about it: report it.** "Is there
a working touchscreen on this frame?" belongs on the census as a reported fact, not as something that
stops the pass — a broken touchscreen should never be a reason a household's photographs stop
updating. I am medium-confidence: a reasonable person could argue that a frame which has lost its only
local recovery path *should* shout, and I would not fight hard against making it a gate.

---

## 12. The array firmware reporter — *change shape* ⚠ *in flight*

*(Inventory A10. `src/FrameLink.Agent/Telemetry/ArrayFirmwareReporter.cs:60`, run from
`AgentHost.cs:704`.)*

**What it is.** The microphone array in the frame is a small computer of its own with its own
software. Twice every six hours this reads which version that software is, by two independent routes,
and reports both — plus whether the two agree. It never writes one.

**Why it is not in the DAG — and this answer is already moving.** It would be **neither**, now. **The
reason is recorded verbatim** (`ArrayFirmwareReporter.cs:14-24`, decision 90): *"The array's firmware
version fails the second half of [Observe → Compare → Act → Verify] and cannot be made to pass it: the
only Act that could converge it is a DFU write, the operator has decided this product will never
perform one unattended, and a resource whose Act cannot succeed is exactly what decision 63
diagnosed."* **That reasoning was overtaken today.** `IResource.IsGate` now exists specifically to
carry resources whose Act cannot succeed without paying three attempts and three reboots for the
privilege, and its own remark says decision 90's objection *"was an objection about waste rather than
about the verdict"*. And the graph already acquired `firmware.xvf3800.recognised` this morning, which
runs the same hardware ladder that reads the same firmware version through the same tool.

**The downsides of moving it in.** The version *by itself* still should not be a gate — a frame whose
array runs an older-but-known firmware works, and stopping the pass over it would block a household's
photographs over a number. But this reporter now duplicates work the new gate already does: both read
the array through `xvf_host`, and both take the same process-wide lock (item 14). Its own remark
notes it *"explicitly refuses a setting"* for its six-hour interval, so the duplication cannot even be
tuned away.

**Honest opinion.** **Fold its observation into the recognition gate's Observe and keep reporting the
version without gating on it.** Two things outside the graph reading the same hardware through the
same single-slot lock, when one of them is now inside the graph, is a coupling that will bite. I am
medium-confidence only because this whole area is being rewritten as I write.

---

## 13. The array firmware flash and its consent screen — *change shape* ⚠ *in flight, actively*

*(Inventory A11. `src/FrameLink.Agent/Firmware/ArrayFirmwareFlash.cs`, run from `AgentHost.cs:709`,
with `ArrayFlashApproval`, `ArrayFlashWindow`, `ArrayFlashProgress` and `ArrayHardwareGate`.)*

**What it is.** The one code path in the whole product that can rewrite the microphone array's
software. It reads a per-frame authorisation naming a specific image, marks that authorisation as
used before it starts anything, runs a ladder of hardware checks, puts a full-screen message in front
of whoever is standing at the frame and asks them to hold for five seconds, writes the firmware, and
then verifies by watching the device disappear and come back on the USB bus.

**This item is being rebuilt right now and nothing below describes it as settled.**
`ArrayFirmwareFlash.cs` has several hundred uncommitted lines of change in the working tree;
`XvfFirmwareRelease.cs` has been largely emptied; a new untracked `XvfFirmwarePin.cs` now defines the
pinned image once and is compiled into both the agent and the Fleet Manager across a project
boundary; and the `Fallback` and `Recovery` image roles were deleted today along with the recovery
kit. The hardware ladder went from nine rungs to ten this morning. The consent screen was last
touched today.

**Why it is not in the DAG.** The *write* would be **neither** — it is an attended, single-use,
non-repeatable operation. Its **preconditions** are **gates**, and some are already resources. **The
reason is recorded at length** (`ArrayFirmwareFlash.cs:217-219` in the current tree): a firmware
resource on a frame nobody has authorised *"would drift, spend three attempts and three reboots,
escalate, and by decision 68 stop the whole pass — so a frame carrying a factory array would never
converge its screen, its camera or its speaker over a number nobody had agreed to write."*

**The downsides of moving the write in — four, and they are not equal.** The inventory says all four
are answerable; I agree with three and not the fourth. (a) *Every Act is followed by a reboot* — a DFU
write must never be crossed by one, and today an outside mechanism refuses the reboot; as a resource
the Act *is* followed by one. Answerable, with an interlock the loop would have to learn. (b) *Three
attempts* — the flash is single-use **by construction**: the authorisation is spent before the write
tool starts, so retrying is not merely wasteful, it is structurally impossible without a second
authorisation. A retry ladder is the opposite of this design, and this is the objection I do **not**
think is cleanly answerable. (c) *The Act cannot succeed unattended* — this one **has been answered
since the inventory was written**, by the gate flag. (d) *The consent screen owns the panel for thirty
minutes at a time* — a resource cannot hold a screen across passes, and the loop has no concept for
it.

**Honest opinion.** **Keep the write out; keep converting its preconditions into gates, which is what
is already happening.** The recognition ladder moved in today and that was the right call; the pinned
image is already a resource; the tool being installed is already a resource. What is left outside is a
single-use, human-authorised, non-retryable, reboot-hostile action, and that is not a resource in any
shape the loop currently has. Medium confidence, purely because the ground is moving — I would ask
again once the current work lands.

---

## 14. `XvfHost.Conversation`, the process-wide array semaphore — *change shape*

*(Inventory D3. `src/FrameLink.Agent/Resources/AudioArrayResources.cs:670`, used at `:691`.)*

**What it is.** The tool that talks to the microphone array has no way to say *which* array it means,
so two overlapping conversations with it lose the connection and the array reads as absent. A single
process-wide lock makes sure only one conversation happens at a time.

**Why it is not in the DAG.** **Neither** — a lock is not a setting and there is nothing to assert.
**The reason is recorded** and is unusually candid (`:664-669`): the wait is unbounded on purpose,
because the process runner *"already awaits that with no timeout wherever it is called from — so a
hung tool wedges the caller today, with or without this gate, and a bounded wait here would buy
nothing except a second way to report a working array as absent."*

**The downsides — and the reason this is here.** That justification is true and it rests entirely on a
**defect** rather than on a property. The process runner has no timeout at all
(`Hosting/IProcessRunner.cs`), and the moment that is fixed the argument for an unbounded wait has to
be rewritten from scratch. Meanwhile this lock is a hard coupling that crosses the graph boundary in
the dangerous direction: a hung tool invocation from the firmware *reporter* (item 12) or the *flash*
(item 13) — both outside the graph — holds the lock and stalls the reconcile pass's audio resources
indefinitely, with nothing on the screen changing to say so. Now that `firmware.xvf3800.recognised` is
inside the graph and also takes this lock, there are three callers on two sides of the boundary.

**Honest opinion.** **Stay out — it cannot be anything else — but the recorded justification is
borrowed and has to be repaid.** Bound the process runner, then bound this wait. A lock whose defence
is "something else is already broken in the same way" is a defence with an expiry date, and the thing
it protects is now shared across the boundary three ways.

---

# Group 3 — Stay out (items 15–41)

Twenty-seven things where the current answer is right. Each still gets the same four parts, because
the operator asked to be able to decide each one rather than take my word for a block of them — but
where the answer is genuinely obvious, the entry is short and says so.

---

## 15. The control link to the Fleet Manager — *stay out*

*(Inventory A2. `src/FrameLink.Agent/Link/ControlLink.cs:26`, run from `AgentHost.cs:686`.)*

**What it is.** The frame's phone line home. It dials out to the Fleet Manager, keeps the connection
open, and retries for ever with a growing delay if nobody answers. Everything the operator sends a
frame, and everything a frame reports, travels on it.

**Why it is not in the DAG.** **Neither** — this is the operator's own named example of infrastructure
the graph runs on. The graph reports *through* it, and the fact that is worth asserting about it is
already a resource: `agent.adoption`. **The reason is recorded** by construction throughout §4.1 and
in the host's own comment that the reconciler *"runs from the first second whether or not anything
ever answers"* (`AgentHost.cs:674-677`).

**The downsides of moving it in.** **Directly circular.** A link resource that could not converge
would block the frame from provisioning whenever the server was unreachable — which is the exact
inversion §1.2.2 forbids and which `Resources/DeviceCatalog.cs:203-212` already warns about for
`agent.version`. A frame must set itself up with nobody listening; a resource that requires somebody
listening in order to set up cannot be part of that.

**Honest opinion.** **Stay out. This one is not a close call in any direction.**

---

## 16. The agent status reporter — *stay out*

*(Inventory A3. `src/FrameLink.Agent/Link/AgentStatusReporter.cs:54`, run from `AgentHost.cs:692`.)*

**What it is.** Turns whatever the reconciler is currently doing into one plain sentence and sends it
to the Fleet Manager whenever it changes. It is why an operator's dashboard row says something more
useful than "online".

**Why it is not in the DAG.** **Neither** — it is the reporting path itself. **The reason is recorded**
at `AgentStatusReporter.cs:46-52`, and the sentence there is load-bearing: *"a Fleet Manager that has
stopped answering therefore leaves a socket blocked here and an array being written to entirely
undisturbed."*

**The downsides of moving it in.** Nothing to gain and a clean circle to create: it would be a resource
whose observation is the state of every other resource, reporting itself through itself.

**Honest opinion.** **Stay out.**

---

## 17. The update service — *stay out*

*(Inventory A4. `src/FrameLink.Agent/Update/UpdateService.cs:62`, run from `AgentHost.cs:693`.)*

**What it is.** Once an hour the frame checks whether the version of the agent it is running matches
the version it is supposed to run, and if not it fetches the right one, checks it is genuine, swaps it
in and restarts itself. It moves in both directions — upgrade or downgrade — whichever matches.

**Why it is not in the DAG.** **Neither**, and this is the subtlest split in the whole inventory. The
*version* already **is** a resource — `agent.version` is the root of the entire graph — and this loop
is that resource's mechanism, wired in as `ConvergeVersion = updates.TriggerNow` (`AgentHost.cs:450`).
**The reason is recorded** at `AgentHost.cs:446-449`: *"the resource asks, the hourly loop does, and
correctness never depends on the ask arriving."*

**The downsides of moving it in.** **Circular, and it is the most consequential circle in the
product.** If the update only happened when a pass acted on it, then a pass stopped by an escalation
(decision 68) would also stop the frame from ever receiving the fix that unstops it. Every frame in
the fleet that escalated on a bug would become unreachable by the release that fixes the bug. The
hourly tick being the mechanism, and the socket being merely an optimisation, is what makes a stopped
frame recoverable at all.

**Honest opinion.** **Stay out. This is the single strongest "leave it out" argument in the list and it
is already written down correctly.**

---

## 18. The reconcile loop itself — *stay out*

*(Inventory A5. `src/FrameLink.Agent/Reconcile/ReconcileLoop.cs:460`, run from `AgentHost.cs:694`.)*

**What it is.** The thing this whole document is about. It walks all 82 settings in dependency order,
checks each one, changes at most one per pass, reboots, and verifies.

**Why it is not in the DAG.** **Neither.** It is the DAG's driver. **Recorded** by construction.

**The downsides of moving it in.** It would have to observe itself in order to run, which is not a
downside so much as a category error.

**Honest opinion.** **Stay out.**

---

## 19. The supervisor — §2.10's five behaviours — *stay out*

*(Inventory A6. `src/FrameLink.Agent/Supervise/Supervisor.cs`, run from `AgentHost.cs:695`.)*

**What it is.** Five small housekeeping habits that keep the frame alive rather than correct: restart
the browser if it eats too much memory, restart it once a night at three in the morning, notice if it
has stopped responding, recycle the camera when a call ends, refresh a stale page. I confirmed all
five are present (`Supervisor.cs:128-146`: memory watchdog, daily restart, kiosk liveness, camera
recycle, page refresh).

**Why it is not in the DAG.** **Neither**, and **it carries the longest recorded reason in the
repository**. §2.10: *"Supervision does not stop the product, and that is the whole reason it is not a
resource… Modelling supervision as drift would force the two rules into collision and one of them
would have to yield: either drift stops being absolute (correctness lost) or a routine browser blink
blanks the frame, kills the call and shows a repair screen every morning at 03:00 (continuity
lost)."* Restated in code at `Supervisor.cs:93-95`: *"Not resources, and the reason is a collision
rather than a taxonomy."*

**The downsides of moving it in.** Concretely: **a repair screen in every household at three in the
morning**, and a frame that needs restarting in order to stay alive would, on restarting, stop. It
would also lose the rate-based fault signal, which is deliberately diagnostic rather than inhibitory —
supervision counts how often it has had to intervene and reports that, rather than refusing to
intervene.

**Honest opinion.** **Stay out. The argument is complete, recorded twice, and correct.**

---

## 20. The package inventory reporter — *stay out*

*(Inventory A9. `src/FrameLink.Agent/Telemetry/PackageInventoryReporter.cs:53`, run from
`AgentHost.cs:703`.)*

**What it is.** Every six hours the frame lists all of the roughly nine hundred and thirty pieces of
software the operating system has installed, and sends the list — but only if it has changed since
last time.

**Why it is not in the DAG.** **Neither**, and **the reason is recorded verbatim** (`:13-20`): *"§2.2's
unit is 'the smallest independently verifiable setting', and the ~930 packages on a frame are not a
setting at all — nothing declares them, nothing converges them, and a resource that asserted the
closure would report drift every time Debian re-cut a dependency. What the operator asked for is
visibility, so this observes and reports and never acts."*

**The downsides of moving it in.** It would gain nothing except the ability to **stop a household's
photographs every time Debian re-cuts a dependency** — a change nobody in this project made, cannot
prevent, and would have to clear by hand. The fifteen packages that *are* declared already have
resources of their own.

**Honest opinion.** **Stay out.** This is visibility, and visibility is correctly not correctness.

---

## 21. The call button watch — *stay out*

*(Inventory A12. `src/FrameLink.Agent/Local/ButtonWatch.cs:74`, run from `AgentHost.cs:710`.)*

**What it is.** The physical button on the frame that starts a call. A small piece of the agent holds
a permanent claim on that button's wire for as long as the agent runs, and turns a press into a
message.

**Why it is not in the DAG.** **Neither** — and this is already the cleanest split in the inventory.
The **claim** is a resource, `gpio.button.line`, and the resource's Act is to ask this watcher to
re-arm. **The reason is recorded** at `AgentHost.cs:373-379`: *"It holds the GPIO line for as long as
the agent runs … Created before the catalog because `gpio.button.line` observes this claim."*

**The downsides of moving it in.** A resource cannot hold a wire, because §2.4 reboots after every Act
— so the holding must live in something that persists, and the graph drives it rather than the
reverse.

**Honest opinion.** **Stay out. This is the pattern items 2 and 4 should copy.**

---

## 22. The Immich Kiosk child process — *stay out*

*(Inventory A14. `src/FrameLink.Agent/Kiosk/KioskProcess.cs:228`, run from `AgentHost.cs:724`.)*

**What it is.** The program that actually chooses and shows the photographs is a separate piece of
software written by somebody else. The agent runs it as its own child and starts it again whenever it
exits.

**Why it is not in the DAG.** **Neither**, and **the reason is recorded and names its own sibling**
(`KioskProcess.cs:224-231`): *"Started from the host, not only from its resource, for the same reason
`LocalOrigin` is: the child is this process's child, so it cannot survive the reboot every resource
takes (§2.4). If starting it were left to the Act, the resource would find it down on every boot, act,
reboot and find it down again — a loop that never converges."* The reportable half already is a
resource, `kiosk.process.supervised`.

**The downsides of moving it in.** **Explicitly circular**, and the circle is spelled out above: every
Act reboots, every reboot kills the child, so the resource is drifted again the moment it converges.
A permanent reboot loop on every frame in the fleet.

**Honest opinion.** **Stay out.**

---

## 23. The local channel and its four handlers — *stay out*

*(Inventory B2. `src/FrameLink.Agent/Local/LocalChannel.cs`, wired at `AgentHost.cs:318-365`.)*

**What it is.** The two-way message path between the photograph page on screen and the agent behind
it. It carries the page's "I am alive" heartbeat and four things a person can do: reboot now, try
again, agree to the firmware write, and tell the agent a call has ended.

**Why it is not in the DAG.** **Neither** — it is a transport, and it says so at
`LocalChannel.cs:6-11`, deliberately not a second control protocol. **Recorded.**

**The downsides of moving it in.** Nothing to assert that item 6's `app.http.local-origin` does not
already assert. Worth noting what it *does* do to the graph, though, because it is real: three of its
four gestures reach into a running pass — the reboot press shortens a countdown mid-Act, the retry
press resets budgets under a running walk, and the firmware answer approves a write that then holds
the reboot boundary. The host records that the retry *"runs on the receive loop and touches nothing
but the journal … the worst case is one extra pass before it takes effect"* (`AgentHost.cs:576-580`).

**Honest opinion.** **Stay out.**

---

## 24. The array flash progress pump — *stay out* ⚠ *in flight*

*(Inventory B3. `src/FrameLink.Agent/Firmware/ArrayFlashProgress.cs:441`, launching a task at `:467`.)*

**What it is.** While the microphone firmware is being written, this repaints a progress bar once a
second — percent, bytes, seconds elapsed — so that a bar which is not moving reads as a wait rather
than as a crash.

**Why it is not in the DAG.** **Neither** — it is a rendering detail of item 13. **The reason is
recorded** in its class remark, which lists four structural properties that keep a hung noticeboard, a
dead network or a screen that will not repaint from ever reaching the write. The most important is
that the writing thread never publishes at all, precisely because publishing runs subscribers on the
caller's thread — this is the one place in the product that understood item 9's hazard and designed
around it.

**The downsides of moving it in.** A progress bar is not a setting. There is no delta and no Act.

**Honest opinion.** **Stay out** — and note that this file is uncommitted right now, so the specifics
above may shift, though not the classification.

---

## 25. The `gpiomon` child process — *stay out*

*(Inventory B4. `src/FrameLink.Agent/Hosting/IGpioLines.cs:166`.)*

**What it is.** A tiny stock Linux program that the agent runs to actually watch the button's wire.
One line of its output is one press.

**Why it is not in the DAG.** **Neither** — it is item 21's implementation, and the claim it produces
*is* the resource. **Recorded** by item 21's quoted reason.

**The downsides of moving it in.** Same circle as item 21: a resource cannot hold a wire across a
reboot.

**Honest opinion.** **Stay out.**

---

## 26. The SIGUSR1 simulated button press — *stay out*

*(Inventory B5. `AgentHost.SimulatedPress:867`, registered at `:383`.)*

**What it is.** A way for a person with a terminal to make the frame behave exactly as if the physical
call button had been pressed, for testing, without touching the frame.

**Why it is not in the DAG.** **Neither** — a signal handler is not a setting and has no state to
compare. **Recorded**, and the recorded part that matters is its failure mode: on a platform where the
handler cannot be registered the agent *"says so once and carries on, because a missing test
affordance must never be a reason for a frame not to start"* (`:859-862`).

**The downsides of moving it in.** A test affordance that could stop a household's photographs is
strictly worse than no test affordance.

**Honest opinion.** **Stay out.**

---

## 27. The SIGTERM and Ctrl+C handlers — *stay out*

*(Inventory B6. `Program.CreateSignalHandler:89`, `Console.CancelKeyPress` at `:27`.)*

**What it is.** How the frame is told to shut down cleanly — by the system at power-off, or by a person
at a keyboard.

**Why it is not in the DAG.** **Neither** — process host. **Recorded** by construction.

**The downsides of moving it in.** They end everything, which is their job; a resource that asserted
the ability to end everything would be asserting it through the machinery it would end.

**Honest opinion.** **Stay out.**

---

## 28. The device keypair load-or-create — *stay out, but it has a real defect*

*(Inventory C1. `src/FrameLink.Agent/Identity/DeviceKeyStore.cs`, called at `AgentHost.cs:123`.)*

**What it is.** Every frame has a permanent cryptographic identity, created the first time it ever
boots and never changed afterwards. It is how the Fleet Manager knows one frame from another.

**Why it is not in the DAG.** Split, and **both halves are recorded**. The *observable* half already is
a resource — `agent.keypair` compares the identity against what the process is actually running as.
The *generation* is **neither** and cannot be a resource: identity is permanent by §3.3, and the
refusal is recorded at `AgentHost.cs:131-132` — *"Refusing to generate a new identity — that would
silently orphan this frame from its Fleet Manager."*

**The downsides of moving it in.** A resource that regenerated a key on drift would be exactly the
failure the refusal exists to prevent: a frame that vanishes from its own fleet and reappears as a
stranger.

**The defect, which is not a graph question but belongs on the same review.** This runs at
`AgentHost.cs:123`; the text screen is not constructed until `:182`. A frame whose key cannot be read
exits before there is anything on the screen at all, then does it again on every restart until the
start-rate limiter gives up and leaves the unit `failed` — with **no narration at any point**. The
household sees whatever was last on the panel, for ever.

**Honest opinion.** **Stay out, and move the screen earlier than the key.** The classification is right;
the ordering is a genuine hole, and it is the one failure mode that produces a completely silent dead
frame.

---

## 29. Journal read, memory read, and the resumed condition — *stay out*

*(Inventory C2. `AgentHost.cs:139-141`.)*

**What it is.** At startup the frame reads back what it believed about itself last time: the last
answer from the Fleet Manager, its settings, its name, and — importantly — whether this frame has
*ever* been fully healthy, which decides whether it behaves like a frame in trouble or a frame still
being set up.

**Why it is not in the DAG.** **Neither** — it is the graph's memory, read before the graph runs.
**Recorded** by construction, including the reason it happens before the first paint: it is why a
power cut does not spend its first half-minute showing a repair screen (`:148-151`).

**The downsides of moving it in.** The graph would have to read its own memory using the graph, which
has not yet read its memory.

**Honest opinion.** **Stay out.**

---

## 30. Endpoint resolution — *stay out*

*(Inventory C5. `AgentHost.ResolveEndpointsAsync:887`, called at `:221`.)*

**What it is.** Working out, once, which Fleet Manager this particular frame belongs to — from a flag
left at install time, from a file on the boot partition, or by listening on the local network for a
couple of seconds.

**Why it is not in the DAG.** **Neither**, and **the reason is recorded by reference**: §4.3's "never
rediscover" — the answer is persisted and authoritative, and `Program.cs:52-57` records the same rule
for the install path.

**The downsides of moving it in.** A resource that re-resolved would be a frame that could be
re-homed by anything shouting on the local network, which is the security property §4.3 exists to
protect. Worth noting one real cost that exists today: the two-second listening window delays the
first useful paint, and the code already works around it by painting once before this runs (`:208`).

**Honest opinion.** **Stay out.**

---

## 31. The `version` verb — *stay out*

*(Inventory C7. `Program.cs:41-43`.)*

**What it is.** Type `fl-agent version`, it prints its version, it exits.

**Why it is not in the DAG.** **Neither.** It is a command-line convenience that runs instead of the
agent rather than alongside it.

**The downsides of moving it in.** There is nothing to move.

**Honest opinion.** **Stay out.** It is in the inventory for completeness and needs no decision.

---

## 32. The supervision interlock — *stay out*

*(Inventory D1. `src/FrameLink.Agent/Supervise/SupervisionInterlock.cs:49`, consumed mid-walk at
`ReconcileLoop.cs:854-874`.)*

**What it is.** The referee between the reconciler and the housekeeping habits of item 19. It stops
the reconciler from treating a deliberate browser restart as something broken, and stops the
housekeeper from touching anything the reconciler is currently working on.

**Why it is not in the DAG.** **Neither** — it *is* the mechanism §2.10 describes, and **recorded as
such**. It is also the one pair of interactions in the whole inventory that is fully designed rather
than discovered.

**The downsides of moving it in.** A resource that mediated between the graph and something outside
it would have to be consulted by the graph while the graph was deciding whether to consult it.

**Honest opinion.** **Stay out.**

---

## 33. The reboot boundary stack — *stay out*

*(Inventory D2. `Reconcile/IRebootBoundary.cs`, assembled at `AgentHost.cs:468-478`.)*

**What it is.** Three layers of "should we really reboot now", outermost first: a firmware write in
progress refuses; then a limit of one hundred and twenty reboots in any rolling six hours refuses;
then the frame actually reboots.

**Why it is not in the DAG.** **Neither**, and **the reason is recorded** at `IRebootBoundary.cs:285`:
the floor *"is deliberately not a resource, and can therefore never be the thing that triggers a"*
reboot. The deeper argument is §2.4's: the attempt ladder counts *failures*, and the cycle this bounds
is made of *successes* — a hundred and twenty successful reboots is a different pathology from three
failed attempts and needs a different brake.

**The downsides of moving it in.** A brake that could itself demand a reboot is not a brake.

**Honest opinion.** **Stay out.**

---

## 34. The telemetry outbox and uplink — *stay out*

*(Inventory D4. `Telemetry/TelemetryOutbox.cs:41`, `Link/AgentUplink.cs`, at `AgentHost.cs:270-271`.)*

**What it is.** A small on-disk queue. When the frame has something to tell the Fleet Manager and
nobody is listening, it writes it down — up to five hundred events — and sends the backlog when the
connection comes back.

**Why it is not in the DAG.** **Neither** — reporting path. **Not recorded as a quotable sentence**,
but the classification is not in doubt: it is the buffer in front of item 15, which is itself the
operator's named example.

**The downsides of moving it in.** It would be a resource whose job is to report the failure of the
thing that reports resources.

**Honest opinion.** **Stay out.**

---

## 35. The reboot countdown — *stay out*

*(Inventory D6. `Reconcile/RebootCountdown.cs`, constructed at `AgentHost.cs:345`.)*

**What it is.** The "rebooting in 30…29…28" the frame shows before it restarts, repainted five times a
second, with a button on the page that skips the wait.

**Why it is not in the DAG.** **Neither** — it is part of the loop's own reboot path, not a setting
about the frame. **Not recorded as a quotable sentence**; the classification is obvious.

**The downsides of moving it in.** A resource whose Act is to display a countdown before the reboot
that follows every Act is a recursion, not a resource.

**Honest opinion.** **Stay out.**

---

## 36. The embedded product app — *stay out*

*(Inventory E1. `app/frame-app.js`, `app/frame-stage.js`, `app/livekit.js` and siblings, served by
item 6.)*

**What it is.** The actual page a household sees: the photograph slideshow, the video-call screen, and
the plain-language explanation the agent narrates when something is wrong. It ships inside the agent's
own binary.

**Why it is not in the DAG.** **Neither** — it *is* the product, not a setting about the product. Its
configuration already is resources (`app.config.*`, five of them). **Not recorded as a single
sentence**, and none is needed.

**The downsides of moving it in.** Worth naming one real asymmetry rather than dismissing this: **the
page holds a deciding vote the agent does not.** It refuses a reload while it is in a call and takes
one when the call ends, because the agent's own copy of "a call is in progress" is up to one heartbeat
stale. A resource asserting the page's state would be asserting something the page knows better than
the graph does, up to fifteen seconds late.

**Honest opinion.** **Stay out.** The right unit of reconciliation here is the page's *configuration*,
and that is already in.

---

## 37. `chromium-kiosk` and `framelink-camera` user units — *stay out*

*(Inventory E2. systemd user units on the frame; their content and enablement are already six
resources.)*

**What it is.** Two background services inside the graphical login session — one runs the browser that
shows the photographs, one runs the camera. Both are configured to restart themselves if they die.

**Why it is not in the DAG.** Split and **recorded**. Their *content* and *enablement* are already
resources (`unit.chromium-kiosk.content`, `unit.chromium-kiosk.enabled`,
`unit.chromium-kiosk.running-matches-content`, `unit.framelink-camera.content`,
`unit.framelink-camera.enabled`). Their *running* is supervised by item 19 and torn down by item 4.
Nothing owns their lifetime except systemd, and that is deliberate — recorded at
`KioskProcess.cs:205-219`, which contrasts them explicitly with the kiosk child of item 22.

**The downsides of moving their lifetime in.** The same collision as item 4: five owners of two units,
with the reconciler reading every legitimate restart as drift.

**Honest opinion.** **Stay out.** The half worth asserting is already asserted, three ways.

---

## 38. `getty@tty1` — *stay out*

*(Inventory E3. The stock Linux login prompt on the first terminal.)*

**What it is.** The plain text "login:" prompt Linux shows on a screen when nothing else is using it.
On a frame it is deliberately left running, because a person with a keyboard needs a way in.

**Why it is not in the DAG.** **Neither** — it is a stock OS component this product deliberately does
not own. **Recorded**, in §2.7: *"That getty is left untouched, so the physical login §5.5 depends on
is still there while the agent narrates."* The unit file additionally records the
`Conflicts=getty@tty1.service` that was considered and rejected. Its habit of repainting its prompt
over whatever shares its terminal is the entire reason item 2 exists.

**The downsides of moving it in.** A resource that asserted the login prompt's state would collide
with item 4, which stops and starts it during a teardown — and asserting a stock daemon nobody
declared is the same category error item 20 records for packages.

**Honest opinion.** **Stay out**, and note that its interaction with item 2 is one more reason item 2
deserves a name in the graph.

---

## 39. `unattended-upgrades` and the apt timers — *stay out, with one genuinely open question*

*(Inventory E4. Configured by `apt.auto-upgrades-enabled` and
`apt.unattended-upgrades.allowed-origins`; driven by system timers the agent does not touch.)*

**What it is.** Debian's own mechanism for installing security updates by itself on a schedule. The
frame configures which sources it will accept and that it should run, then leaves it alone.

**Why it is not in the DAG.** The *policy* already is two resources. The *timers that run it* are
**neither** by the current design. **The reason is recorded**, though indirectly: this is *"the only
thing on a converged frame that moves a package"*, which is the sizing argument for item 20's
six-hour cadence (`PackageInventoryReporter.cs:62-68`).

**The downsides of moving the timers in.** Ordinary drift-detection cost, plus one real hazard: a
resource asserting that the apt timers are enabled would, on drift, act — and acting on apt machinery
mid-transaction is how a package database gets wedged.

**Honest opinion, and this is the one I genuinely do not have a position on.** I found a gap I cannot
size: **the frame reconciles the apt *configuration* but nothing on the frame ever asserts that the
timers which consume that configuration are actually enabled and running.** A frame whose
`apt-daily.timer` had been disabled would have a perfectly converged, green, fully-reported
configuration and would silently never receive another security update. Whether that matters enough to
be a gate depends on how the operator weighs a slow silent security drift against another thing that
can stop a household's photographs, and I do not know the answer. **I would put this to the operator
as an open question rather than a recommendation.**

---

## 40. WirePlumber and PipeWire — *stay out*

*(Inventory E5. Stock audio daemons running inside the login session.)*

**What it is.** The standard Linux sound system. It decides which microphone and which speaker
programs actually get, and it rebuilds that picture whenever hardware appears or disappears.

**Why it is not in the DAG.** **Neither** — stock OS components. Their *configuration* already is
resources (`wireplumber.conf.camera-monitors-disabled`, `audio.wireplumber.playback-volume`,
`wireplumber.service`). **Not recorded as a single sentence**, but the graph already contains the
consequence of their behaviour: `Resources/MediaGraphGate.cs` exists inside the graph specifically to
stop a pass reading an audio picture that WirePlumber has not finished building.

**The downsides of moving them in.** They are a second owner of the sound mixer, running on their own
schedule, and a resource asserting their behaviour would be racing them — which is the exact race the
media-graph gate already exists to avoid rather than to fight.

**Honest opinion.** **Stay out.** The interesting half is already handled, and handled well.

---

## 41. The workstation bench harness — *stay out*

*(Inventory E6. `tools/harness`, on the workstation, not on the frame.)*

**What it is.** The set of tools on a developer's own computer used to build and test frames on the
bench. One of its jobs during firmware work is to refuse to cut the power to a frame that is
mid-write.

**Why it is not in the DAG.** **Neither**, and by construction: it does not run on a frame at all.
**Recorded** — decision 91 names it as *"the one interlock that lives on the workstation"*
(`ArrayFlashWindow.cs:31-37`), and it works by reading the same durable marker item 5 writes.

**The downsides of moving it in.** The graph runs on a frame; this runs on a laptop that may not be
connected to anything. There is no shared clock, no shared filesystem and no shared process.

**Honest opinion.** **Stay out.** It is listed only so that the operator's inventory of firmware
interlocks is complete — and the useful fact is that **one of them is not on the frame**, which is
worth remembering the next time somebody counts them.

---

# Two things I would raise that are not on the list of 41

Neither is a candidate resource, so neither gets a number; both came out of verifying the inventory
rather than reading it.

**The `running` list's own comment is wrong by two, and in the direction that hides the newest
loops.** `AgentHost.cs:674` still says *"Twelve loops now"* and the capacity hint below it still says
thirteen; the list holds fourteen. The two missing are the array firmware reporter and the array
flash — items 12 and 13, which are also the two with the widest blast radius. The number a future
reader trusts is the comment.

**There are five subscriptions to the status hub, and the inventory numbers two of them.** I found
`AgentHost.cs:170` (item 10), `ConsoleStage.cs:94` (item 8), `AgentStatusReporter.cs:99`,
`ConnectionAttempt.cs:225` and `BrowserStage.cs:213`. The three unnumbered ones do no I/O, which is
why they are not hazards and almost certainly why they were not listed — but the count is worth having
written down next to item 9, because item 9's fix has to be safe for all five.
