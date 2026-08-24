# Software Build Guide 08 — WebRTC Call-Load Validation

The Pi 5 with 2 GB RAM has no hardware video decoder, so Chromium software-decodes every incoming WebRTC stream and a video call is by a wide margin the heaviest thing a frame ever does. This guide loads a finished frame with a real call between the household's own units, measures RAM, CPU and temperature at the start, logs them every thirty seconds for four hours or more, and ends in a pass or fail against five criteria. Nothing here needs a LiveKit URL, an API key, an API secret or a token: the Fleet Manager owns the call server and issues every frame its credentials, so the call is started the way a person starts one, by pressing the button, and the only thing you supply is time.

---

<a id="1-confirm-the-frame-is-idle-and-green"></a>
<img src="https://img.shields.io/badge/STEP_01-Confirm_the_frame_is_idle_and_green-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Confirm the frame is idle and green"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A measurement of a frame that is already unwell measures the illness, not the call. Before loading it you need to know what this frame looks like when it is doing nothing wrong.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Check that the frame is running normally, then write down its memory, temperature and swap while it is only showing photos.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Four readings, and one of them is a precondition rather than a measurement.

1. `systemctl is-active fl-agent` answers `active` when the FrameLink Agent is running. The agent is what holds this frame's calling configuration: the Fleet Manager mints the call token and supplies the call server's address, the agent records both and serves them to the app on the frame's own local origin. That is the whole reason this guide no longer installs a command-line tool, exports three environment variables or asks you to paste a secret. There is nothing for a person to carry, and nothing a person is able to carry, because the API secret lives inside the Fleet Manager and is never shown. A frame whose agent is not active has no credentials to call with and there is nothing to load.
2. `free -m` prints memory in megabytes. The `used` column on the `Mem:` line is the number this guide tracks; the ceiling that matters is 1.5 GB, which leaves headroom below the point at which the whole system begins to stall.
3. `vcgencmd measure_temp` reads the SoC temperature. Idle on a bare heatsink is a long way below the throttle point; the interesting question is where it settles under load, which is step 3's job.
4. `grep SwapTotal /proc/meminfo` proves ZRAM (compressed swap in RAM) is configured. A frame with no swap at all has a very different failure curve under memory pressure than one with it, so a zero here changes how the rest of this guide reads.

Run these while the frame is showing its slideshow and nobody is in a call with it.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
systemctl is-active fl-agent
free -m
vcgencmd measure_temp
grep SwapTotal /proc/meminfo
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The first line reads exactly `active`. Anything else (`inactive`, `failed`, `activating`) means this frame is not ready to be measured and the Fleet Manager's own screen will say why. In the `free -m` output, note the `used` figure on the `Mem:` line; this is your idle baseline and every later reading is compared against it. The temperature line is of the form `temp=NN.N'C`. `SwapTotal` should be a non-zero number of kilobytes.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have a written-down starting point for this frame at rest, and you have confirmed it is healthy enough for the measurement to mean anything.

<a id="2-start-a-call-and-snapshot-the-load"></a>
<img src="https://img.shields.io/badge/STEP_02-Start_a_call_and_snapshot_the_load-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Start a call and snapshot the load"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Before committing to four hours you need to know the frame is not already over capacity in the first minute of a call.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Press the call button so every frame in the household joins, look at the screen to confirm everyone is there, then take the same readings again.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The call is started by pressing the physical button on one frame, which is the product's own path and therefore the thing worth testing. Every other frame in the household answers automatically and joins the same room, so the number of participants is the number of units you have powered on. There is no step here that publishes artificial streams into the room, because there is no longer a credential with which to publish them. A call between real frames exercises the codec, resolution and simulcast settings the product actually ships, which simulated streams never did.

`top -bn1 | head -20` is the addition to step 1's readings. It prints one snapshot of the busiest processes; during a call the list is dominated by Chromium renderer processes, one of which is decoding every incoming stream in software. The `%CPU` column is per core, so on this four-core machine the ceiling is 400 and a figure such as 285 means roughly 71% of the whole machine.

Take these readings a minute or two into the call, once the layout has settled and every tile is showing moving video.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
free -m
top -bn1 | head -20
vcgencmd measure_temp
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

On the frame's screen: one video tile per other unit in the call, all of them moving rather than frozen. In the output: memory `used` well under 1.5 GB, the summed `%CPU` of the Chromium processes below about 320 of the 400 available, and a temperature still climbing rather than settled; four hours is what settles it. A tile that is black or frozen while the others move is a stream that did not arrive, and that is a call fault to resolve before spending four hours measuring around it.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame is carrying a real call from the household's own units, and you have confirmed it has room to keep doing so for long enough to be worth measuring.

<a id="3-run-the-four-hour-soak"></a>
<img src="https://img.shields.io/badge/STEP_03-Run_the_four--hour_soak-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Run the four-hour soak"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Chromium leaks memory slowly over hours. A snapshot taken two minutes into a call proves nothing about a call that is still running at bedtime.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Leave the call running and record the same numbers every thirty seconds into a file, then walk away for at least four hours.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`nohup` detaches the logging loop from this SSH session, so you can close the terminal, close the laptop and reconnect later without the loop stopping. The loop writes a timestamp, the `Mem:` line and the temperature every thirty seconds to `~/soak-test/resources.log`, which is roughly 480 entries over four hours.

The `pgrep -f` guard in front of it is what makes this step safe to run twice. Without it, a second run starts a second logger writing into the same file and the readings interleave into nonsense; with it, the second run finds the first still going and does nothing. `pgrep` matches against the full command line, which is why it can find this specific loop rather than merely "some bash".

Nothing here touches the frame's configuration. The whole soak is a read of `/proc` and a write into your own home directory, so there is nothing to put back afterwards beyond deleting the log, which matters more than it sounds, because a frame's settings are reconciled continuously and a hand-edit made here to force a test condition would be corrected underneath you, and would stop the product while it was corrected.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/soak-test
pgrep -f 'soak-test/resources.log' > /dev/null || nohup bash -c 'echo "Soak test started: $(date)" > ~/soak-test/resources.log; while true; do echo "=== $(date +%H:%M:%S) ==="; free -m | grep Mem:; vcgencmd measure_temp; echo "---"; sleep 30; done >> ~/soak-test/resources.log 2>&1' &
sleep 5
tail -5 ~/soak-test/resources.log
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `tail` at the end should already show a timestamp header, a `Mem:` line and a temperature, which is the loop proving it is writing rather than merely having been started. Come back in four or more hours; do not stop the test early unless the frame is visibly unresponsive or the call has dropped, both of which are results in themselves and should be written down rather than retried. If you reconnect during the soak and want to check on it, `tail -5 ~/soak-test/resources.log` is the whole check.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The frame is under continuous call load and recording its own vital signs unattended. There is nothing more to do until the soak has run.

<a id="4-evaluate-and-clean-up"></a>
<img src="https://img.shields.io/badge/STEP_04-Evaluate_and_clean_up-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Evaluate and clean up"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The soak has run and produced a few hundred lines of numbers. Those are not yet an answer, and the frame is still carrying a test it no longer needs.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Pull the worst numbers out of the log, check them against five pass criteria, then end the call and delete the log.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The five criteria are:

1. Peak memory stays below 1.5 GB for the whole run.
2. CPU stays below roughly 320 of the 400 available on average.
3. No out-of-memory kills: `dmesg` mentions neither `oom` nor `killed`.
4. No Chromium crash: the call is still up and the tiles are still moving when you come back.
5. The video stayed smooth, with no frozen tiles.

The `awk` pipeline is what turns the log into criterion 1. `grep "Mem:"` selects only the memory lines, `awk '{print $3}'` takes the third field, which is the `used` column, `sort -n` orders them numerically and `tail -1` keeps the largest, so one line prints the highest memory figure reached across the entire soak. `wc -l` is a sanity check on the log's length: a four-hour run at one entry every thirty seconds produces roughly 1 440 lines, and a much shorter log means the loop died and the run is not four hours of evidence whatever the numbers say.

The failure branch is a hardware conversation, not a software one. If peak memory or CPU is over the line, the options are a Pi 5 with 4 GB instead of 2 GB, fewer simultaneous participants, or a lower capture resolution. That decision belongs before the enclosure is built, not after.

The clean-up is one line, because nothing was changed. `pkill` stops the logging loop, and `rm -rf` removes the directory it wrote into; the frame's own configuration was never touched, so there is nothing to restore. End the call itself by pressing the button again on the frame you started it from.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
wc -l ~/soak-test/resources.log
grep "Mem:" ~/soak-test/resources.log | awk '{print $3}' | sort -n | tail -1
tail -20 ~/soak-test/resources.log
dmesg | grep -i -e oom -e killed
pkill -f 'soak-test/resources.log'
rm -rf ~/soak-test
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`wc -l` should report at least 1 440 lines for a four-hour run. The `awk` line prints a single number in megabytes: this is the peak, and it is the one figure to record. `dmesg` should print nothing at all; any line mentioning `Out of memory` or `Killed process` is criterion 3 failing outright. The last twenty lines should look like the first twenty, not like a number that has been climbing all afternoon; a steady figure is a frame that can be left alone, a rising one is a leak that will reach the ceiling eventually even if it did not today. `pkill` prints nothing on success, and it exits non-zero if the loop had already stopped, which is not an error.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have a peak memory figure, a temperature plateau and a pass or fail against five criteria for this hardware under a real call, and the frame is back to exactly the state it was in before you started, with nothing to undo.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

A real call between the household's own frames ran continuously for four hours or more on a 2 GB Pi 5 without peak memory exceeding 1.5 GB, without an out-of-memory kill, and without Chromium crashing or a video tile freezing. Note the participant count beside the result: this validates the hardware at the number of units that were actually in the call, and a household with two frames has not measured a six-way one. The thresholds the memory watchdog uses in [guide 12](12-systemd-and-reliability.md) come from the fullest call this hardware has been put under, and a result recorded here is only comparable against calls of the same size.
