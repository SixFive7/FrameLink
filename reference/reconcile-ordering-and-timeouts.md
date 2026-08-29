# Reconcile ordering and every bound in the agent

Read from the code, not from `version2.md`. Every claim below cites the file and line it came
from. Where the code and the specification disagree, the disagreement is stated in §8 rather
than smoothed over.

Scope: `src/FrameLink.Agent` (the whole binary) plus `app/` (the product page, which ships
*inside* that binary per §2.1 and is therefore part of the same artifact). The Fleet Manager
(`src/FrameLink.Control`) is out of scope except where a value is resolved server-side.

**Snapshot caveat.** The working tree was being edited by another process while this was written —
`Firmware/ArrayFirmwareFlash.cs` grew from 945 to 999 lines mid-analysis and
`Firmware/ArrayFlashProgress.cs` appeared. Every line number cited below was re-verified against
the tree after those edits landed, and the new file's one bound is included (#71). The *count* is
therefore a snapshot of the array-flash area specifically; everything in §2.1–§2.6 and §2.8–§2.9
sits in files untouched since 16 August.

---

## 1. Is the resource graph's execution order deterministic?

**Yes. Completely, and by construction rather than by accident.** Two runs of the same build on
the same frame visit the 90 resources in exactly the same order, every pass, every boot.

Four facts hold it in place, and all four are in the code:

1. **The catalog is a hand-written list literal, not a scan.** `DeviceCatalog.Build`
   (`src/FrameLink.Agent/Resources/DeviceCatalog.cs:256-427`) returns a collection expression —
   90 constructor calls in a fixed textual order, spliced with four sub-builders
   (`PackageCatalog.Build`, `KioskStack`, `AudioCatalog.Build`, `AppConfigCatalog.Build`). There
   is no reflection, no assembly scan, no dependency-injection container and no
   `Dictionary`/`HashSet` enumeration anywhere in the construction path. The sub-builders are
   themselves `foreach` loops over static spec lists
   (`PackageResources.cs:400-411`, `AppResources.cs:566-592`). The one `HashSet` in the area
   (`AudioArrayResources.cs:473`) is a dedup helper over an explicitly ordinal-sorted list
   (`AudioArrayResources.cs:476`) and is not on the catalog path.

2. **The topological sort is Kahn's algorithm with declaration index as the tie-break.**
   `ResourceGraph.Sort` (`Reconcile/ResourceGraph.cs:112-195`) records every resource's position
   at `ResourceGraph.cs:118-121`, and when more than one resource is ready it picks the *lowest
   declaration index*, not the lowest arrival order
   (`ResourceGraph.cs:155-162`). The comment there says why: appending newly-ready resources
   would also be a valid topological order and a much worse one. The class remark
   (`ResourceGraph.cs:41-46`) states the intent outright — "a sort that could return two
   different valid orders on two boots would undo half of that".

3. **The result is computed once, at construction, and stored in a `List`.**
   `_ordered` is built in the constructor (`ResourceGraph.cs:88`) and exposed as
   `IReadOnlyList` (`ResourceGraph.cs:92`). Nothing re-sorts it afterwards.

4. **The walk is a plain sequential `foreach` over that list.**
   `ReconcileLoop.WalkAsync` (`Reconcile/ReconcileLoop.cs:750`) iterates
   `_services.Graph.Ordered` with no parallelism, no `Task.WhenAll`, no continuation
   reordering. Every collection the loop reads back from the journal is a `List` too —
   `ReconcileJournalState.Ledger` and `.Reboots` are both `IReadOnlyList`
   (`Reconcile/ReconcileJournal.cs:144,164`), so even the orphan sweep
   (`ReconcileLoop.cs:549`) is order-stable.

The tests pin all of this: `Independent_resources_keep_the_order_the_catalog_declared_them_in`
(`tests/FrameLink.Tests/AgentResourceGraphTests.cs:28-41`) and
`The_shipped_catalog_orders_the_display_first_and_needs_nothing_to_do_it`
(`AgentResourceGraphTests.cs:120-137`).

### What *is* non-deterministic, stated honestly

Order is fixed. Three other things are not, and it is worth separating them because the
operator's worry is about order and none of these touches it.

- **Retry *timing* is jittered.** `ReconcileOptions.RetrySchedule()`
  (`ReconcileOptions.cs:232`) builds a `Backoff` with the default 20 % jitter
  (`Link/Backoff.cs:46`) drawn from `RandomNumberGenerator`
  (`Backoff.cs:88-89`). So the delay before a resource's second attempt varies run to run by up
  to 20 %. Which resource is tried, and in what order, does not.

- **Which resource acts first in a given pass depends on what is drifted**, which depends on the
  machine. The walk acts on at most one resource per pass (`ReconcileLoop.cs:896-913`), and that
  is the first drifted, non-blocked, non-backing-off resource *in graph order*. Same drift set →
  same choice, always.

- **Fourteen other loops run concurrently with the reconcile loop**
  (`AgentHost.cs:683-725`): the link, the update service, the supervisor, both stages, the
  screen handover, the package inventory, the array firmware reporter, the array flash, the
  button, the touch reader and the Immich Kiosk child. They can change what a resource *observes*
  between passes — a supervision window is explicitly consulted mid-walk
  (`ReconcileLoop.cs:854-874`) — but none of them reorders the walk or acts on a resource. The
  reconcile loop itself is single-threaded, exactly as §2.2 says.

**Verdict: the order is deterministic and switching to a hand-written linear list would not make
it more so.** It is already, in effect, a hand-written linear list that a validator checks.

---

## 2. Every bound in the agent — the complete inventory

**71 behaviour-gating bounds in the agent binary**, plus **11 more in the embedded app**, plus
**5 numeric limits that shape output or child configuration rather than timing** — **87 numeric
constants in total**. Every one is listed below. Nothing is sampled.

Kind column: **M** = a machine cannot wait forever (I/O, process, network); **P** = a person is
not there; **G** = the system decided to give up; **C** = a cadence (how often we *look*, not how
long we *wait*); **H** = a health trigger threshold.

### 2.1 Reconcile loop and reboot discipline — 16

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 1 | `AttemptBudget` = 3 | `Reconcile/ReconcileOptions.cs:47` | 4th failure → `Degraded`/`Escalated`, frame stops acting | No (see §8.2) | G |
| 2 | `EscalationLimit` = 2 | `ReconcileOptions.cs:64` | **Nothing. Dead code** — see §8.1 | No | G |
| 3 | `ConflictThreshold` = 3 | `ReconcileOptions.cs:92` | 3 consecutive reversions → immediate give-up, skipping the budget (`ReconcileLoop.cs:882-894`) | No | G |
| 4 | `ConflictHold` = 5 min | `ReconcileOptions.cs:111` | A value held this long forgives prior reversions | No | C |
| 5 | `RebootFloorCount` = 120 | `ReconcileOptions.cs:136` | Reboot refused (`IRebootBoundary.cs:245-256`) | No | G |
| 6 | `RebootFloorWindow` = 6 h | `ReconcileOptions.cs:145` | Window #5 is counted over | No | G |
| 7 | `InitialBackoff` = 30 s | `ReconcileOptions.cs:148` | Wait before attempt 2 | No | C |
| 8 | `BackoffCap` = 30 min | `ReconcileOptions.cs:158` | Ceiling on that wait | No | C |
| 9 | Repair countdown = 60 s | `ReconcileOptions.cs:165`, `CountdownDuration.Default` `:269` | Pause before a verifying reboot on a frame that has been green | **Yes** — `repair.countdownSeconds` (`:295`), wired at `AgentHost.cs:409-411` | P |
| 10 | Provisioning pace = 0 | `ReconcileOptions.cs:188`, `ProvisioningPace.Default` `:374` | Pause before a *provisioning* reboot | **Yes** — `provisioning.paceSeconds` (`:384`), wired at `AgentHost.cs:416-418` | P |
| 11 | `PassInterval` = 5 min | `ReconcileOptions.cs:208` | Sleep between passes when nothing is pending (`ReconcileLoop.cs:479-495`) | No | C |
| 12 | `UnevaluableRecheck` = 30 s | `ReconcileOptions.cs:229` | Re-ask a resource whose observation could not be made | No | C |
| 13 | Countdown accepted range 0–3600 s | `ReconcileOptions.cs:334-338` | Out-of-range or unparseable → falls back to **60 s** | n/a | validation |
| 14 | Pace accepted range 0–3600 s | `ReconcileOptions.cs:412-417` | Out-of-range or unparseable → falls back to **0** | n/a | validation |
| 15 | Countdown repaint tick = 200 ms | `Reconcile/RebootCountdown.cs:33` | Bar repaint while counting | No | C |
| 16 | `BootPartitionGuard.BootLimit` = 2 | `Resources/BootPartitionGuard.cs:134` | 2 unconfirmed boots → the `/boot/firmware` backup is restored | No | G |

### 2.2 Control link and discovery — 7

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 17 | `Backoff.DefaultInitial` = 1 s | `Link/Backoff.cs:24` | First reconnect wait | No | C |
| 18 | `Backoff.DefaultCap` = 30 s | `Link/Backoff.cs:27` | Reconnect ceiling — **never gives up** (`Backoff.cs:9-14`) | No | C |
| 19 | Backoff jitter = 0.2 | `Link/Backoff.cs:46` | Up to 20 % shaved off, cryptographic RNG (`:88-89`) | Constructor only | C |
| 20 | `ControlLink.HandshakeTimeout` = 20 s | `Link/ControlLink.cs:119` | Handshake abandoned, connection retried | No | M |
| 21 | `ConnectionAttempt.HandshakeTimeout` = 20 s | `Link/ConnectionAttempt.cs:58`, applied `:186` | Same knob, second declaration site (passed down at `ControlLink.cs:235`) | No | M |
| 22 | WebSocket polite-close = 2 s | `Link/WebSocketControlTransport.cs:81` | Close abandoned, socket disposed anyway | No | M |
| 23 | mDNS query window = 2 s | `Discovery/MdnsEndpointSource.cs:53`, applied `:172` | Discovery returns what it has | Constructor only | M |

The agent answers server pings (`ConnectionAttempt.cs:360-362,458-466`) but keeps **no
missed-pong deadline of its own** — §3.5's ping/pong deadline is the Fleet Manager's side.

### 2.3 Self-update and HTTP — 2

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 24 | `UpdateService.DefaultInterval` = 1 h | `Update/UpdateService.cs:65`, used `:113,270` | Out-of-band version convergence | `Interval` init-only; not fleet-wired | C |
| 25 | `HttpClient.Timeout` = 5 min | `AgentHost.cs:246` | **One shared client** for the agent binary, Immich Kiosk release, `xvf_host` and firmware images (`AgentHost.cs:248,441,442,658`) | No | M |

### 2.4 Screen, stages and the local origin — 10

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 26 | Browser check-in deadline = 60 s | `Stage/BrowserStage.cs:227-228`, applied `:408` | §2.7's fallback rule: GUI torn down, console narration resumes (`:435-439`) | **Yes** — `stage.browserCheckInDeadline` (`:156`) | M |
| 27 | Browser retry delay = 2 min | `Stage/BrowserStage.cs:231-232`, applied `:439` | Console narrates, then the GUI is tried again — **no cap on retries** (`Teardowns` just counts, `:221,435`) | **Yes** — `stage.browserRetryDelay` (`:159`) | C |
| 28 | Browser-stage loop tick = 5 s | `AgentHost.cs:839` | Next stage evaluation | No | C |
| 29 | `ConsoleStage.TickInterval` = 120 ms | `Stage/ConsoleStage.cs:130`, applied `:204` | `/dev/tty8` repaint | init-only | C |
| 30 | `ScreenHandover.PollInterval` = 2 s | `Stage/ScreenHandover.cs:123`, applied `:272` | Re-ask whose the panel should be | init-only | C |
| 31 | `ScreenHandover.SwitchDeadline` = 5 s | `Stage/ScreenHandover.cs:126` | A requested VT switch is called failed | init-only | M |
| 32 | `ScreenHandover.Settle` = 3 s | `Stage/ScreenHandover.cs:135` | Anti-flap: one attempt per settle period | init-only | C |
| 33 | `ScreenHandover.CoverAfter` = 5 s | `Stage/ScreenHandover.cs:146` | How long the compositor must be gone before the console covers it | init-only | C |
| 34 | `ScreenHandover.ConfirmInterval` = 100 ms | `Stage/ScreenHandover.cs:412`, applied `:390` | Poll while confirming a switch | No | C |
| 35 | Local-origin WS keepalive = 20 s | `Local/LocalOrigin.cs:368` | Agent↔page socket keepalive | No | M |

### 2.5 Supervision (§2.10) — 12, all fleet settings

Every value here resolves through `Supervise/SupervisionSettings.cs` and is a real fleet
setting under §3.4. This is the **only** block where the specification's "the constants are fleet
settings" is fully true in code.

| # | Value | Where | What fires | Kind |
|---|---|---|---|---|
| 36 | `supervision.browserTreeRssCeilingKb` = 1 843 200 | `SupervisionSettings.cs:32,80` | Browser restart | H |
| 37 | `supervision.memAvailableFloorKb` = 358 400 | `SupervisionSettings.cs:35,83` | Browser restart | H |
| 38 | `supervision.memoryCheckInterval` = 5 min | `SupervisionSettings.cs:38,86`, applied `Supervisor.cs:371` | Memory sample | C |
| 39 | `supervision.dailyRestartTime` = 03:00 | `SupervisionSettings.cs:41,89`, applied `Supervisor.cs:416` | Scheduled browser restart; empty disables it | C |
| 40 | `supervision.kioskSilenceTimeout` = 90 s | `SupervisionSettings.cs:44,92` | Browser restart on local-channel silence | H |
| 41 | `supervision.kioskCheckInterval` = 15 s | `SupervisionSettings.cs:47,95` | **The whole supervisor loop's tick** (`Supervisor.cs:355`) | C |
| 42 | `supervision.kioskRestartCooldown` = 5 min | `SupervisionSettings.cs:50,98` | Minimum spacing between liveness restarts | C |
| 43 | `supervision.pageRefreshCooldown` = 5 min | `SupervisionSettings.cs:56,113` | Minimum spacing between page reloads | C |
| 44 | `supervision.recoveryDeadline` = 2 min | `SupervisionSettings.cs:59,116` | Window expires → the transient **becomes ordinary drift** (`SupervisionInterlock.cs:202-216`, consumed `ReconcileLoop.cs:854`) | M |
| 45 | `supervision.faultRateThreshold` = 3 | `SupervisionSettings.cs:62,119` | Raises a **diagnostic** supervision fault; never inhibits | H |
| 46 | `supervision.faultRateWindow` = 1 h | `SupervisionSettings.cs:65,122` | Window #45 is counted over | H |
| — | `supervision.cameraRestartOnCallEnd` = true | `SupervisionSettings.cs:53,101` | A flag, not a bound; listed for completeness | — |

### 2.6 The Immich Kiosk child — 5

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 47 | `StopGrace` = 5 s | `Kiosk/KioskProcess.cs:254`, applied `:467-468` | Child is killed | No | M |
| 48 | `RecoveryDeadline` = 2 min | `Kiosk/KioskProcess.cs:686` | Relaunch stops being a transient | Delegate; wired to the fleet setting on a frame | M |
| 49 | Relaunch loop interval = 5 s | `Kiosk/KioskProcess.cs:689`, applied `:543` | Next relaunch check | init-only | C |
| 50 | `ChildOutputBudget.DefaultLinesPerWindow` = 60 | `Supervise/ChildOutputBudget.cs:72` | Further child output is dropped | Deliberately **not** a fleet setting (`KioskProcess.cs:692-698`) | G |
| 51 | `ChildOutputBudget.DefaultWindow` = 10 min | `Supervise/ChildOutputBudget.cs:80` | Window #50 resets | No | G |

### 2.7 Array firmware flash and the consent screen — 10

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 52 | `DefaultInterval` = 1 min | `Firmware/ArrayFirmwareFlash.cs:319`, applied `:608-610` | Next full tick (reads the authorisation) | No | C |
| 53 | `PromptInterval` = 5 s | `Firmware/ArrayFirmwareFlash.cs:327`, applied `:610` | `StandDown()` while one of its screens is up | No | C |
| 54 | `ReEnumerationTimeout` = 90 s | `Firmware/ArrayFirmwareFlash.cs:335`, applied `:903-913` | Post-write verification gives up waiting for the bus | No | M |
| 55 | `ReEnumerationPoll` = 2 s | `Firmware/ArrayFirmwareFlash.cs:338`, applied `:915` | Re-read the USB bus | No | C |
| 56 | `ApprovalHold` = 5 s | `Firmware/ArrayFlashApproval.cs:452` | Finger-down duration that means *yes* | No | P |
| 57 | `DismissHold` = 3 s | `Firmware/ArrayFlashApproval.cs:455` | Finger-down duration that means *put this away* | No | P |
| 58 | `AskWindow` = 30 min | `Firmware/ArrayFlashApproval.cs:458`, applied `:560-571` | See §3 | No | P |
| 59 | `RestWindow` = 6 h | `Firmware/ArrayFlashApproval.cs:461`, applied `:548-551,563,696,729,785` | See §3 | No | P |
| 60 | `CompletionLinger` = 15 min | `Firmware/ArrayFlashApproval.cs:471`, applied `:768` | See §3 | No | P |
| 71 | `ArrayFlashProgressPump.DefaultBeat` = 1 s | `Firmware/ArrayFlashProgress.cs:354` | Repaints the elapsed-seconds counter so a still progress bar reads as a wait rather than a hang | Constructor only (`:400`) | C |

### 2.8 Local input — 6

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 61 | `TouchRetry.HoldDuration` = 3 s | `Local/TouchRetry.cs:101` | §2.7 item 9's retry hold on the console stage | No | P |
| 62 | `TouchRetry.PollInterval` = 50 ms | `Local/TouchRetry.cs:109`, applied `:277` | evdev read cadence | No | C |
| 63 | `TouchRetry.RediscoverDelay` = 30 s | `Local/TouchRetry.cs:118`, applied `:277` | Look for a touchscreen again | No | C |
| 64 | `ButtonWatch.Debounce` = 50 ms | `Local/ButtonWatch.cs:92` | libgpiod debounce | No | M |
| 65 | `ButtonWatch.RetryDelay` = 30 s | `Local/ButtonWatch.cs:100`, applied `:221` | Re-attempt a lost or refused GPIO claim | No | C |
| 66 | `GpioMonLines.ClaimGrace` = 2 s | `Hosting/IGpioLines.cs:86`, applied `:188` | `gpiomon` still up after this → the claim counts as held | No | M |

### 2.9 Telemetry and resource-level observation — 5

| # | Value | Where | What fires | Configurable | Kind |
|---|---|---|---|---|---|
| 67 | `PackageInventoryReporter.DefaultInterval` = 6 h | `Telemetry/PackageInventoryReporter.cs:69`, applied `:187` | Inventory push | `Interval` property `:110` | C |
| 68 | `ArrayFirmwareReporter.DefaultInterval` = 6 h | `Telemetry/ArrayFirmwareReporter.cs:74`, applied `:240` | Firmware-version push | No | C |
| 69 | Mixer probe freshness = 10 s | `Resources/AudioMixerResources.cs:342` | One `amixer` probe is reused across a pass rather than re-spawned per control | No | C |
| 70 | `KioskSessionResources.SettleSeconds` = 30 | `Resources/KioskSessionResources.cs:268`, applied `:386` | Below this, a unit active without a session yet is *settling* rather than wrong | No | M |

### 2.10 Numeric limits that are not timing bounds — 5

Listed for completeness so the enumeration is genuinely exhaustive; none of these gates a wait.

`AptPackages.cs:503` output `Limit = 240` lines · `KioskBrowserResources.cs:474`
`DiagnosticArgumentLimit = 4` · `AudioMixerResources.cs:339` `NamesShown = 6` ·
`KioskProcess.cs:84` `OfflineAssetCount = 200` (Immich Kiosk's own config) ·
`AppResources.cs:455` slideshow `DefaultInterval = "30"` (a product setting pushed to the page).

### 2.11 The embedded app — 11

The app is inside the binary (§2.1), so these ship and update with the agent.

`app/frame-app.js:24` `IFRAME_LOAD_TIMEOUT_MS` = 8 000 · `:31` `PROBE_TIMEOUT_MS` = 4 000 ·
`:32` `PROBE_BACKOFF_MIN_MS` = 3 000 · `:33` `PROBE_BACKOFF_MAX_MS` = 30 000 ·
`:34` `HEALTHY_RECHECK_MS` = 60 000 · `app/frame-stage.js:36` `HEARTBEAT_MS` = 15 000 ·
`:37` `RECONNECT_MIN_MS` = 500 · `:38` `RECONNECT_MAX_MS` = 5 000 ·
`app/livekit.js:118` auth-failure sleep 600 000 ms · `:118` reconnect backoff cap 60 000 ms ·
`:155` post-disconnect sleep 2 000 ms.

### 2.12 What is *not* bounded, and one of them is a hazard

- **`IProcessRunner.RunAsync` has no timeout at all** (`Hosting/IProcessRunner.cs:55-101`). It
  drains both pipes and awaits `WaitForExitAsync` with only the agent's shutdown token
  (`:87-90`). Neither `ReconcileLoop.ObserveAsync` (`ReconcileLoop.cs:1597-1618`) nor the Act
  path (`:1028`) imposes a deadline either. **A hung `apt`, `amixer`, `systemctl` or `xvf_host`
  hangs the entire reconcile pass indefinitely** — no countdown, no escalation, no screen change.
  The code is aware of the *deadlock* version of this hazard (`IProcessRunner.cs:83-86`: "a hung
  pass is worse than a failed one, because nothing on the screen ever changes to say so") and
  guards the pipe-buffer case, but not the slow-child case. This is the one place where "we wait
  indefinitely" is a defect rather than a policy, and it is the one bound the system is
  arguably *missing*.

- **There is no SSH anywhere in the agent** (no client, no server, no timeouts). The only hits
  for "ssh" in `src/FrameLink.Agent` are prose in comments. §3.6's remote shell is the Fleet
  Manager's, not the agent's.

---

## 3. The three consent-screen durations, traced

These belong to `ArrayFlashApproval`, which drives the **microphone-array firmware write**. It is
**not a resource and not in the DAG** — the flash runs in its own loop
(`ArrayFirmwareFlash.RunAsync`, `Firmware/ArrayFirmwareFlash.cs:583-625`), started beside the
reconcile loop at `AgentHost.cs:709`. The only firmware *resource* in the graph is
`firmware.xvf3800.image` (`Resources/XvfFirmwareImageResource.cs:64`), which converges the pinned
image files on the SD card and never touches the array.

### 3.1 The 30-minute ask window — what actually happens at the boundary

`ArrayFlashApproval.Ask` (`ArrayFlashApproval.cs:531-576`) is called once per full tick, i.e.
about once a minute (`ArrayFirmwareFlash.cs:476,529` inside `TickAsync`; the tick cadence is
`DefaultInterval` = 1 min, `:319`). On the first call it stamps `_askingSince = now`
(`:553-555`) and publishes the question (`:574`). On each later call it compares
`now - since >= AskWindow` (`:560`).

When 30 minutes are up, in this exact order (`:560-571`):

1. `_askingSince = null` — the ask is retired.
2. `_restingUntil = now + RestWindow` — a 6-hour quiet period starts.
3. A `Warn` is written naming the elapsed window and stating that **"The authorisation is still
   armed and the frame will ask again later."**
4. `Publish(null)` — the prompt is removed from the status hub, so **the panel goes back to the
   product**. Both stages drop the overlay because both render `status.ArrayFlash`.
5. `Ask` returns `false`.

**What does *not* happen:**

- **The authorisation is not cancelled.** The fleet setting under `AuthorisationKey` is untouched;
  `Consume()` is never called on this path (it is called only on a real flash or on
  `AlreadyAtTarget`, `ArrayFirmwareFlash.cs:449-452`).
- **No approval is granted.** `_approvedFor` is left null (`ArrayFlashApproval.cs:585` is the only
  writer, reached only from `Approve`).
- **No state machine advances, nothing escalates, no attempt is spent, no reboot is taken.** The
  flash loop simply returns `AwaitingLocalApproval` from
  `ArrayFirmwareFlash.ApprovedAsync` (`:531-546`) with the third of its three message variants —
  the one that says the screen has gone back to the product and will ask again later.
- **The frame does not idle.** It is doing everything else it always does: the reconcile loop
  keeps sweeping every 5 minutes, supervision keeps ticking every 15 seconds, the product plays.
  The flash loop keeps ticking once a minute and takes the cheap `StandDown()` path.

**Where the refusal goes:** `RefuseAsync` (`ArrayFirmwareFlash.cs:958-973`) dedupes on
(refusal kind, message), so the Fleet Manager receives **one** event when the wording changes
from "waiting for somebody" to "nobody agreed, the screen has gone back to the product", and
then nothing until the state changes again.

### 3.2 The 6-hour rest — and the direct answer to "why not show it for six hours?"

During the rest window every `Ask` returns `false` at the very first check
(`ArrayFlashApproval.cs:548-551`) without touching the screen. `Interrupted` (`:696-699`) and
`Unrecognised` (`:729-732`) respect the same window, so **no** firmware screen of any kind goes
up during it.

When the 6 hours elapse, the next `Ask` finds `_askingSince` null again and restarts the whole
cycle at step 1: the question goes back on the panel for another 30 minutes. **This repeats
indefinitely for as long as the authorisation stands.** There is no counter, no give-up, no
terminal state. The frame will still be asking next week.

So the answer to *"why not show it within six hours?"* is: **it is shown again, and again, and
again — every 6 h 30 m, for ever.** The 6 hours is not a deadline on the operator's intent; it
is the off-phase of a duty cycle. The class remark states this outright
(`ArrayFlashApproval.cs:440-447`): *"An operator's intent is never lost — the authorisation
stays armed, the refusal reaches the Fleet Manager, and the frame asks again later — but no
frame is left showing a question nobody is going to answer for the rest of the week."*

The thing being bounded is **not the decision. It is the household's photographs.** The prompt
is a full-screen overlay; while it is up, the product is covered. A question shown until
answered on a frame whose viewer is on holiday is a frame that shows a question until they get
back.

### 3.3 The 15-minute completion linger

This one governs the *outcome* screens, not the question. `Finished` (`:672-684`) stamps
`_shownAt = now`. `Withdraw` — called on the ordinary end of every tick that writes nothing
(`ArrayFirmwareFlash.cs:385,402,408,417,430,436,466`) — takes the `Asking` screen away
immediately (`ArrayFlashApproval.cs:753-762`), but for any other phase it refuses to remove the
screen until `now - _shownAt >= CompletionLinger` (`:764-772`). After that it calls `Dismiss`
(`:774`), which clears everything and **opens a fresh 6-hour rest** (`:778-789`) — which is why
the recovery and unrecognised-hardware screens *repeat* rather than persist
(`:713-720`).

A person can end the linger early with a 3-second hold or a button press: `Answer`
(`:603-631`) routes any non-`Asking` phase to `Dismiss`. The remark at `:463-470` gives the
reason for bounding it at all — a frame flashed under the operator's unattended bypass has
nobody to press anything, and the outcome is in the event trail, the journal and the Fleet
Manager regardless of who reads the screen.

### 3.4 Summary of the three

| Duration | Bounds | On expiry | Authorisation | Product |
|---|---|---|---|---|
| 30 min ask | How long one question covers the product | Screen cleared, rest starts, one event emitted | **Still armed** | Returns |
| 6 h rest | How long before the same question returns | Question goes back up for 30 min | **Still armed** | Covered again |
| 15 min linger | How long a *result* screen stays with nobody to dismiss it | Screen cleared, fresh 6 h rest starts | Already spent | Returns |

None of the three gives up on anything. All three are **"a person is not there"** bounds.

---

## 4. Is "everything green before we proceed" actually implemented?

**Partly — and the part that is not is deliberate, not an oversight.** The premise as stated
("all checks green before we proceed") is not what the loop does, and was never what §2.3
describes. What §2.3 describes is Observe → Compare → Act **only on drift** → Verify, per
resource.

### 4.1 What the loop actually enforces

Three separate rules, all in `WalkAsync`:

1. **Every *dependency* must be `InSync` this pass before a dependent is touched.**
   `Blocker` (`ReconcileLoop.cs:1527-1538`) returns the first `dependsOn` entry that is not
   `InSync` in this pass's status map; the dependent is recorded `Blocked(dependency)` and
   skipped without being observed or acted on (`:757-771`). This is the real content of
   "green before we proceed", and it is scoped to the dependency edges, not to the whole catalog.

2. **One Act per pass, for the whole frame.** The `acted` flag (`:743,896-913,913`) means a pass
   changes at most one resource, then reboots and verifies it (`:1064,1087-1242`). Everything
   after it in the walk is observed and reported as `Progressing`, and acted on next pass. So the
   frame does not wait for everything to be green; it converges one resource at a time, in graph
   order, one reboot each (§2.4).

3. **One escalation stops all acting, frame-wide, until a human intervenes.** `HasStopped`
   (`:233-234`) reads the ledger for *any* resource that has given up; the walk starts with its
   one change already spent (`:742-743`) and `stopped` is latched on any give-up mid-walk
   (`:783-784,890-891,940`). Observation continues so the screen can tell the truth
   (`:695-733`, decision 76). **This is the strongest form of the operator's premise that the
   code contains, and it is real.**

### 4.2 Every place a wait is unbounded (the premise holds)

| Condition | Waits for | Bound |
|---|---|---|
| `Escalated` / `Degraded` | A person pressing retry (`ResetBudget`, `ReconcileLoop.cs:246`) | **None. Forever.** The loop keeps ticking at `PassInterval` doing nothing but looking (`:452-458`) |
| `Blocked(dependency)` | Its dependency to converge | **None.** Re-evaluated every pass |
| `Blocked(the Fleet Manager)` — `Unevaluable` | The server to answer | **None.** Rechecked every 30 s; the ledger is not touched in either direction (`:813-835`) — no attempt is spent and none is forgiven |
| Link down | The Fleet Manager | **None.** Capped 30 s backoff, retry for ever (`Backoff.cs:9-14`) |
| Browser will not render | The page | **None on the number of attempts.** 60 s deadline → teardown → 2 min → try again, unbounded (`BrowserStage.cs:435-439`) |
| Firmware consent | A person at the frame | **None.** 30 min on / 6 h off, for ever (§3) |
| A hung child process | The process | **None — and this one is unintended** (§2.12) |

### 4.3 Every place a wait *is* bounded, and what the bound is for

| Bound | Value | Why it exists |
|---|---|---|
| Attempt budget | 3 | **Give-up.** Card wear per attempt; a fault that has not cleared by the second will not clear (`ReconcileOptions.cs:19-45`) |
| Conflict threshold | 3 consecutive reversions | **Give-up.** Something is actively fighting the desired state; retrying is a livelock (`:70-90`) |
| Reboot floor | 120 reboots / 6 h | **Give-up on rebooting**, not on the resource. A refused reboot spends an attempt and reaches a person on the ordinary ladder (`ReconcileOptions.cs:117-134`, `IRebootBoundary.cs:245-256`) |
| Boot-partition trial | 2 unconfirmed boots | **Give-up + rollback.** Restores the backup rather than bricking the frame (`BootPartitionGuard.cs:125-134`) |
| Child output budget | 60 lines / 10 min | **Give-up on logging**, to protect the 64 MB journal (`ChildOutputBudget.cs:62-80`) |
| Supervision recovery deadline | 2 min | **Hand-over, not give-up.** The transient becomes ordinary drift and the reconciler takes it with full §2.7 narration (`SupervisionInterlock.cs:202-216`) |
| Handshake / HTTP / VT switch / re-enumeration | 20 s / 5 min / 5 s / 90 s | **A machine cannot wait forever on I/O.** All retried |
| Countdown / pace / holds / ask / rest / linger | 60 s / 0 / 3–5 s / 30 min / 6 h / 15 min | **A person is or is not there.** None ends anything |

### 4.4 The honest verdict on the premise

Of the 71 bounds, **8 are the system deciding to give up** (#1, #2, #3, #5, #6, #16, #50, #51),
and of those, one is dead code, two protect the SD card from a livelock, one prevents a brick, and
two only drop log lines. **The real give-up surface is two numbers: the attempt budget of 3 and
the conflict threshold of 3.** Both are exactly §2.5 rung 2 and §2.6's conflict drift, both were
argued for in the specification, both are recoverable by one press that grants a fresh budget
(`ReconcileLoop.cs:236-245`), and `Escalated` remains terminal with nothing below it.

Everything else the operator might read as "giving up" is one of three other things: **33
cadences** (how often we look), **15 I/O bounds** (a machine cannot block for ever on a socket),
and **8 human-attention bounds** (the consent screen owns five of those eight — the three
durations plus its two hold gestures). **None of those contradicts "wait indefinitely for an
answer" — they schedule the waiting rather than end it.**

---

## 5. A fixed linear order instead of a graph — an honest assessment

### 5.1 What the graph buys today

1. **Blocking, and the honest reporting that depends on it.** `Blocker`
   (`ReconcileLoop.cs:1527-1538`) is the *only* source of `blockedBy` anywhere in the agent, and
   §2.6's decision 76 requires that "a row may only name a dependency the graph actually
   contains". The measured bug that rule exists to prevent — a frame reporting 0 of 79 in sync,
   with 77 rows claiming to wait on a resource most had never depended on
   (`ReconcileLoop.cs:709-718`) — was a fabricated dependency claim. Delete the edges and the
   `blockedBy` field has nothing truthful to hold.
2. **Not attempting doomed work.** A dependent whose dependency is not `InSync` is never observed
   and never acted on, so it spends no attempt and takes no reboot
   (`AgentResourceGraphTests.cs:83`). Without edges, a resource that cannot possibly succeed
   would burn 3 attempts and 3 reboots to discover it — and with the escalation rule, would stop
   the frame.
3. **Build-time validation.** Duplicate ids, dangling dependencies and cycles all throw at
   construction with an actionable message (`ResourceGraph.cs:64-88,182-192`). A cycle names the
   whole stuck set rather than "found a back edge" (`:107-111`).
4. **Edit safety.** The catalog is 83 constructor calls across five files. The sort is what makes
   `cpu.governor` land after its unit and its enablement *whatever order somebody edits the list
   into* — the test asserts precisely that (`AgentResourceGraphTests.cs:227-233`).

The edge set is not decorative: **42 declaration sites carry a non-empty `DependsOn`**,
several of them shared across a whole block — every `app.config.*` defaults to
`[agent.adoption]` (`AppResources.cs:176`), five `kiosk.config.*` name `kiosk.binary`
(`KioskResources.cs:783-836`), and the entire user-unit layer hangs off the autologin drop-in
(`KioskSessionResources.cs:636,747`, asserted at `AgentResourceGraphTests.cs:243-250`).

### 5.2 What the graph does *not* buy

- **No parallelism.** `WalkAsync` is a plain `foreach` (`ReconcileLoop.cs:750`). §2.2 rules
  concurrency out on purpose.
- **No dynamic reordering.** The sort runs once, at construction (`ResourceGraph.cs:88`).
- **Barely any reordering at all.** The catalog is already written dependencies-first — its own
  remark says "the order below is the order a bare frame converges in"
  (`DeviceCatalog.cs:178-181`). When declaration order is already a valid topological order, Kahn
  with a declaration-index tie-break returns it **verbatim**. So on the shipped catalog the sort
  is, in practice, an identity function with a validator attached.

### 5.3 A strict linear pass — what it gains and loses

**Gains:** one less concept; `Ordered` becomes `Build()`; roughly 85 lines of `ResourceGraph`
deleted; and the order becomes readable by eye from one file with no algorithm in between.

**Loses:**
- `Blocked(dependency)` as a *derived truth*. It would become a hand-maintained annotation, and
  decision 76's rule ("a row may only name a dependency the graph actually contains") would have
  nothing to be checked against.
- The build-time cycle/dangling-reference check. Ordering mistakes would become runtime
  behaviour — a resource silently attempted before its prerequisite, spending three attempts and
  stopping the frame under decision 68.
- The refusal to attempt doomed work. Every dependent of a failed resource would burn its full
  budget.
- Robustness against catalog edits. Today, inserting a resource in the wrong place is corrected
  by the sort; then, it would be a bug that only a reboot on a real frame reveals.

### 5.4 Recommendation

**Keep the graph.** Not because the complexity is large — it is 196 lines, one class, no state —
but because what it earns is not ordering. **It earns the right to say `Blocked(dependency)` and
have that be a fact rather than a claim**, and that is the exact property decision 76 was written
to protect after a frame lied about it in production.

The operator's instinct is nonetheless right about the *symptom*: the shipped catalog does read
as a linear list, and the sort mostly returns it unchanged. If the goal is to be able to *read*
the convergence order, the fix is not to delete the graph — it is to assert the identity. **A
single test that `graph.Ordered.Select(r => r.Name)` equals `DeviceCatalog.Build(...).Select(r =>
r.Name)` would make "the catalog file is the execution order, verbatim" a checked property**, and
would fail loudly the day somebody adds an edge that reorders something. That gives the
readability of a linear list and keeps the validation. It does not exist today.

---

## 6. The three kinds of bound, applied

The operator's question conflates three things, and the distinction decides the answer.

**A machine cannot wait forever (15 bounds).** #20–23, 25, 26, 31, 35, 44, 47, 48, 54, 64, 66, 70.
A socket read, an HTTP fetch, a VT switch, a child's exit. Every one of these retries; none ends
anything. These are not policy at all — they are what "the operating system will not tell us"
looks like in code.

**A person is not there (8 bounds).** #9, 10, 56–61. The repair countdown, the provisioning pace,
the two hold gestures, and the three consent durations the operator asked about. **Every one of
these bounds how long a *screen* is held, and not one of them bounds a decision.** The
authorisation survives; the retry stays available; the escalation stays escalated. What expires is
the frame's willingness to keep a household's photographs covered while it waits.

**The system decided to give up (8 bounds).** #1, 2, 3, 5, 6, 16, 50, 51. **This is the only
category that contradicts the premise**, and it is two live numbers plus four safety valves plus
one dead field:
- `AttemptBudget = 3` and `ConflictThreshold = 3` — the real ones. Both argued in §2.5 and §2.6,
  both recoverable by one press, both leading to `Escalated`, which is terminal *and waits for a
  person for ever*. **So even the give-up ends in an unbounded wait.**
- `RebootFloorCount`/`Window`, `BootLimit` — refuse a *reboot*, not a resource. The change still
  reaches a person on the ordinary ladder.
- `ChildOutputBudget` ×2 — drop log lines.
- `EscalationLimit` — dead.

**Plus a fourth kind the question does not have a slot for, and it is the largest: cadence (33
bounds) and health triggers (5).** `PassInterval = 5 min` is not "we give up after 5 minutes"; it
is "we look every 5 minutes". `Backoff.DefaultCap = 30 s` is not "we stop reconnecting after 30
seconds"; it is "we never wait longer than 30 seconds before trying again". **Nearly half of the
71 numbers are of this kind, and reading them as deadlines is the most likely source of the
impression that the system is full of give-up timers.**

---

## 7. Where the specification and the code agree

For completeness, the values §2 and §3.4 state and the code implements identically: the 3-attempt
budget; the 120-reboot / 6-hour floor; the 60-second countdown default with per-device → fleet →
built-in resolution and `--development` forcing zero; the zero-default provisioning pace with
opposite typo fallbacks (60 s for the countdown, 0 for the pace — `ReconcileOptions.cs:330-338`
vs `:409-417`); all twelve `supervision.*` settings and their defaults; the hourly update tick;
and `Escalated` as terminal with no rung below it.

---

## 8. Where the specification and the code disagree

**8.1 `EscalationLimit` is dead, and its own documentation says otherwise.**
`ReconcileOptions.cs:54-57` states: *"It is left in place, and still read by the loop, because
removing it belongs with the loop's own halt paths in one deliberate change."* It is **not read by
the loop**. A repository-wide search finds exactly three occurrences: the declaration
(`ReconcileOptions.cs:64`) and two test fixtures setting it
(`AgentMemoryTests.cs:37`, `AgentServerSilenceTests.cs:35`). No production code consumes it.

**8.2 "The budgets are values, so they can come from the Fleet Manager" — in practice they
cannot.** `ReconcileOptions.cs:9-13` frames the whole record as fleet-supplyable. In
`AgentHost.cs:403-419` **only two of the twelve options are wired to fleet settings**:
`CountdownSource` and `ProvisioningPaceSource`. `AttemptBudget`, `ConflictThreshold`,
`ConflictHold`, `RebootFloorCount`, `RebootFloorWindow`, `InitialBackoff`, `BackoffCap`,
`PassInterval` and `UnevaluableRecheck` all take their compiled-in defaults on a real frame and
can only be changed by a release. Contrast §2.10's supervision block, where all twelve values are
genuinely fleet settings. The doc says *can*, and that is true of the type; the practical answer
to "is this configurable" is **no** for nine of the twelve.

**8.3 "Twelve loops now" is fourteen.** `AgentHost.cs:674` says twelve, the list is constructed
with capacity 13 (`:683`), and it contains fourteen tasks (`:685-724`). Cosmetic — one list
growth — but the comment is the thing a future reader trusts.

**8.4 §3.4's "every setting is fleet-managed" does not hold for the consent screen.** All five of
`ApprovalHold`, `DismissHold`, `AskWindow`, `RestWindow` and `CompletionLinger` are
`static ... { get; } =` on `ArrayFlashApproval` (`:452-471`) with no setting key, no
`FleetValues` lookup and no override path. Same for the flash loop's four intervals
(`ArrayFirmwareFlash.cs:319-338`). **The three durations the operator is asking about cannot be
changed without a release.** That may well be the right call for a safety interlock — but it is
not what §3.4 says, and if the answer to "30 minutes is too short for my household" is currently
"ship a new binary", that is worth knowing before the conversation about the number itself.

---

## Correction, 2026-08-24

**The count above was 81 when this was written and is 82.** `firmware.xvf3800.recognised` landed
in commit `1df3e07` on the same day, a few hours after this analysis was taken, and it is the
first resource of a new kind: a **gate**, which has no Act at all. The three counts corrected
here are the resource total; every file-and-line citation in this document was verified against
the tree as it stood that morning and lines have moved since, which is why each citation also
names its identifier.

Two findings in this document were sharpened by later work and should be read with the
amendments rather than as they stand:

- The **missing process timeout** is worse than stated here. `IProcessRunner` is called by seven
  of the fourteen loops, not only by the reconcile pass. A hung `systemctl` freezes the browser
  stage, which is the mechanism whose whole purpose is to stop the screen going blank; a hung
  `ps` freezes all five supervised behaviours at once, because they share one tick.
- The **fourteen loops are fifteen concurrent things**. The local origin's accept loop and its
  per-connection tasks are not in the host's supervised list at all, so nothing notices if they
  stop. See `reference/outside-the-dag.md`, which inventories all 41 items outside the graph.


---

## Second correction, 2026-08-24 — three of these bounds have moved

**#19, backoff jitter = 0.2, is gone.** Both call sites now build `Backoff` with no jitter and no
fraction seam, on the operator's decision (`reference/reconcile-determinism.md` §7.4 direction 5).
The inventory is therefore **86 numeric constants**, and the agent binary contains no call to a
random source of any kind.

**#1, `AttemptBudget` = 3, now counts one more thing.** A supervised loop that ends while the agent
is still running is recorded in the same ledger as `agent.loop.<name>`, against the same budget,
and forgiven by #4's `ConflictHold` when the process that ended it had already run longer than
that. Three consecutive short-lived runs stop the frame exactly as three failed repairs do, and the
same retry clears both. It is not a new bound; it is an existing one with a second kind of
subject.

**§2.12's unbounded list loses one entry and §8's disagreements lose none.** The accept loop of the
local origin is now the fifteenth supervised loop rather than the one thing in no list at all, and
`AgentHost` treats a loop that *returns* as a failure and not only one that throws — so the
fourteen that were watched for a fault are now watched for both ways a loop can die. The one
legitimate early return, `ScreenHandover` on a machine with no virtual terminals, waits for
cancellation instead of returning, so the rule has no exceptions rather than one.

`IProcessRunner` still has no deadline. That is untouched and remains the largest gap.

---

## Third correction, 2026-08-24 — a dead loop reboots the frame, and what bounds that

**#1, `AttemptBudget` = 3, now bounds a reboot as well as counting one.** A supervised loop that
ends no longer stands the agent down for `Restart=always` to bring back; attempts one and two
**reboot the machine**, through the same `IRebootBoundary` chain a resource's reboot crosses
(`AgentLoopFailures.RestartOrStopAsync`, called from `AgentHost.cs`). So a firmware write in flight
refuses it (decision 91) and decision 79's floor counts it, with no new vocabulary at the boundary.
The third attempt is unchanged: no automatic reboot, the stopped screen, no timer.

**The inventory stays at 86 numeric constants.** The new backstop's size is `AttemptBudget` itself
rather than a number of its own, deliberately: two mechanisms that both decide how many times a
frame may restart itself must not be able to disagree, and the one that disagreed downwards would
be the one that mattered.

### Four layers under a restarting frame, and which of them is redundant

| Layer | Where | Bounds | Survives a reboot | Survives an unreadable journal | Survives an unwritable card |
| --- | --- | --- | :---: | :---: | :---: |
| `StartLimitBurst=10` / `StartLimitIntervalSec=5min` | the unit, `[Unit]` | process starts | **no** | n/a | n/a |
| §2.5 attempt ladder | `ReconcileLoop`, `AgentLoopFailures.Record` | failures per resource, 3 | yes | yes, since `ReconcileJournal.Unreadable` | **no** |
| decision 79 reboot floor | `RebootFloor` | every reboot, 120 in 6 h | yes | yes, refuses while `Unreadable` | **no** |
| restart allowance | `RebootAllowance` | a frame restarting itself over its own loops, 3 | yes | yes, reads nothing parseable | **yes**, refuses |

**None of the four is redundant, and the reason is that each covers a case the one above it
structurally cannot.**

1. The **start limit** is the only layer that needs no durable state at all, and it is the only one
   that bounds a process which dies *before* the agent can record anything — a crash in start-up,
   an unhandled exception outside the supervised set, a binary that will not run. Nothing inside the
   agent can bound that, because nothing inside the agent runs. It is also the layer this change
   costs: its counter lives in the running `systemd`, so a reboot resets it, and moving the
   loop-death path from a process restart to a machine restart takes it out of that path entirely.
2. The **ladder** is the diagnosis. It is per-resource, it distinguishes a fault from a fault that
   has already been repaired, and it is what puts a name and a delta on the screen. It cannot bound
   a livelock made of successes (decision 79's own argument) and it cannot bound anything at all
   when its counters will not persist.
3. The **floor** is device-level and dumb, which is exactly what the ladder is not, and it is the
   only layer that bounds the *provisioning* reboots — 81 of them on a bare frame — because it is
   the only one sized for them.
4. The **allowance** is the only layer that does not read the journal. Two ways a ledger comes back
   empty are left after `Unreadable`, and neither is a fault its reader can see: **genuinely
   absent**, which is correctly read as a first boot and is what a wiped state directory, a
   re-flashed card and a script tidying `/var/lib` all look like; and **readable but unwritable**,
   where every counter stays honestly at its old value for ever and no read ever throws. The
   allowance refuses in both, because absence of its own file means *spent* rather than *fresh*, and
   because a spend it cannot read back afterwards is a spend that did not happen.

**What the allowance is.** `/var/lib/fl-agent/reboot-allowance`, one `#` per remaining automatic
restart, counted by scanning for that byte and never parsed. Truncated, fewer restarts; garbled,
fewer, because a byte that is no longer the token stops counting; absent, none; unreadable, none.
Every corruption of it can only cost the frame restarts, never grant them — so it needs no schema,
no version field and no upgrade seam, and it is correct whether or not the write that produced it
was atomic. It is refilled by a process that ran for `ConflictHold` before its loop ended, which is
the identical test the ladder forgives on, or by a person pressing retry.

**A refused restart ends the ladder where it stands.** Whichever layer refuses, nothing is
scheduled and the process is parked, so the ledger is written as an ordinary give-up
(`AgentLoopFailures.Refused`) and every surface downstream behaves as it does on the third attempt.
The delta carries both halves: which loop stopped, and why the frame did not restart over it.

**The one reset in the agent now clears all three durable layers.** `RebootFloor.Forget()` had no
caller at all, so decision 79's promise that "a retry grants a fresh window" was not implemented and
a frame that had reached the floor could not be recovered by the button built for it. The retry path
in `AgentHost` now calls it, refills the allowance, and then clears the budgets.

### Still unbounded

**A loop that dies just after `ConflictHold`, for ever.** Both the ladder and the allowance forgive
a process that ran longer than the hold, so a loop that reliably survives five minutes and then dies
restarts the frame about ten times an hour indefinitely. The floor does not catch it either: 60
reboots in six hours is under its 120. This is a property of decision 78's forgiveness rule rather
than of anything added here, and closing it means either a longer hold for this one subject or a
second, slower window on the allowance — an operator decision, not an implementation detail.

**A loop that never survives the hold gets no automatic restart at all.** The cost of "absence means
exhausted": a frame whose loop has never once run for five minutes has no allowance to spend, so it
holds the stopped screen on the first death instead of power-cycling itself three times first. That
is the right way round — a loop that dies in two seconds every time is not fixed by a reboot — but
it is a real difference from the three the operator's model describes, and seeding the file at
install time is the alternative if that matters.

---

## Third correction, 2026-08-24 — §2.12's hazard is closed

**`IProcessRunner.RunAsync` has a deadline now, and it is the last of §2.12's list to get one.**
Every one of the roughly one hundred call sites names a bound, either at the call itself or at the
wrapper that knows what kind of command it is running — `SystemdControl`, `LoginUserSession`,
`XvfHost` and `AptPackages` between them cover about sixty. There is no infinite value and no
default: `Timeout.InfiniteTimeSpan` is −1 milliseconds and lands on the same guard as zero, so the
old behaviour has no spelling left.

**The bounds, and where each number came from.** `ProcessDeadline.Local` = 30 s for anything that
answers from the kernel or a local file (`id`, `pgrep`, `ps`, `swapon --show`, `findmnt`, `amixer`,
`dpkg-query`, `apt-config`, `ss`, `iw`, `chown`, `usermod`, `gpioinfo`, `alsactl`) — three orders of
magnitude of headroom, and short because `ps` and `pgrep` are on loops that tick every few seconds.
`Resolver` = 1 min for `getent hosts`, the one command that can legitimately block off-frame.
`Array` = 90 s for `xvf_host`, sized for a USB re-enumeration rather than for the sub-second
transaction. `Service` = 2 min for everything that reaches systemd or D-Bus, **derived** from
Debian's own `DefaultTimeoutStartSec` of 90 s: a job systemd has not finished by then is one it is
about to fail and answer for, so a shorter bound would fire on jobs that were about to succeed.
`Firmware` = 5 min for `dfu-util`, two and a half times the documented 30 s–2 min write.
`Storage` = 10 min for `swapoff`, which reads every swapped page back into RAM before returning.
`PackageChange` = 60 min for `apt-get`, deliberately late: killing it mid-transaction leaves `dpkg`
half-configured and needing `dpkg --configure -a` by hand, which is worse than the hang and not
something the agent can repair from.

**The kill is the whole tree, and the reachability claim is narrower than "it works".** `apt` is
`env … apt-get …` and every user-scope command is `runuser … -- env … systemctl --user …`, so the
process the agent holds a handle to is a wrapper and the thing that hangs is below it. Measured on
the workstation: replacing `Kill(entireProcessTree: true)` with `Kill()` leaves the grandchild alive
and the test names its pid. A grandchild whose parent has *already exited* is reachable on Windows,
where the parent id stays in the child's record, and **not** on Linux, where the kernel reparents an
orphan to init. That is why the return does not depend on the kill succeeding: the wait is a race
against a timer rather than a token, so end-of-file never has to arrive, and the result reports
whether the pipes actually closed instead of assuming they did.

**Two failure paths, one model.** On the resource path a timeout is data — `ProcessResult.TimedOut`,
a non-zero exit, and a sentence in `StandardError` naming the command and the deadline — so it
travels every path a failed command already travels and fails the resource, spending one of #1's
three attempts. In the six loops beside the pass it is converted to a `ProcessTimeoutException` that
leaves the loop, because those loops have no ledger of their own and would otherwise retry a wedged
`systemctl` for ever; `AgentHost` then records it as `agent.loop.<name>` against the same budget.
The array flash is deliberately excluded from that conversion: a `dfu-util` timeout has to run the
interrupted-flash latch and the outcome report, and throwing past those would lose the state that
says a frame's microphone unit may be half-written.

**§5's D3 loses its exception too.** `XvfHost.Conversation`'s unbounded wait was justified in-code
by this defect — *"a hung tool wedges the caller today, with or without this gate"* — and that
premise is gone, so the wait is now bounded at three times `ProcessDeadline.Array`. Three things in
the process hold that gate, so at most two can be ahead of any waiter and each may spend its whole
deadline; the bound can therefore only be reached by a future caller holding it across more than one
call, which is the case it now defends against.

**What is not verified.** None of this ran on a frame. The workstation has no password for one in
this session, so the Linux half of the process-tree behaviour — including the orphan case that
Windows handles and Linux cannot — is reasoned from the kernel's reparenting rule and the .NET
implementation, not measured.

---

## Correction, 2026-08-29

**The count above was 82 when the previous correction was written and is 83.**
`apt.daily-timers.enabled-and-active` landed as the answer to
[`outside-the-dag-review.md`](outside-the-dag-review.md) item 39 — the frame reconciled apt's
configuration while nothing asserted that the two systemd timers consuming it were still enabled
and running, so a frame whose `apt-daily.timer` had been disabled or masked reported a fully green
apt block and silently stopped receiving security updates. It declares no dependency, so it
changes no edge and no order: it is declared at position 32, beside the two apt configuration
resources it completes, and the walk order is still the declaration order verbatim.

Only the three resource counts are corrected here. Every file-and-line citation in this document
still stands as the previous correction left it — verified against the tree of 2026-08-24 and
stale by line number since, which is why each one also names its identifier.
