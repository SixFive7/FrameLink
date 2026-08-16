# Software Build Guide 15 — The Fleet Manager on Your Workstation

Run the Fleet Manager on your own PC as a Docker container instead of starting it by hand with `dotnet run`, with the same bundled call server, the same database and the same address the frames already know. This is the guide in daily use; [guide 14](14-fleet-manager-deployment.md) is the same server on an always-on host behind a reverse proxy, and it is deferred until a release has been cut. Every step here runs on the Windows workstation — most of them in Git Bash from the repository root, and two of them in a PowerShell window opened with **Run as administrator**, which the step says so where it matters. Two things about Windows shape this guide more than anything else, and both were measured on this machine rather than reasoned about: Windows hands out the ports call media needs as temporary ports to other programs, and a database file shared between Windows and a Linux container is not safely shared at all. Steps 1 and 5 are what those two facts turn into. **About the captured output:** every EXPECTED OUTPUT block is real output from this workstation. Image digests, container ids, port counts, LiveKit node ids and timestamps will differ on yours. Where a block could not be captured — because the command needs an Administrator shell this session did not have, or because running it would have taken the port a live frame is using — LOOK FOR says so in those words rather than showing something invented.

---

<a id="1-reserve-the-media-ports-windows-lends-out"></a>
<img src="https://img.shields.io/badge/STEP_01-Reserve_the_media_ports_Windows_lends_out-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 01 — Reserve the media ports Windows lends out"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Video calls need sixty numbered network channels, and Windows treats those same sixty numbers as scratch space it can lend to any program that asks. Sometimes a few are already lent out when the call server starts, and sometimes that stops the whole thing from starting at all.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Tell Windows once, permanently, to stop lending those sixty numbers to anybody. It is a single command, it opens nothing, and it survives restarts.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The UDP range `50000-50059` is published one-to-one — host port *n* maps to container port *n* and to nothing else. That is not a convention. The call server runs with external-IP discovery switched off and advertises the address it is genuinely on, so a connection completes only because the source port a frame observes is a port this host will deliver back to the same place. Remap the range and calls connect for nobody, with no error printed anywhere, which is why the fix below is a reservation rather than a different range.

Windows allocates ephemeral ports from a dynamic range that begins at 49152 and is 16384 wide — 49152 to 65535 — so the entire media range sits inside it. Any program that opens an outbound UDP socket can be handed one of these numbers, and it keeps it for as long as it likes. Measured here while the range was still two hundred wide: a probe container published 198 of 200, with `50005` held by a `svchost` socket on `127.0.0.1` and `50082` held by Microsoft Teams. Neither is a fixed service, so *which* two are missing changes between attempts, and on one attempt Docker Compose did not degrade at all — it refused outright with `failed to bind host port 0.0.0.0:50060/udp: address already in use` and the container never started. A stack that starts on Tuesday and refuses on Wednesday for a reason nobody can see is worse than one that never starts, which is what makes this step first.

Sixty is the width because a reservation is what makes the range dependable, and a reservation is a fixed grant: this workstation holds `50000-50059`, so that is what the stack asks for and the two agree by construction. The call server takes one port per participant connection, so sixty is an order of magnitude past a household's need — [guide 13](13-multi-device-deploy.md) contemplates several frames in one house, not sixty people in one call. To widen it, widen both ends together: `numberofports` here, the `ports:` entry in the stack file, and `FRAMELINK_LIVEKIT_UDP_END` so the call server is told about it.

The RUN block reads the current state, then adds the reservation if it is not already there, then reads it back. Line by line: line 1 shows the dynamic range, so you can see for yourself that it swallows the media range; line 2 shows which port ranges are already withheld from it; line 3 is the guarded reservation — `grep -q` first, so a second run of this guide changes nothing; line 4 proves the reservation is there. `store=persistent` is what makes it survive a reboot, and it is applied early in boot, before ordinary programs are running, which is also why a reboot is the reliable way to make it take effect: `netsh` refuses to reserve a range while any port inside it is in use at that moment. **Line 3 needs a PowerShell or Command Prompt window opened with Run as administrator.** Lines 1, 2 and 4 do not.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
netsh int ipv4 show dynamicport udp
netsh int ipv4 show excludedportrange protocol=udp
netsh int ipv4 show excludedportrange protocol=udp | grep -qE '^ +50000 +50059' || netsh int ipv4 add excludedportrange protocol=udp startport=50000 numberofports=60 store=persistent
netsh int ipv4 show excludedportrange protocol=udp
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text

Protocol udp Dynamic Port Range
---------------------------------
Start Port      : 49152
Number of Ports : 16384


Protocol udp Port Exclusion Ranges

Start Port    End Port      
----------    --------      
     50000       50059     *
     57566       57665      
     57666       57765      
     59236       59335      
     59336       59435      

* - Administered port exclusions.


Protocol udp Port Exclusion Ranges

Start Port    End Port      
----------    --------      
     50000       50059     *
     57566       57665      
     57666       57765      
     59236       59335      
     59336       59435      

* - Administered port exclusions.

```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`Start Port : 49152` with `Number of Ports : 16384` covers 49152 to 65535 and therefore covers 50000 to 50059 — that is the problem, stated in two lines by Windows itself. Then the row that answers it: `50000       50059     *`, where the star means an administered exclusion. It appears twice because lines 2 and 4 print the same table. **The capture above was taken on a workstation where the reservation was already in place, so line 3 printed nothing at all — the `grep -q` matched and the `netsh add` never ran.** That is the idempotent path and it is what a second run of this guide looks like; the first run instead shows no `50000` row in line 2's table, `Ok.` from line 3, and the row present in line 4's. The other excluded ranges belong to other software, will differ on your machine, and do not overlap the media range. If line 3 answers `The requested operation requires elevation`, the window is not an Administrator one. If it answers `The process cannot access the file because it is being used by another process`, some program is holding a port inside the range right now: reboot and run the line again as the first thing you do, which is the state in which nothing has had a chance to take one. If line 4 still shows no `50000` row, the reservation did not happen and every later step will still mostly work — you will simply lose a call for one participant now and then, and the stack will occasionally refuse to start.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The sixty numbers call audio and video travel over now belong to FrameLink and to nothing else on this PC, permanently, so the call server can always have all of them.

<a id="2-let-the-fleet-reach-the-container"></a>
<img src="https://img.shields.io/badge/STEP_02-Let_the_fleet_reach_the_container-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 02 — Let the fleet reach the container"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A program you start by hand on this PC can be reached by the frames over the house network. The very same program inside Docker cannot, and nothing anywhere says so — the container looks perfectly healthy and the frames simply never arrive.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Docker runs inside a small virtual machine, and Windows puts a second, separate firewall around that machine which blocks everything from outside by default. Open exactly the ports the fleet needs on that second firewall, and nothing else.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

This workstation's `C:\Users\jori\.wslconfig` sets `networkingMode=mirrored`, which makes the Linux virtual machine share this PC's network interfaces rather than sit behind a private one. A consequence of that sharing is that ports Docker publishes are bound *inside* the virtual machine, so they are governed by the Hyper-V firewall — a separate rule set from the Windows Firewall you see in the control panel, with its own default action per virtual machine.

The effect was measured rather than guessed, from a network namespace that is not this PC's own network stack, which is the closest thing available to standing where the frame stands. Against `10.20.30.200:5199`, the Fleet Manager started by hand as an ordinary Windows program: `200` in three milliseconds. Against `10.20.30.200:5299` and `10.20.30.200:7880`, both published by Docker: both timed out after five seconds. Same address, same host, same moment; the difference is which side of the virtual machine boundary the listener is on.

The RUN block's first two lines say why in one screen. The virtual machine's default inbound action is `Block` and its loopback is enabled — which is precisely the observed behaviour, because `127.0.0.1` is loopback and the LAN address is not — and the only inbound rules that allow anything are ICMP and mDNS. There is no rule permitting TCP or UDP from anywhere, so the frames' traffic is dropped before it reaches Docker at all.

Lines 3 and 4 add two rules scoped to that one virtual machine and to these ports only: TCP `5199` for the console and the frames' own websocket, TCP `7880` and `7881` for call signalling and its TCP media fallback, and UDP `50000-50059` for call media. Both are guarded on their own name, so running this guide twice creates nothing twice. **Both need a PowerShell window opened with Run as administrator.** The alternative fix is to delete `networkingMode=mirrored` from `.wslconfig` and run `wsl --shutdown`, which restores Docker's ordinary port publishing on all interfaces — it is fewer moving parts, and it restarts every container on the machine and changes a setting that was presumably chosen for a reason, so it is mentioned rather than recommended.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
powershell -NoProfile -Command "Get-NetFirewallHyperVVMSetting -Name '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -PolicyStore ActiveStore | Format-List Name,DefaultInboundAction,LoopbackEnabled"
powershell -NoProfile -Command "Get-NetFirewallHyperVRule -PolicyStore ActiveStore -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' | Where-Object { \$_.Direction -eq 'Inbound' -and \$_.Action -eq 'Allow' -and \$_.Enabled -eq 'True' } | Select-Object -ExpandProperty DisplayName"
powershell -NoProfile -Command "if (-not (Get-NetFirewallHyperVRule -Name 'FrameLink-Dev-TCP' -ErrorAction SilentlyContinue)) { New-NetFirewallHyperVRule -Name 'FrameLink-Dev-TCP' -DisplayName 'FrameLink dev stack (TCP)' -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Direction Inbound -Protocol TCP -LocalPorts 5199,7880,7881 -Action Allow }"
powershell -NoProfile -Command "if (-not (Get-NetFirewallHyperVRule -Name 'FrameLink-Dev-UDP' -ErrorAction SilentlyContinue)) { New-NetFirewallHyperVRule -Name 'FrameLink-Dev-UDP' -DisplayName 'FrameLink dev stack (UDP media)' -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -Direction Inbound -Protocol UDP -LocalPorts 50000-50059 -Action Allow }"
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
Name                 : {40E0AC32-46A5-438A-A0B2-2B479E8F2E90}
DefaultInboundAction : Block
LoopbackEnabled      : True



WslCore Inbound ICMPv4 Default Allow Rule
WslCore Inbound ICMPv6 Default Allow Rule
WslCore Inbound IPv4 mDNS Default Allow Rule
WslCore Inbound IPv6 mDNS Default Allow Rule
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`DefaultInboundAction : Block` together with a list containing only ICMP and mDNS is the diagnosis in two lines: nothing is allowed in except pings and name lookups, so no frame can reach anything Docker publishes. **That block is the output of lines 1 and 2 only. Lines 3 and 4 were not run and their output was not captured, because creating a Hyper-V firewall rule needs an Administrator shell that this session did not have** — run them yourself in an elevated window, then re-run line 2 and look for `FrameLink dev stack (TCP)` and `FrameLink dev stack (UDP media)` in the list. `New-NetFirewallHyperVRule: Access is denied` means the window is not elevated. `The term 'New-NetFirewallHyperVRule' is not recognized` means this build of Windows predates the Hyper-V firewall, in which case the `.wslconfig` route in the technical explanation is the only one available. If your `.wslconfig` has no `networkingMode=mirrored` line at all then none of this applies to you: Docker publishes ports as ordinary Windows listeners and the frames can already reach them. The real proof either way is step 7, which asks the question from outside this PC's own network stack.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You know why a container that looks healthy can be unreachable, and the frames' console traffic, call signalling and call media have an explicit path in — and nothing else does.

<a id="3-build-the-fleet-manager-image"></a>
<img src="https://img.shields.io/badge/STEP_03-Build_the_Fleet_Manager_image-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 03 — Build the Fleet Manager image"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The Fleet Manager is source code, and Docker can only run a finished, packaged program.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Run one script. It compiles the server, packs it into a container image, starts that image, and refuses to call the build a success until the thing it built actually answers a request.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`deploy/fleet-manager/build-image.sh` drives a two-stage build: the .NET SDK image plus the Native AOT toolchain publishes the server as one self-contained executable, and `debian:trixie-slim` plus four packages delivers it. There is no .NET runtime in the delivered image, which is why it comes out around 63 MB rather than several hundred. It is the same script and the same image [guide 14](14-fleet-manager-deployment.md) uses on a server; nothing about the image is development-specific, and that is deliberate — the thing you develop against should be the thing you eventually deploy.

Three lines of its output are worth reading rather than skimming. **`agent payload`** names the agent binary baked into the image, which the Fleet Manager serves to every frame as its update feed; a checkout that has never run `python tools/harness/fl.py build` says `NONE`, which is a valid image that simply cannot update a fleet. **`version=0.0.0+<sha>.dirty`** means the working tree had uncommitted changes, so this artifact is not reproducible — printed as a warning rather than enforced, and entirely normal during development. **The tag substitutes `-` for `+`** because an OCI tag may not contain `+`; the label inside the image keeps the real string.

The last line prints the tag. Keep it: step 6 needs it, and step 9's rollback is nothing more than starting the stack again with a previous one, which is a question a moving tag like `:latest` cannot answer.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
cd /c/Source/SixFive7/FrameLink
bash deploy/fleet-manager/build-image.sh
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[image] repo=/c/Source/SixFive7/FrameLink
[image] version=0.0.0+74cdedf.dirty  tag=framelink/fleet-manager:0.0.0-74cdedf.dirty
[image] platform=linux/amd64  rid=linux-x64
[image] agent payload: linux-arm64 0.0.0+74cdedf.dirty
[image] docker build ...
...
[image] smoke test: starting the image with no configuration (§3.2's unconfigured state)
[image] container be3038031f1c on 127.0.0.1:32768
[image] unconfigured instance names its own variable, as §3.2 requires
[image] image framelink/fleet-manager:0.0.0-74cdedf.dirty  63075392 bytes
[image] digest sha256:ffc086a2e9030dbb286a6d40af34b44da051f82f6a054606a92eb6a5a69fcc37
[image] done. Deploy with FRAMELINK_IMAGE=framelink/fleet-manager:0.0.0-74cdedf.dirty
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The `...` stands in for several hundred lines of Docker build progress, which are noise unless something fails; the four lines after it are what matter. `unconfigured instance names its own variable` is the smoke test passing — the script started the image it had just built, waited for its health endpoint, and confirmed the server tells an anonymous caller which environment variable it is missing. A build that never reaches that line produced an image that does not run, and the script exits 7 after printing the container's own last forty log lines. **Write down the tag on the final line.** If `agent payload` says `NONE`, run `python tools/harness/fl.py build` first and build again; if you are only working on the server that does not matter yet.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have a container image that has been proven to start and serve on this machine, tagged with a name that will never mean anything else.

<a id="4-create-the-volume-that-must-outlive-the-stack"></a>
<img src="https://img.shields.io/badge/STEP_04-Create_the_volume_that_must_outlive_the_stack-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 04 — Create the volume that must outlive the stack"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

Everything the Fleet Manager knows about your frames — which ones you adopted, what you named them, and the secret that lets them make calls — lives in one small file. If that file is ever deleted, every frame in the house becomes a stranger the server has never met.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Create the storage for that file by hand, once, before anything else exists, so that no later mistake with the stack can take it away with it.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

The stack file declares this volume `external: true`, which means Compose will never create it, never adopt it and — the part that matters — never remove it. `docker compose down -v`, a wrong project name and a mistyped command all leave an external volume exactly where it was. The cost of that protection is this one command; everything else here is disposable and this is not.

What it holds is worth being precise about, because it is not simply "data". Identity in FrameLink is the keypair the agent generates on its own first boot, and adoption is the act of binding that key to a record. A Fleet Manager that has lost the record has not lost a row — it has lost the binding, and every frame it has ever adopted reappears in the pending queue as an unknown device that has to be adopted again by hand. The volume also carries the call server's API secret, which every call token in the fleet is signed with, and the call server binary itself.

It is named `framelink-data` rather than something with "dev" in it on purpose. After step 8 this volume holds the real fleet database, and a name that suggested it was scratch would be a name that eventually gets somebody to delete it. `docker volume create` is idempotent by construction: run it twice and the second run prints the same name and changes nothing.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker volume create framelink-data
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
framelink-data
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

One line, the volume's name, and nothing else. Printing the name again on a second run is correct and is not a warning — the command is a no-op against a volume that already exists. `error during connect` means Docker Desktop is not running.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The one piece of storage that must survive every future mistake now exists, and it exists independently of the stack that is about to use it.

<a id="5-move-an-existing-fleet-database-into-the-volume"></a>
<img src="https://img.shields.io/badge/STEP_05-Move_an_existing_fleet_database_into_the_volume-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 05 — Move an existing fleet database into the volume"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

You already have a Fleet Manager database sitting in an ordinary Windows folder, with frames adopted into it. The container cannot simply read that folder, and the obvious way of making it — pointing the container at the folder — quietly corrupts the file.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Copy the database into the volume once, with a script that checks the copy arrived byte for byte and never touches the folder it came from.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Skip this step entirely if you have no existing database — step 6 will create a fresh one — but do not skip it and come back later, because a volume that already holds a database is one this script refuses to overwrite.

**Why a copy and not a shared folder.** Docker Desktop presents a Windows folder to a Linux container over 9p, and SQLite's file locking does not survive that crossing. Measured here, on a copy of a real fleet database: with a process on the Windows side holding `BEGIN EXCLUSIVE` on the file, a write issued inside the container returned success in fifty milliseconds — and afterwards the two sides disagreed about the contents, with the container able to read a fleet setting the Windows side could not see. Two writers, no mutual exclusion, no error anywhere. The file that would eventually be destroyed is the one holding every adopted frame's identity binding. The same comparison also settles the performance question in the same direction, and by less than you would guess: three hundred writes took 6.2 seconds through the shared folder and 3.3 seconds in the volume. Correctness is the argument; speed is a bonus.

**Why the script and not a `docker cp`.** Three things need to be true and none of them is automatic. The copy travels as a tar stream on standard input rather than through a shared folder, because a shared folder is the mechanism whose locking does not work here and because Git Bash silently rewrites a `-v C:\...:/data` argument into a path that does not exist. The files land owned by `10001`, the unprivileged user the image runs as. And the destination is checked with `sha256sum` against the source afterwards, which turns "the command did not fail" into "the database on the other side is the database that went in".

**It refuses rather than risks.** A `framelink.db-wal` file beside the database means SQLite did not close cleanly, which on a live folder means a Fleet Manager still has it open; copying a database and its write-ahead log while a writer is mid-transaction produces a set of files that do not agree with each other, and the symptom arrives days later as a fleet that has forgotten a frame. So the script stops and says so. A volume that already holds a database is left alone and the script exits successfully, because a second run is far more likely to be somebody repeating a procedure than somebody asking to overwrite a live fleet with an older copy.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
bash deploy/fleet-manager/import-database.sh /c/Users/jori/framelink-control-data
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
[import] source /c/Users/jori/framelink-scratch/devstack/src-clean
[import] volume framelink-data
[import] volume framelink-data exists
[import] source sha256 e2a7ba018def2e991a297a971d3e59d0b57ae5668d37f01b0441f721162175ed
[import] imported sha256 e2a7ba018def2e991a297a971d3e59d0b57ae5668d37f01b0441f721162175ed — byte for byte identical
total 628
drwxr-xr-x 2 10001 10001   4096 Aug 16 03:15 .
drwxr-xr-x 1 root  root    4096 Aug 16 03:15 ..
-rw-r----- 1 10001 10001 634880 Aug 16 03:03 framelink.db
[import] done. The source directory was not modified.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

The two `sha256` lines must be the same string, and the line that says so is the whole point of the step. One `framelink.db` in the listing, owned by `10001 10001` — that is the unprivileged user inside the image, and a file owned by anyone else is one the Fleet Manager cannot write. **The capture above names a scratch folder as its source rather than the folder in the command, because it was taken against a copy of the live database while the live one was still in use; the source path is the only line that differs.** Two refusals are normal rather than faults. `ERROR: ...framelink.db-wal exists, so a Fleet Manager still has this database open` means exactly what it says: stop whatever is running, wait for the `-wal` and `-shm` files to disappear from the folder, and run it again — this was captured verbatim from a real run against the live folder while the Fleet Manager was up, and the folder was untouched afterwards. `the volume already holds framelink.db (sha256 ...)` followed by `nothing to do` is the idempotent path, and on a second run it is the correct answer.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

Your existing fleet — every adopted frame, its name, its settings and its call credential — is now inside the volume, verified identical, with the original still sitting untouched where it was.

<a id="6-start-the-stack-on-a-spare-port"></a>
<img src="https://img.shields.io/badge/STEP_06-Start_the_stack_on_a_spare_port-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 06 — Start the stack on a spare port"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The image needs storage, a password, an address and a set of network ports before it is a service rather than a program — and it must not disturb the Fleet Manager the frames are currently talking to while you are still finding out whether it works.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Start it on a spare address that nothing is using, prove it there, and only move it to the real one in step 8.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

`deploy/fleet-manager/framelink.dev.yml` is a sibling of the deployment stack rather than a variant of it. That one describes a server behind Traefik, Authelia, a certificate resolver and a firewall generator; none of that exists on a workstation, and a Traefik label with no Traefik reading it is not a harmless leftover — it reads like security that is present when it is not. So this file carries none of them, uses plain HTTP, and says so.

Plain HTTP is a real trade and worth stating once: there is no certificate and no name, a frame reaches this Fleet Manager as `http://<this PC's LAN address>:5199`, and the operator password crosses the house network in the clear when you sign in. That is acceptable for a workstation on a home network and is not acceptable anywhere else, and it is one of the reasons this is not the deployment file.

The password comes from the shell rather than from a file. An `.env` beside a compose file is a password on disk, which this project forbids, so `read -rsp` takes it without echoing it and without leaving it in shell history, and the compose file refuses to render at all when the variable is unset — naming the variable in the error. Every `docker compose` command for this stack needs that variable set in the same window, because Compose reads the whole file every time; `docker logs` and `docker ps` do not, which is why the later steps use those for inspection.

`FRAMELINK_HTTP_PORT=5299` is the spare address. The default in the compose file is `5199`, and 5199 is not an arbitrary choice: it is the port already written into the frame's own `endpoints.json`, which the agent persists when it is installed and — by design — never rediscovers. That is exactly what makes step 8's cutover invisible to the fleet, and exactly why you do not want to take that port until you are ready.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
read -rsp 'operator password (24 characters or more): ' FRAMELINK_OPERATOR_PASSWORD
export FRAMELINK_OPERATOR_PASSWORD
export FRAMELINK_IMAGE=framelink/fleet-manager:0.0.0-74cdedf.dirty
export FRAMELINK_HTTP_PORT=5299
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml up -d
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Network framelink-dev Creating
 Network framelink-dev Created
 Container framelink-dev Creating
 Container framelink-dev Created
 Container framelink-dev Starting
 Container framelink-dev Started
NAME            IMAGE                                         STATUS
framelink-dev   framelink/fleet-manager:0.0.0-74cdedf.dirty   Up 12 seconds (health: starting)
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Six lines ending in `Started`, then a table with the tag you built in step 3 and never `latest`. The `read` on the first line prints only its own prompt and takes no visible input, so the capture above begins at the first Compose line. `health: starting` is correct for the first twenty seconds and becomes `healthy` after it; re-run the last command to watch it change. Three failures are worth naming. `error while interpolating services.framelink.environment.FRAMELINK_OPERATOR_PASSWORD: required variable FRAMELINK_OPERATOR_PASSWORD is missing a value: export it in this shell first` means the variable is not set in this window — Compose is refusing rather than starting something unconfigured, which is the intended behaviour, and it is the same refusal for every `docker compose` command including `ps` and `stop`. `failed to bind host port 0.0.0.0:50060/udp: address already in use` means step 1's reservation is not in place and something borrowed a media port; the port number in the message changes every time, which is the tell. `external volume "framelink-data" not found` means step 4 was skipped.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Fleet Manager is running in Docker on this PC with its storage, its password and its media ports, on an address nothing else is using — and the instance the frames are talking to has not been touched.

<a id="7-prove-the-console-and-the-call-server"></a>
<img src="https://img.shields.io/badge/STEP_07-Prove_the_console_and_the_call_server-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 07 — Prove the console and the call server"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

A container that says it is healthy has only checked itself. It has not shown that the call server inside it is running, that the two hundred media ports actually got published, or that anything outside this PC can reach any of it.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Ask it four questions from outside the container, and one of them from outside this PC entirely.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Line by line. `/healthz` is the liveness probe the container's own health check uses and answers `ok` and nothing else. `/api/status` is the interesting one and is deliberately reachable with no session at all: it is what the browser asks before anybody has signed in, and `"configured":true` proves the password arrived from your shell intact — `false` arrives with a `problem` field saying whether the variable is unset or shorter than the twenty-four character floor. Neither answer contains the password or any part of it; the credential is compared as a digest and never rendered.

The third line asks the call server itself whether it is listening on the signalling port. On its first start the Fleet Manager downloads the pinned `livekit-server` release, refuses it unless it matches both the checksum upstream publishes and the digest recorded in this repository, unpacks it into the volume, generates its configuration with a freshly created API secret and starts it as a child process. That is why the first start logs `installed` and every later one does not — the installer hashes what is already on disk and returns without fetching a byte.

The fourth line counts published UDP mappings, and the fifth is the one that matters most. It runs a throwaway container in the Docker virtual machine's own network namespace and has it call this PC's LAN address — which is not this PC's own network stack, and is therefore the closest available stand-in for a frame. A success there is the thing step 2 was for. It is also the check that fails first and most confusingly if step 2 was skipped, because everything on `127.0.0.1` will have worked perfectly.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -fsS http://127.0.0.1:5299/healthz
curl -fsS http://127.0.0.1:5299/api/status
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:7880/
docker port framelink-dev | grep -c udp
docker logs framelink-dev 2>&1 | head -8
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
ok
{"configured":true,"variable":"FRAMELINK_OPERATOR_PASSWORD"}
200
198
info: fl-control[1000]
      FrameLink Fleet Manager starting with an operator password configured.
info: FrameLink.Control.Alerting.FleetWatch[1901]
      ALERT call-server-down cleared [call-server-down]: No frame in this fleet can place a call
info: FrameLink.Control.LiveKit.LiveKitService[1809]
      LiveKit 1.13.5 installed at /var/lib/fl-control/livekit/livekit-server.
info: FrameLink.Control.LiveKit.LiveKitService[1810]
      LiveKit 1.13.5 started as pid 22, signalling on port 7880.
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

`ok`, then `"configured":true`, then `200` from the call server, then a count, then `started as pid`. The count is `198` here and not `200` for the reason step 1 gives — two media ports were on loan to other programs at the moment the container started — and after step 1's reservation it should read `200`. A count in the single digits means the range did not publish at all. The `ALERT ... cleared` line appears only when the database you imported carried an open alert that this instance has just resolved, which is a good sign rather than a warning: the state came across. On any start after the first, the `installed at` line is **absent**, and that is the correct idempotent behaviour. `refused: the download does not match the checksum upstream published` means the release was tampered with or the pin is stale, and the Fleet Manager will not run it — everything except calling keeps working. **The off-host reachability check is not in the captured block above, and its result on this workstation was a failure rather than a success:** run `docker run --rm --network host --entrypoint sh framelink/fleet-manager:latest -c "curl -sS -m 5 -o /dev/null -w '%{http_code}\n' http://10.20.30.200:5299/healthz"` and compare it against the same request to the Fleet Manager you start by hand. Before step 2's firewall rules exist, the hand-started one answered `200` in three milliseconds and the container timed out after five seconds. Anything other than a prompt `200` from the container means step 2 is unfinished, and no frame will be able to connect in step 8.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You have seen the console answer, the password arrive, the call server download itself and start, the media range publish, and — the only one that decides whether step 8 can work — whether anything outside this PC can reach any of it.

<a id="8-cut-over-from-dotnet-run-to-the-container"></a>
<img src="https://img.shields.io/badge/STEP_08-Cut_over_from_dotnet_run_to_the_container-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 08 — Cut over from dotnet run to the container"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

The frames are talking to a Fleet Manager you started by hand, and its database is the live one. Swapping in the container means moving that database across without losing a single frame's identity, and doing it in a way that is safe to repeat if you are interrupted halfway.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Stop the hand-started one, copy its database into the volume, start the container on the same address the frames already know, and watch a frame come back.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Nothing on any frame changes, and the reason is worth understanding because it is what makes this cheap. When an agent is installed it is given the Fleet Manager's address, and it writes that address into its own `endpoints.json` and then never asks anything again — not a cache with a refresh, an early return. So a frame pointed at `http://10.20.30.200:5199` stays pointed there for as long as it exists, and the entire cutover is the question of who is listening on that port.

Every line is safe to run twice. Line 1 asks whether the hand-started instance is still up, and you want it to fail. Line 2 stops the container from step 6 and leaves the volume alone. Line 3 is the import, which refuses if the database is still open, refuses if the volume already holds one, and otherwise verifies the copy by hash. Lines 4 and 5 start the stack on 5199. If you get interrupted after line 3 and start again from the top, line 3 says `nothing to do` and lines 4 and 5 recreate a container that was already correct.

The order matters in one place. Stop the hand-started Fleet Manager **before** the import, not after: SQLite in write-ahead-logging mode keeps three files while it is open, a clean shutdown folds the log back into `framelink.db` and deletes the other two, and the import refuses to run while it can see them. Waiting for the folder to hold one file is the whole of the safety check.

Going back is the same sequence in reverse and takes about a minute: `docker compose ... down`, then start the Fleet Manager by hand again against the original folder, which is still exactly where it was because the import only ever read from it. That is the property to keep in mind if anything below does not go the way it should — nothing you have done is one-way.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
curl -sS -m 3 http://127.0.0.1:5199/healthz
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml down
bash deploy/fleet-manager/import-database.sh /c/Users/jori/framelink-control-data
export FRAMELINK_HTTP_PORT=5199
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml up -d
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Container framelink-dev Stopping
 Container framelink-dev Stopped
 Container framelink-dev Removing
 Container framelink-dev Removed
 Network framelink-dev Removing
 Network framelink-dev Removed
DRIVER    VOLUME NAME
local     framelink-data
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Line 1 must **fail** with a `curl: (7)` connection refusal naming port 5199 before you go any further — the exact wording of the rest of that line varies between curl builds and does not matter. If it answers `ok`, the hand-started Fleet Manager is still running: stop it in the window you started it in, and check the folder until `framelink.db` is the only file left in it. The captured block above is lines 2 and a `docker volume ls` run straight afterwards, included together because the pair is the thing worth seeing — Compose removed the container and the network and did not touch the volume. **Lines 4 and 5 were not captured, because running them would have taken port 5199 from the Fleet Manager a live frame was using at the time.** Their output is line for line what step 6 showed, with `5199` in place of `5299`; if Docker instead answers `address already in use` for 5199, line 1 was not really failing. Line 3's output is step 5's. The real acceptance test is not in this block at all: run `docker logs -f framelink-dev` and wait for a frame to reconnect, which takes under a minute, then open `http://127.0.0.1:5199` in a browser, sign in, and confirm the frame is listed under the name you gave it rather than sitting in the adoption queue as a stranger. A frame in the queue means the volume holds the wrong database — go back, and step 9's archive is what you restore.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

The Fleet Manager the fleet talks to is now a container rather than a program in a terminal window, at the same address, with the same frames adopted under the same names — and the folder it came from is still there, untouched, if you want to go back.

<a id="9-back-up-the-database-and-roll-back"></a>
<img src="https://img.shields.io/badge/STEP_09-Back_up_the_database_and_roll_back-555555?style=for-the-badge&labelColor=228b22" height="50" alt="Step 09 — Back up the database and roll back"/>

![PROBLEM](https://img.shields.io/badge/🤔-PROBLEM-e05d44?style=flat-square)

There is no backup button anywhere in FrameLink, the one file that matters is now inside a Docker volume you cannot open from Explorer, and sooner or later you will build a version that behaves worse than the one it replaced.

![APPROACH](https://img.shields.io/badge/💡-APPROACH-fbbf24?style=flat-square)

Stop the server for a few seconds, copy the volume's contents into one compressed file, and start it again. Going back to an older version is a separate, simpler thing: name the older image and start the stack again.

![TECHNICAL EXPLANATION](https://img.shields.io/badge/🧠-TECHNICAL_EXPLANATION-8a2be2?style=flat-square)

Stopping first is what makes the copy trustworthy. The database is SQLite in write-ahead-logging mode, so while it is running there are three files and copying them one at a time can produce a set that does not agree with itself. A clean shutdown folds the log back into the database and deletes the other two, which is why the archive below contains exactly one file. The frames are unharmed by the pause: one that was green when contact dropped keeps showing photos and simply reconnects.

`images/` and `livekit/` are excluded on purpose — generated SD-card images at roughly 3 GB each, and the 50 MB call-server binary. Both are reproducible, so including them would turn a 56 kB backup into a multi-gigabyte one that is no more useful. `MSYS_NO_PATHCONV=1` is on the two `docker run` lines because Git Bash otherwise rewrites `/data` into a Windows path inside the container's own arguments and the copy fails on a folder nobody typed; it is the single most confusing Windows-specific failure in this whole guide, and it announces itself with `Cannot open: No such file or directory` naming a path containing `Program Files/Git`.

Rolling back to an older build is one edit and no restore, and the reason is a rule rather than luck: the database schema only ever grows. Tables and nullable columns are added; nothing is repurposed or removed. An older Fleet Manager started against a newer volume therefore finds every table and column it knows about exactly where it left them and never reads the ones added after it. That was exercised here — an image built ten commits earlier came up healthy against a database written by the current build, signed a session in, took three hundred writes without a failure and still listed the adopted frame under its own name — and it is a property to defend rather than assume, so a future change that renames or drops a column is a change that breaks rollback and must say so.

![RUN THESE COMMANDS OVER SSH](https://img.shields.io/badge/👤-RUN_THESE_COMMANDS_OVER_SSH-1e40af?style=flat-square)

```bash
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml stop
MSYS_NO_PATHCONV=1 docker run --rm -v framelink-data:/data:ro debian:trixie-slim tar czf - -C /data --exclude=images --exclude=livekit . > framelink-data.tar.gz
MSYS_NO_PATHCONV=1 docker run --rm -i debian:trixie-slim tar tzvf - < framelink-data.tar.gz
docker compose -p framelink -f deploy/fleet-manager/framelink.dev.yml start
```

![EXPECTED OUTPUT](https://img.shields.io/badge/🍓-EXPECTED_OUTPUT-0d9488?style=flat-square)

```text
 Container framelink-dev Stopping
 Container framelink-dev Stopped
drwxr-xr-x 10001/10001       0 2026-08-16 03:23 ./
-rw-r----- 10001/10001  634880 2026-08-16 03:23 ./framelink.db
 Container framelink-dev Starting
 Container framelink-dev Started
```

![LOOK FOR](https://img.shields.io/badge/🔎-LOOK_FOR-ea580c?style=flat-square)

Exactly one `framelink.db` in the listing, owned by `10001/10001`. **A `framelink.db-wal` or `framelink.db-shm` in the listing means the container had not finished stopping when the copy ran** — wait for the `Stopped` line before starting the archive; that failure was reproduced here by running the copy without the stop, and it produced a three-file archive that is not safe to restore from. The database is around 600 kB on a small fleet and the archive around 56 kB, so keep every copy you ever make, somewhere that is not this disk. Restoring is the same shape in reverse against a stopped stack: `MSYS_NO_PATHCONV=1 docker run --rm -i -v framelink-data:/data debian:trixie-slim tar xzf - < framelink-data.tar.gz`. To roll back to an older build instead, set `FRAMELINK_IMAGE` to the earlier tag and run step 6's `up -d` again — `Recreate` and `Recreated` rather than `Creating` is what tells you Compose replaced a container instead of finding none, and `Up ... (healthy)` afterwards is the older build reading the newer database without complaint.

![ACHIEVED](https://img.shields.io/badge/🏆-ACHIEVED-228b22?style=flat-square)

You hold a small, consistent, restorable copy of every adopted frame's identity, name, settings and call credential, and you can move the Fleet Manager forwards and backwards between builds with one variable.

---

<br>

![CHECKPOINT](https://img.shields.io/badge/🚩-CHECKPOINT-228b22?style=for-the-badge)

`docker ps` shows `framelink-dev` `Up ... (healthy)` running the immutable tag you built, `docker port framelink-dev | grep -c udp` reports the media range published, and `docker logs framelink-dev` shows the call server started as a child process. A frame that was adopted before the cutover reconnects on its own within a minute and appears in the console under its own name rather than in the adoption queue, with nothing changed on the frame itself. `docker compose ... down` followed by `docker volume ls` leaves `framelink-data` exactly where it was, and the folder the database was imported from is still sitting there untouched, so going back to `dotnet run` is always one minute away.
