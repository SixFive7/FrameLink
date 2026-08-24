# Every source of non-determinism in the agent, classified

The operator's instruction, verbatim:

> *"Also, I like the DAG. Keep it. In fact, draw it out in a diagram as part of the documentation.
> Then have a DAG walker that is deterministic. So no timers and jitter or anything. If the hardware
> is the same the DAG walks the same. The only way you take another route through the DAG is if you
> take another turn because you have a dependency that is not okay yet where somebody else his
> dependency is."*

The picture is [the reconcile DAG](reconcile-dag.md), generated from the catalog and checked by the
suite. The bounds inventory is [reconcile ordering and every bound in the
agent](reconcile-ordering-and-timeouts.md), which established that the order is deterministic by
construction and enumerated 87 numeric constants. This document does the third thing: it names
**every** source of non-determinism in `src/FrameLink.Agent`, classifies each one, and says what it
would cost to remove.

Every claim below cites the file and line it was read from, in the tree as of 2026-08-24.

---

## 1. The classification, and the distinction the instruction conflates

The instruction contains two separate demands and treats them as one:

- *"no timers and jitter or anything"* — a demand about **timing**.
- *"if the hardware is the same the DAG walks the same"* — a demand about **route**.

These are not the same property, and the sources that threaten them barely overlap. Worse, the
instruction's own last sentence — *"the only way you take another route is if you take another turn
because you have a dependency that is not okay yet"* — describes a **third** class, and describes it
approvingly. A resource whose dependency is not `InSync` yet is a resource whose *observation* came
back different. So the operator has already granted the largest category of run-to-run variation the
agent has, and the useful answer is to name all three and say which one each source belongs to.

| Class | What varies | Does the walk take a different route? | Is it a defect? |
|---|---|---|---|
| **A — order-affecting** | Which resource is visited, and in what sequence | **Yes** | Yes. This is the class the instruction forbids |
| **B — timing-only** | *When* something happens. Identical route, identical decisions | No | No. This is what "timers and jitter" actually are |
| **C — observation-affecting** | What a resource *sees*, so a legitimately different conclusion is reached | Only in the sense the instruction explicitly allows | No — but it is what will look like a defect from outside |

**The headline: class A is empty.** Nothing in the agent can make two runs of the same build on the
same hardware visit the resources in a different sequence. §2 lists what holds that in place, §3
lists the two things that come closest and why neither reaches, §4 is the complete class B list, and
§5 is the complete class C list — the one the operator has not seen yet and deserves to.

---

## 2. Class A — order-affecting: nothing, and why

Five properties, all in code, and all of them have to hold simultaneously for the route to move.
None of them is currently violated.

1. **The catalog is a list literal, not a scan.** `DeviceCatalog.Build`
   (`Resources/DeviceCatalog.cs:217`) returns a collection expression spliced with four sub-builders,
   each a `foreach` over a static spec list. No reflection, no assembly scan, no DI container, no
   hash-container enumeration anywhere on the construction path.
2. **The sort is Kahn's algorithm with a declaration-index tie-break** (`Reconcile/ResourceGraph.cs:149-162`).
   When more than one resource is ready it picks the lowest declaration index, not the lowest arrival
   order. The three dictionaries the sort uses (`:114-116`) are indexed by key and never enumerated.
3. **The result is computed once and stored in a `List`** (`ResourceGraph.cs:88`, exposed at `:92`).
   Nothing re-sorts it.
4. **The walk is one sequential `foreach`** (`Reconcile/ReconcileLoop.cs:750`). No `Task.WhenAll`, no
   `Parallel`, no continuation reordering.
5. **The declaration order and the walk order are the same list.** Asserted, as of this change, by
   `ReconcileDagTests.The_walk_order_is_the_catalog_declaration_order_verbatim` — a test that reads
   `DeviceCatalog.Build`'s output and `ResourceGraph.Ordered` off one build and requires them equal
   name for name. It passed the first time it was run, so the property held before it was written;
   what is new is that it can no longer stop holding quietly.

### 2.1 Four hazards that would produce class A, checked and found absent

Worth recording as *checked* rather than left unmentioned, because each is the ordinary way a
codebase acquires a non-deterministic order without anyone deciding to.

- **Hash-container enumeration.** Every `Dictionary` and `HashSet` in the reconcile path and in
  `Resources/` is used for membership, counting or keyed lookup, and none is iterated into anything
  that reaches an order or an observed value. The pass's own status map
  (`ReconcileLoop.cs:736`) is only ever `TryGetValue`/`ContainsKey`/indexed-assign; the list that is
  published is built in walk order. .NET randomises string hashing per process, so a single `foreach`
  over one of these would have been a genuine boot-to-boot reorder.
- **Filesystem enumeration order.** `ISystemFiles.ListFiles`/`ListDirectories` wrap
  `Directory.EnumerateFiles`/`EnumerateDirectories`, whose order is filesystem-defined — and then
  sort ordinal before returning (`Hosting/ISystemFiles.cs:285`). The hazard is closed at the seam, so
  no caller can reintroduce it.
- **Parallelism in the walk.** There is none (`ReconcileLoop.cs:750`), by version2.md §2.2's decision.
- **Random tie-breaks.** There is exactly **one** call to a random source in the entire agent binary
  (`Link/Backoff.cs:89`), and it produces a duration, never a choice. The only other
  non-deterministic value construction is `Guid.NewGuid()` at `Reconcile/IBootIdentity.cs:59`, which
  is a fallback boot id — see §5.3.

---

## 3. The two near-misses, and why neither is class A

Both deserve naming because both are one small change away from becoming order-affecting.

### 3.1 Backoff jitter feeds a persisted eligibility time

This is the one the operator asked about, and it is closer to the line than "timing-only" suggests.

`ReconcileLoop.RecordFailureAsync` computes a retry delay from the jittered schedule and **persists
the resulting instant** in the journal: `var wait = _retry.Delay(attempt)` (`:1336`), then
`NextAttemptUtc = next` (`:1346`). The walk then skips any resource whose `NextAttemptUtc` is still
in the future (`:793`), and the loop sleeps until the *earliest* pending one (`:480-491`, fed by
`earliest = Sooner(...)` at `:795` and `:960`).

So if two resources were ever in backoff at the same time, jitter could make the later-started one
expire first, and the pass that woke would act on the wrong one. That would be class A.

**It is unreachable in the shipped configuration**, and the reason is version2.md §2.5 rung 2 plus its decision 68.
The walk acts on at most one resource per pass, and that is the first drifted, non-blocked,
non-backing-off resource in graph order. When it fails, the next pass finds it drifted and eligible
again, and it is still the first — so a failing resource consumes attempts 1, 2 and 3 consecutively,
then escalates, and an escalation stops the frame acting entirely. No second resource ever gets far
enough to be holding a backoff at the same time. `AttemptBudget = 3`
(`Reconcile/ReconcileOptions.cs:47`) is what makes the window too small to matter.

That is a real guarantee, but it is a guarantee held by a *different* number in a *different* file.
Raise the attempt budget, or add any path that lets the walk move past a failing resource, and this
becomes order-affecting with nothing to say so. It belongs in §6's recommendations for that reason
alone.

### 3.2 A hung child process stops the walk part-way

`IProcessRunner` has no timeout of any kind (`Hosting/IProcessRunner.cs:147-150`: both pipes drained,
then `WaitForExitAsync(cancellationToken)` with only the agent's shutdown token). A hung `apt`,
`amixer`, `systemctl` or `xvf_host` therefore hangs the pass at whatever position it was at, for
ever, with no countdown, no escalation and no change on the screen.

That is not a *reorder* — the prefix of the walk is identical every time — but it is a run that
visits a different **set** of resources than another run on the same hardware, and from outside it is
indistinguishable from the walker having wandered off. The ordering document already calls this the
one bound the system is arguably missing; this document seconds it, and adds that it is also the only
way to get a truncated walk that nothing reports.

---

## 4. Class B — timing-only: the complete list

Every one of these changes when something happens and nothing about what happens. Together they are
the whole of what "timers and jitter" means in this codebase.

| # | Source | Where | What varies | Recommendation |
|---|---|---|---|---|
| B1 | **Backoff jitter, ±20 %, cryptographic RNG** | `Link/Backoff.cs:46` (default), `:84` (applied), `:89` (source) | Reconnect delay and per-resource retry delay | **See §7 — this one needs a decision, not a verdict** |
| B2 | Link reconnect schedule, 1 s → 30 s cap | `Link/Backoff.cs:24,27`, used `Link/ControlLink.cs:81` | When a dropped link is retried. Never gives up | Keep. Removing it is an unthrottled reconnect loop |
| B3 | Reconcile retry schedule, 30 s → 30 min cap | `ReconcileOptions.cs:148,158`, built `:232` | When a failed resource is retried | Keep. version2.md §2.4's card-wear argument |
| B4 | `PassInterval` = 5 min | `ReconcileOptions.cs:208` | How often a converged frame re-observes | Keep. A cadence, not a deadline |
| B5 | `UnevaluableRecheck` = 30 s | `ReconcileOptions.cs:229` | How often a frame re-asks the server | Keep. Flat, unjittered already |
| B6 | Wall-clock windows: conflict hold 5 min, reboot floor 6 h, ask 30 min, rest 6 h, linger 15 min, fault rate 1 h | `ReconcileOptions.cs:111,145`; `Firmware/ArrayFlashApproval.cs:458,461,471`; `Supervise/SupervisionSettings.cs:65` | Which side of a boundary a given moment falls on | Keep. All are policy, all are argued in version2.md §2 |
| B7 | `supervision.dailyRestartTime` = 03:00 | `Supervise/SupervisionSettings.cs:41` | A once-a-day browser restart at a wall-clock time | Keep. Empty disables it |
| B8 | **Clock steps.** NTP correcting the Pi's clock after boot moves every window in B6 at once | `IAgentClock` throughout | A window can expire early or late by the size of the step | Name it; do not fix it. A frame with no RTC is a frame whose first NTP sync is a step, and the alternative is a monotonic clock that cannot express "03:00" |
| B9 | I/O latency: handshake 20 s, HTTP 5 min, mDNS 2 s window, VT switch 5 s, re-enumeration 90 s | see the [bounds inventory](reconcile-ordering-and-timeouts.md) §2.2–§2.7 | How long a machine waits on something it cannot hurry | Keep. Not policy at all |
| B10 | Thread-pool scheduling of the 14 concurrent loops | `AgentHost.cs:685-724` | Interleaving between the loops | Cannot be removed without removing the loops |
| B11 | Repaint cadences: console 120 ms, countdown 200 ms, confirm 100 ms, progress beat 1 s, touch poll 50 ms | see the bounds inventory §2.4, §2.7, §2.8 | Frame timing on a screen | Keep |

**Nothing in this table can change which resource the walk visits, or in what order.** B1 is the
only entry whose *value* reaches persisted state at all, and §3.1 is why that does not reach the
route either.

---

## 5. Class C — observation-affecting: what actually differs between two runs

This is the category the operator has not been told about, and the one that will produce the first
report of "it did something different on identical hardware". None of it is the walker. All of it is
the world the walker is looking at.

### 5.1 Fourteen concurrent loops — the count, resolved

The code says twelve. **It is fourteen**, and the history says exactly how it got there:

| Commit | Date | Loops in the list | Capacity | Comment said |
|---|---|---|---|---|
| `fe61154` | 2026-08-16 | 12 | 12 | Twelve — **correct** |
| `422d428` | 2026-08-23 | 13 | 12 | Twelve — stale, `arrayFirmware.RunAsync` added |
| `9908080` | 2026-08-24 | 14 | 13 | Twelve — stale, `arrayFlash.RunAsync` added, capacity bumped to 13 and not to 14 |

So the comment at `AgentHost.cs:674` was accurate when written and was not revisited by either of the
two commits that added a loop; the `new List<Task>(13)` at `:683` is the trace of one of those two
authors noticing the capacity and not the sentence. The list at `:685-724` holds fourteen tasks. This
is cosmetic in effect and worth fixing anyway, because the comment is the thing a future reader
trusts instead of counting.

**A fifteenth concurrent thing is not in that list at all**: the local origin's HTTP server, started
at `AgentHost.cs:332` and running an accept loop plus one task per connection
(`Local/LocalOrigin.cs:160,254`). A sixteenth, `ArrayFlashProgressPump`
(`Firmware/ArrayFlashProgress.cs:454`), exists only while a firmware write is running. Neither is
supervised by `WhenAllOrFirstFaultAsync`, so neither would fault the agent if it died — which is a
separate observation, and not one this document is chasing.

### 5.2 What each loop can change under the walk

None of them reorders anything, and none of them acts on a resource. What they do is change what an
`Observe` returns between one pass and the next.

| Loop | Can change what these resources observe |
|---|---|
| `link` | **Everything with a fleet-supplied desired value.** A settings push mid-walk changes the target the second half of the pass compares against |
| `updates` | `agent.version` (the served version it publishes), and it can replace the binary and restart the process outright |
| `supervisor` | `unit.chromium-kiosk.running-matches-content`, `kiosk.process.supervised`, and — through the interlock consulted mid-walk at `ReconcileLoop.cs:854` — turns real drift into `Progressing` for the length of a supervision window |
| `BrowserStageLoopAsync` | The same Chromium resources; it tears the GUI down on a 60 s check-in deadline and brings it back 2 min later |
| `kiosk` (the Immich Kiosk child) | `kiosk.process.supervised`, `kiosk.listen-address` — a relaunch in flight is a port that is briefly not bound |
| `arrayFlash` | The whole audio and firmware block. During a `dfu-util` write the array is **not on the USB bus at all** |
| `arrayFirmware` | `firmware.xvf3800.recognised`, `audio.xvf3800.gpo-x0d31-amp-enable` — it drives the same `xvf_host` the resources do, and the gate that keeps the two off the device at once decides by timing which one gets "busy" |
| `packages` | Every `pkg.*` resource, indirectly: it runs dpkg queries on a 6 h cadence and the dpkg lock is not shared |
| `button` | `gpio.button.line` observes the claim the agent itself is holding, and that claim is re-attempted every 30 s after a refusal |
| `screen` | Which VT is foreground, which `display.*` and stage observations can see |
| `stage` (console), `touch`, `reporter` | Nothing in the catalog. Listed so the fourteen are accounted for |
| local origin (§5.1) | `app.http.local-origin`, and every `app.config.*` cross-check, which reads what the page last reported over that socket |

### 5.3 The world outside the agent

- **Fleet values and `ServerAnswer`.** A frame that has been adopted between two runs has a
  different desired state and a different set of unblocked resources. This is the operator's own
  sentence — "a dependency that is not okay yet" — and it is by far the largest legitimate cause of
  two runs differing.
- **Persisted state.** Attempt counts, the give-up ledger, the reboot ledger and `FirstInSyncUtc`
  all live in `/var/lib/fl-agent` and survive updates by design (version2.md §2.1). Two runs on identical
  *hardware* are not two runs on identical *state*, and the second one can escalate on the first
  failure where the first one had three attempts.
- **Other owners of the same setting.** WirePlumber applies its own stored device volume once the
  login session starts, which is why `audio.mixer.pcm0-playback-volume` had to be given an edge on
  `audio.wireplumber.playback-volume`. systemd, unattended-upgrades and a person at a keyboard are
  the same shape of hazard.
- **Hardware presence.** Whether the array is on the bus, whether the panel is up, whether a camera
  enumerated. Different answers, same route.
- **`/proc` availability.** `KernelBootIdentity` falls back to `Guid.NewGuid()` when
  `/proc/sys/kernel/random/boot_id` cannot be read (`Reconcile/IBootIdentity.cs:59`), which makes
  every process start look like a fresh boot. On a real frame this never fires; off a frame it is
  the reason a verify-after-reboot can appear to succeed without a reboot.
- **mDNS response order** is network arrival order (`Discovery/MdnsEndpointSource.cs:175-183`).
  It selects which Fleet Manager address is tried first, and nothing about the walk.

---

## 6. Recommendations

Nothing here has been implemented. The one change made alongside this document is the identity test
in §2 item 5 above, which asserts existing behaviour and alters none.

1. **Fix the loop-count comment** at `AgentHost.cs:674` and the list capacity at `:683`. One line
   each. It is the cheapest item on this page and the only one that is unambiguously a defect.
2. **Give `IProcessRunner.RunAsync` a deadline** (§3.2). This is the single largest gap in the
   agent's determinism story, because it is the only way to get a walk that stops half-way with
   nothing on the screen to say so. A per-call timeout with the elapsed time in the failure message
   turns "the frame is frozen" into an ordinary `Degraded` on the ladder.
3. **Put a comment on `AttemptBudget` saying that the retry sequence's single-resource property is
   what keeps jitter out of the route** (§3.1). The guarantee is real and is currently undocumented
   and cross-file; the next person to raise the budget will not know they are also changing the
   determinism argument.
4. **Do not remove the DAG.** Already argued in the ordering document §5.4 and unchanged by anything
   here: the edges are what make `Blocked(dependency)` a derived fact rather than a hand-maintained
   claim, and decision 76 exists because a frame once lied about exactly that.
5. **Say class C out loud in the product.** The operator will meet it as "two identical frames did
   different things". `Blocked(dependency)` already carries the reason on the screen and in the
   Fleet Manager, which is most of the answer — the gap is that nothing distinguishes "this pass
   observed something different" from "this pass ran differently", and only the first is possible.

---

## 7. The jitter decision, with both costs stated

`Backoff` is constructed in exactly two places, both with the default 20 % jitter and neither passing
a fraction source: `ControlLink.cs:81` (link reconnect) and `ReconcileOptions.cs:232`
(per-resource retry).

### 7.1 The case for keeping it

The comment at `Backoff.cs:17-18` states the reason and it is the correct one: *"a household power
cut restarts every frame at the same instant, and a fleet that reconnects in lockstep turns the
operator's own recovery into a thundering herd."* This is not hypothetical shape — it is the
canonical failure mode of synchronised retry, and the Fleet Manager is a **single self-hosted
container** (version2.md §3.1), not an elastic service. Six frames behind one router, one power cut, one Fleet
Manager that takes thirty seconds longer than the frames to come up: without jitter all six retry at
1 s, 2 s, 4 s … 30 s in exact lockstep, and the instant the server answers it receives six
simultaneous handshakes, six full status reports and six telemetry flushes. Jitter spreads that over
a 24–30 s band at no cost to anybody.

The fleet this operator runs is six frames, where the herd is survivable. The fleet a self-hoster
runs is not bounded by that, and the code is shipped to self-hosters.

### 7.2 The case for removing it

A single frame becomes *repeatable in wall-clock terms*, not merely in route. A provisioning run of
the 82-resource catalog takes on the order of eighty reboots; today two recordings of that run differ
in their timings even when every decision was identical, so a timing difference between two runs
carries no information. Without jitter it would: any difference in elapsed time between two runs on
one frame would mean something actually happened differently. That is a real diagnostic asset for
exactly the artifact this project produces most of — a captured run.

It also removes §3.1's near-miss outright, rather than leaving it standing behind a guarantee that
lives in another file.

### 7.3 What already exists, and what does not

**Repeatability in test is already solved with one code path.** `Backoff` takes a `Func<double>?
fraction` seam (`Backoff.cs:43-47,89`) and the suite uses it: nine call sites construct
`new Backoff(..., jitter: 0)` and two pin the extremes with `fraction: () => 1.0` and `() => 0.0`
(`AgentConnectionLeakTests.cs:348-349`). Production leaves the parameter at its default. There is no
second path and nothing to diverge.

**What has no seam at all is the reconcile retry.** `ReconcileOptions.RetrySchedule()` is
`new(InitialBackoff, BackoffCap)` (`:232`) — the jitter argument is not exposed on the options record,
so `InitialBackoff` and `BackoffCap` can be set and the jitter cannot. A frame's own retry timing
cannot be made repeatable by any configuration that exists today.

### 7.4 Five directions

1. **Keep it as-is.** Zero work, zero risk. Cost: the reconcile retry stays unrepeatable on a real
   frame with no way to change that, and §3.1's near-miss stays live.
2. **Drop jitter from the reconcile retry; keep it on the link reconnect.** One argument, applied
   where it holds. The herd defence exists because many frames converge on *one server* — and the
   reconcile retry never touches the server at all. It is a per-frame, per-resource delay before a
   local Act, and a fleet retrying `cpu.governor.performance` in lockstep converges on nothing. This
   makes the loop's own timing fully deterministic, removes §3.1 entirely, adds no flag and creates
   no second path. Cost: one argument at `ReconcileOptions.cs:232`, and the loss of any accidental
   herd protection on retry-triggered reboots, which nothing shares a resource with.
3. **Derive the fraction from the device identity instead of the RNG.** `fraction = () =>
   stable_hash(deviceId, resourceName, attempt) / 2^32`. One code path, always jittered, and the
   jitter is a pure function of things that do not change: one frame replays identically for ever,
   and a fleet is still spread because no two frames share a device id. This is the answer to
   "repeatability in test *and* jitter in production without two code paths". Cost: a hash function
   to write and test, and jitter that is predictable to anyone who knows a device id — irrelevant
   here, since the thing being protected is a self-hosted server from its own frames.
4. **Make jitter a fleet setting**, `link.reconnectJitter`, default 0.2, forced to 0 by
   `--development` the way the countdown already is (`ReconcileOptions.cs:287`). Cost: this is the
   two-paths option the brief warns about — the shipped behaviour and the tested behaviour diverge
   by configuration, and the configuration lives on the server the jitter exists to protect.
5. **Remove jitter entirely**, both call sites. Cost: the herd, in full, on the one server a
   self-hoster owns.

### 7.5 Recommendation

**Direction 2, and direction 3 if and when the fleet outgrows one household.**

Direction 2 is the honest reading of the operator's instruction. What they want deterministic is the
walk, and the reconcile retry delay is the only jitter that is part of the walk's own machinery. It
is also the only one that reaches persisted state and therefore the only one that was ever close to
the route. Removing it there costs nothing that can be named, because the thundering-herd argument
does not apply to a delay before a local `systemctl` call.

Keeping it on `ControlLink` is not a compromise; it is the argument being applied where the argument
is true. Every frame in a fleet reconnects to the same single container, and that is the definition
of the case jitter exists for.

Direction 3 is strictly better than direction 2 on the link half and costs a hash function. It is not
recommended *yet* only because six frames do not need it and an unwritten hash function is a place
for a bug. If the fleet grows, or if the link reconnect ever needs to be replayable, it is the right
shape and it is a small change from where direction 2 leaves things.
