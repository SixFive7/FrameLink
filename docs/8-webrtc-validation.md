# Software Build Guide 08 — WebRTC Hardware Validation

The Pi 5 with 2 GB RAM has no hardware video decoder — Chromium software-decodes every WebRTC stream. This guide publishes five simulated video streams into a LiveKit room from the workstation, subscribes from the Pi, and monitors RAM, CPU, and temperature over four hours. The result is a pass or fail: if the Pi stays stable, proceed to guide 10; if it crashes or runs out of memory, switch to the Pi 5 4 GB or reduce the participant count before writing any production code.

---

<a id="1-install-the-livekit-cli"></a>
<img src="https://img.shields.io/badge/STEP_01-Install_the_LiveKit_CLI-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Install the LiveKit CLI"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Pi is the device under test — it cannot also simulate the five other callers in a six-way call.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Install a tool on the workstation that can impersonate five video callers by publishing fake streams into the LiveKit room.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The LiveKit CLI (`lk`) can join a room and publish a built-in demo video that loops indefinitely — one instance per simulated caller. Install via `winget` on Windows. Three environment variables (`LIVEKIT_URL`, `LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET`) configure `lk` for the server deployed in [guide 7](7-livekit-server.md). All commands in steps 1 through 4 run on the workstation in the same Git Bash session, not over SSH to the Pi.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
winget install --id LiveKit.LiveKitCLI --accept-source-agreements --accept-package-agreements
export LIVEKIT_URL="ws://<your-livekit-server>:7880"
export LIVEKIT_API_KEY="<your-api-key>"
export LIVEKIT_API_SECRET="<your-api-secret>"
mkdir -p ~/framelink-validation && cd ~/framelink-validation
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `winget` line finishes with "Successfully installed" or notes the package is already present. After installation, `lk version` prints a version number. Replace the three placeholder values in angle brackets with the WebSocket URL, API key, and API secret from your LiveKit deployment.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The workstation has the LiveKit CLI and knows how to reach the LiveKit server.

<a id="2-create-the-validation-test-page"></a>
<img src="https://img.shields.io/badge/STEP_02-Create_the_validation_test_page-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Create the validation test page"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Pi needs a webpage that connects to the LiveKit room and displays all five incoming video streams side by side.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Download the LiveKit JavaScript SDK and create a minimal HTML page that auto-subscribes to every video track.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The page reads the LiveKit server URL and room token from query-string parameters (`?ws=...&token=...`), so the same file works with any server — no editing needed. It creates a CSS grid cell for each incoming participant, labelled with the participant identity. `adaptiveStream: false` and `dynacast: false` ensure every stream arrives at full resolution. The LiveKit client SDK (`livekit-client.umd.js`) is downloaded from the npm CDN into the same directory. An uptime counter in the HUD helps confirm the page has not reloaded during the soak test.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -Lo livekit-client.umd.js "https://unpkg.com/livekit-client@2/dist/livekit-client.umd.js"
cat > validation.html << 'HTMLEOF'
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>FrameLink Validation</title>
<script src="livekit-client.umd.js"></script>
<style>
body { margin: 0; background: #000; display: flex; flex-wrap: wrap; height: 100vh; }
.cell { flex: 1 1 33.33%; height: 50vh; background: #111; overflow: hidden; position: relative; }
.cell video { width: 100%; height: 100%; object-fit: contain; }
.cell::after { content: attr(data-id); position: absolute; top: 4px; left: 4px; color: #ff0; font: bold 14px monospace; background: rgba(0,0,0,0.7); padding: 2px 6px; }
#hud { position: fixed; bottom: 0; width: 100%; color: #0f0; font: 12px monospace; background: rgba(0,0,0,0.8); padding: 6px 8px; text-align: center; z-index: 10; }
</style>
</head>
<body>
<div id="hud">Connecting...</div>
<script>
var params = new URLSearchParams(location.search);
var WS = params.get('ws') || '';
var TOKEN = params.get('token') || '';
var hud = document.getElementById('hud');
var startTime = Date.now();
function count() { return document.querySelectorAll('.cell video').length; }
function updateHud() {
  var n = count();
  var elapsed = Math.floor((Date.now() - startTime) / 60000);
  hud.textContent = 'Streams: ' + n + '/5 | Uptime: ' + Math.floor(elapsed/60) + 'h ' + (elapsed%60) + 'm';
  hud.style.color = n >= 5 ? '#0f0' : n > 0 ? '#ff0' : '#f00';
}
if (!TOKEN) { hud.textContent = 'Add ?ws=ws://server:port&token=JWT to the URL'; }
else {
  var room = new LivekitClient.Room({adaptiveStream: false, dynacast: false});
  room.on(LivekitClient.RoomEvent.TrackSubscribed, function(track, pub, pt) {
    if (track.kind !== LivekitClient.Track.Kind.Video) return;
    var cell = document.getElementById('cell-' + pt.identity);
    if (!cell) {
      cell = document.createElement('div');
      cell.className = 'cell';
      cell.id = 'cell-' + pt.identity;
      cell.dataset.id = pt.identity;
      document.body.insertBefore(cell, hud);
    }
    cell.querySelectorAll('video').forEach(function(v) { v.remove(); });
    cell.appendChild(track.attach());
    updateHud();
  });
  room.on(LivekitClient.RoomEvent.TrackUnsubscribed, function(track) {
    track.detach().forEach(function(el) { el.remove(); });
    updateHud();
  });
  room.on(LivekitClient.RoomEvent.Disconnected, function() { hud.textContent = 'DISCONNECTED'; hud.style.color = '#f00'; });
  room.on(LivekitClient.RoomEvent.Reconnected, function() { updateHud(); });
  setInterval(updateHud, 60000);
  room.connect(WS, TOKEN).then(function() { updateHud(); }).catch(function(e) { hud.textContent = 'Failed: ' + e.message; hud.style.color = '#f00'; });
}
</script>
</body>
</html>
HTMLEOF
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `curl` line downloads `livekit-client.umd.js` — verify it is not empty (`ls -la` should show several hundred kilobytes). The `cat` heredoc creates `validation.html`. Both files should exist in the working directory.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The validation test page and its JavaScript dependency are ready to serve.

<a id="3-serve-the-page-and-generate-a-token"></a>
<img src="https://img.shields.io/badge/STEP_03-Serve_the_page_and_generate_a_token-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Serve the page and generate a token"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Pi needs to load the test page over the network and authenticate with the LiveKit room.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Start a web server on the workstation and generate a room token the Pi will use to join.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Python's built-in HTTP server binds to all interfaces on port 8080 so the Pi can reach it. The `lk token create` command generates a JWT that authorises identity `pi-01` to join room `validation` with subscribe permissions. The token is valid for 24 hours — enough for the soak test with margin. Copy the token output for use in [step 5](#5-point-the-pi-at-the-test-page). The HTTP server runs in the background (`&`) so the terminal stays usable.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
python -m http.server 8080 --bind 0.0.0.0 &
lk token create --join --room validation --identity pi-01 --valid-for 24h
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The HTTP server prints `Serving HTTP on 0.0.0.0 port 8080`. The token command prints a single long string starting with `eyJ`. Copy this token — it is needed in [step 5](#5-point-the-pi-at-the-test-page).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The test page is accessible at `http://<workstation-ip>:8080/validation.html` and a room token is ready.

<a id="4-publish-five-simulated-streams"></a>
<img src="https://img.shields.io/badge/STEP_04-Publish_five_simulated_streams-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Publish five simulated streams"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The LiveKit room is empty — the Pi would connect and see no video.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Start five LiveKit CLI instances, each publishing a built-in demo video that loops indefinitely.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Each `lk room join` instance reads the environment variables set in [step 1](#1-install-the-livekit-cli) for server URL and credentials. The `--publish-demo` flag publishes a built-in test video that loops without ending, avoiding the problem of streams going black after a file finishes. All five run in the background. Each gets a unique identity (`sim-01` through `sim-05`). `-y` auto-confirms prompts and `--quiet` suppresses informational output to keep the terminal clean.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
lk room join --room validation --identity sim-01 --publish-demo -y --quiet &
lk room join --room validation --identity sim-02 --publish-demo -y --quiet &
lk room join --room validation --identity sim-03 --publish-demo -y --quiet &
lk room join --room validation --identity sim-04 --publish-demo -y --quiet &
lk room join --room validation --identity sim-05 --publish-demo -y --quiet &
lk room list
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `lk room list` output should show room `validation` with 5 participants and 5 publishers. If any publisher fails to join, check the environment variables from step 1 are still set in the current shell session.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Five looping video streams are flowing into the LiveKit room, simulating five callers.

<a id="5-point-the-pi-at-the-test-page"></a>
<img src="https://img.shields.io/badge/STEP_05-Point_the_Pi_at_the_test_page-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Point the Pi at the test page"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Pi's kiosk browser is showing its normal page — it needs to switch to the validation test page to subscribe to the five streams.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Temporarily swap the Chromium kiosk URL to the workstation's test page with the token embedded in the query string.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The `chromium-kiosk.service` user service (from [guide 5](5-kiosk-base.md)) launches Chromium with a URL in its `ExecStart` line. Back up the service file, then replace the URL with the validation page address including the LiveKit server URL and token as query parameters. `systemctl --user daemon-reload` picks up the change, and restarting the service relaunches Chromium. Replace `<workstation-ip>` with the workstation's LAN IP, `<livekit-ws-url>` with the LiveKit WebSocket URL (same as `LIVEKIT_URL` from step 1), and `<token>` with the JWT from [step 3](#3-serve-the-page-and-generate-a-token). The `\&` in the `sed` replacement inserts a literal `&` between query parameters.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cp ~/.config/systemd/user/chromium-kiosk.service ~/.config/systemd/user/chromium-kiosk.service.pre-validation
sed -i 's|http://.*|http://<workstation-ip>:8080/validation.html?ws=<livekit-ws-url>\&token=<token>|' ~/.config/systemd/user/chromium-kiosk.service
systemctl --user daemon-reload
systemctl --user restart chromium-kiosk.service
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The Pi's screen shows the validation page. The HUD at the bottom starts at "Connecting..." then changes to "Streams: 5/5" as video tiles appear. If it shows "Failed:" or stays on "Connecting...", check that the workstation IP, LiveKit URL, and token are correct.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Pi is receiving and software-decoding five WebRTC video streams in Chromium — the system is under full test load.

<a id="6-verify-streams-and-take-a-baseline"></a>
<img src="https://img.shields.io/badge/STEP_06-Verify_streams_and_take_a_baseline-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Verify streams and take a baseline"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Before committing to four hours, need to confirm all five streams are playing and the system is not already over capacity.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Visually check the screen shows five video tiles, then take a resource snapshot over SSH.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`free -m` shows RAM in megabytes — the `used` column under `Mem:` is the key number. `top -bn1 | head -20` shows the top CPU consumers — Chromium renderer processes dominate. `vcgencmd measure_temp` reads the SoC temperature. This baseline snapshot establishes the starting point before the soak test begins.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
free -m
top -bn1 | head -20
vcgencmd measure_temp
grep SwapTotal /proc/meminfo
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Five video tiles visible on the Pi's screen. RAM used should be under 1 GB at this point (comfortable headroom below the 1.5 GB ceiling). CPU will be high — 60–80% is expected with five software-decoded streams. Temperature should be under 70 °C. SwapTotal should be non-zero (ZRAM from [guide 2](2-sd-flash-first-boot.md)) and SwapFree should equal SwapTotal (nothing swapped yet).

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Baseline confirmed — five streams playing, system healthy, safe to start the soak test.

<a id="7-run-the-four-hour-soak-test"></a>
<img src="https://img.shields.io/badge/STEP_07-Run_the_four--hour_soak_test-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Run the four-hour soak test"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Chromium leaks memory over hours. A one-minute snapshot proves nothing about long-term stability.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Log resource measurements every 30 seconds for at least four hours, then review the log for dangerous trends.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

A `nohup` background loop writes timestamped RAM and temperature readings to `~/soak-test/resources.log`. The SSH session can be closed and reopened — the logging continues independently. After four hours (or longer), reconnect and review the log. The loop produces roughly 480 entries per four hours (one every 30 seconds). Optionally, install `grim` and add a second background loop to capture Wayland screenshots every ten minutes to `~/soak-test/screenshots/` — this provides visual proof that the streams stayed alive.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
mkdir -p ~/soak-test/screenshots
nohup bash -c 'echo "Soak test started: $(date)" > ~/soak-test/resources.log; while true; do echo "=== $(date +%H:%M:%S) ==="; free -m | grep Mem:; vcgencmd measure_temp; echo "---"; sleep 30; done >> ~/soak-test/resources.log 2>&1' &
nohup bash -c 'while true; do WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 grim ~/soak-test/screenshots/$(date +%H%M%S).png 2>/dev/null; sleep 600; done' &
echo "Soak test logging started — check back in 4+ hours"
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Run `tail -5 ~/soak-test/resources.log` to confirm the logger is writing. Run `ls ~/soak-test/screenshots/` to confirm screenshots are being captured. Do not stop the test early unless the Pi is visibly unresponsive.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The soak test is running. Return in four or more hours for the final evaluation.

<a id="8-evaluate-and-restore"></a>
<img src="https://img.shields.io/badge/STEP_08-Evaluate_and_restore-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 08 — Evaluate and restore"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The soak test has run — need a clear pass or fail, and the Pi needs to go back to its normal kiosk page.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Review the soak log for memory trends and crashes, make the go/no-go call, then restore the kiosk URL.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The five pass criteria are:
1. Peak RAM stays below 1.5 GB throughout (check the highest `used` value in the log).
2. Average CPU stays below 80%.
3. No OOM kills (`dmesg | grep -i oom` returns nothing).
4. No Chromium crashes (the validation page is still showing streams when you return).
5. Video remained smooth (no frozen tiles visible on screen).

If any criterion fails, the 2 GB Pi 5 is not viable at this participant count. Options: switch to Pi 5 4 GB, reduce to three or four participants, or lower the resolution. The `awk` pipeline below extracts peak RAM from the soak log. After evaluation, restore the backed-up kiosk service and clean up.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
wc -l ~/soak-test/resources.log
grep "Mem:" ~/soak-test/resources.log | awk '{print $3}' | sort -n | tail -1
dmesg | grep -i -e oom -e killed
tail -20 ~/soak-test/resources.log
ls ~/soak-test/screenshots/ | wc -l
cp ~/.config/systemd/user/chromium-kiosk.service.pre-validation ~/.config/systemd/user/chromium-kiosk.service
systemctl --user daemon-reload
systemctl --user restart chromium-kiosk.service
rm -rf ~/soak-test
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Not yet captured — to be recorded during the first validation session.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`wc -l` should show at least 1 440 lines (~480 entries × 3 lines each for a four-hour test). The `awk` pipeline prints the single highest `used` memory value in MB — this is the peak. `dmesg | grep` should return nothing (no OOM kills). The last 20 lines of the log should show stable numbers, not a climbing trend. After restoring the service, the Pi's screen returns to its normal kiosk page.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The hardware validation is complete with a clear pass or fail. If all five criteria are met, the 2 GB Pi 5 is confirmed viable — proceed to [guide 10](10-spa.md) to build the kiosk SPA. If any criterion failed, revisit the hardware choice before writing production code.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

Five simulated WebRTC video streams ran continuously for four hours on the 2 GB Pi 5 without exceeding 1.5 GB RAM, triggering an OOM kill, or crashing Chromium. The hardware is confirmed viable for a six-way video call. Proceed to [guide 10](10-spa.md) to build the kiosk SPA.
