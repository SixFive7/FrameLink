# The reconcile DAG

<!--
  GENERATED FILE — do not edit by hand. Every node, edge, position and count below is
  read out of the catalog the agent actually runs.

  Regenerate:   dotnet run --project tools/FrameLink.Diagram -- write
  Check only:   dotnet run --project tools/FrameLink.Diagram -- check

  The suite re-renders this file on every run and fails if the committed copy differs,
  so a stale diagram is a red test rather than a picture people quietly stop trusting.
-->

§2.2's "explicit lightweight DAG", drawn from itself. `tools/FrameLink.Diagram` builds
the real catalog — the same `DeviceCatalog.Build` an agent builds at start-up — sorts it
through the same `ResourceGraph`, and renders this file from the result. Nothing here is
typed by hand, so nothing here can be out of date without a test going red.

The catalog holds **86 resources** in 25 areas, joined by 83 dependency edges.

Two hand-written companions sit beside this one and are not generated:
[every source of non-determinism, classified](reconcile-determinism.md), which says what can
and cannot make two runs differ; and [reconcile ordering and every bound in the
agent](reconcile-ordering-and-timeouts.md), which inventories every number the agent waits on.

---

## 1. How to read this

**An arrow means "has to be `InSync` first".** `A --> B` reads *A before B*: while A is
anything other than `InSync` this pass, B is recorded `Blocked(A)` and is neither observed
nor acted on, so it spends no attempt and takes no reboot. That is the only thing an edge
does, and it is the whole reason the graph exists — `Blocked(dependency)` is a derived
fact rather than a claim somebody maintained by hand.

**The walk is one sequential `foreach` over the order in §3.** No parallelism, no
re-sorting, no arrival-order tie-break: the sort runs once at construction, ties break on
declaration index, and the result is a `List`. Same build, same hardware, same route
through the graph, every pass and every boot.

**The catalog file *is* the execution order, verbatim.** The topological sort returned
the declaration order unchanged — on this catalog it is an identity function with a
validator attached. `AgentResourceGraphTests` asserts exactly that, so the day an edge
reorders something this sentence changes and a test goes red on the same commit.

**Areas are the resource id's first dot-separated segment** — `pkg`, `audio`, `kiosk`,
`unit`. A mechanical rule rather than an editorial one, because an editorial one is a
second thing to maintain and would be the part of a generated document that still drifts.
It has one real cost, stated rather than hidden: the session and kiosk stack is one
subject spread across `session`, `labwc`, `unit`, `portal` and `camera`, because that is
how its ids are spelled.

**Three views, because one picture of 86 nodes is a picture nobody opens twice.** §2 is the
area map — small enough to take in at a glance, and where the gates are. §3 is the
numbered running order, which is the plainest statement of what happens when. §4 is one
small diagram per area that waits on something. §5 is the whole graph in one picture, and
it is dense; it is last because it is the least useful of the three, not the most.

---

## 2. The area map

Areas, and which areas they wait on. An edge label is how many resource-level
dependencies it stands for; self-edges are left out because an area waiting on itself is
not a fact about the shape. An area with no arrow at either end holds only resources that
wait on nothing and that nothing waits on.

```mermaid
flowchart TD
  a_agent["agent<br/>4 resources"]
  a_boot["boot<br/>6 resources"]
  a_journal["journal<br/>1 resource"]
  a_unit["unit<br/>11 resources"]
  a_pkg["pkg<br/>15 resources"]
  a_system["system<br/>2 resources"]
  a_swap["swap<br/>2 resources"]
  a_user["user<br/>1 resource"]
  a_mount["mount<br/>1 resource"]
  a_apt["apt<br/>3 resources"]
  a_identity["identity<br/>1 resource"]
  a_audio["audio<br/>9 resources"]
  a_cpu["cpu<br/>1 resource"]
  a_session["session<br/>1 resource"]
  a_labwc["labwc<br/>3 resources"]
  a_display["display<br/>1 resource"]
  a_app["app<br/>6 resources"]
  a_wireplumber["wireplumber<br/>1 resource"]
  a_portal["portal<br/>2 resources"]
  a_camera["camera<br/>1 resource"]
  a_kiosk["kiosk<br/>9 resources"]
  a_tool["tool<br/>1 resource"]
  a_firmware["firmware<br/>2 resources"]
  a_gpio["gpio<br/>1 resource"]
  a_eeprom["eeprom<br/>1 resource"]
  a_agent -->|2| a_system
  a_pkg -->|2| a_apt
  a_agent --> a_identity
  a_unit --> a_cpu
  a_pkg --> a_session
  a_boot --> a_session
  a_pkg -->|3| a_labwc
  a_labwc -->|2| a_display
  a_session --> a_display
  a_pkg -->|6| a_unit
  a_boot -->|3| a_unit
  a_pkg --> a_wireplumber
  a_boot --> a_wireplumber
  a_pkg -->|2| a_portal
  a_boot --> a_portal
  a_unit --> a_portal
  a_unit --> a_camera
  a_wireplumber --> a_camera
  a_agent -->|3| a_kiosk
  a_agent -->|3| a_app
  a_kiosk --> a_app
  a_tool --> a_firmware
  a_tool --> a_audio
  a_pkg --> a_audio
  a_boot --> a_audio
  a_user --> a_gpio
  a_agent --> a_boot
```

---

## 3. The execution order

Every resource, in the order one pass visits it. A pass changes at most one of them and
then reboots and verifies, so this is the order a bare frame converges in rather than a
list of things that happen at once. Dependencies are shown by position, sorted ascending,
which makes the property worth checking by eye visible: **every number after "waits for"
is smaller than the one it sits beside.**

1. `agent.version`
2. `boot.config.dtoverlay-waveshare-panel`
3. `boot.cmdline.fbcon-rotate` — waits for #2 `boot.config.dtoverlay-waveshare-panel`
4. `agent.keypair`
5. `journal.storage-persistent`
6. `unit.fl-agent.content`
7. `unit.fl-agent.enabled` — waits for #6 `unit.fl-agent.content`
8. `unit.fl-agent.running-matches-content` — waits for #6 `unit.fl-agent.content`, #7 `unit.fl-agent.enabled`
9. `agent.adoption`
10. `agent.device-name` — waits for #9 `agent.adoption`
11. `pkg.labwc`
12. `pkg.chromium`
13. `pkg.wireplumber`
14. `pkg.pipewire-alsa`
15. `pkg.wlr-randr`
16. `pkg.xdg-desktop-portal`
17. `pkg.xdg-desktop-portal-gtk`
18. `pkg.gstreamer1.0-tools`
19. `pkg.gstreamer1.0-plugins-base`
20. `pkg.gstreamer1.0-libcamera`
21. `pkg.gstreamer1.0-pipewire`
22. `pkg.libspa-0.2-libcamera.absent`
23. `pkg.dfu-util`
24. `pkg.grim`
25. `pkg.unattended-upgrades`
26. `system.timezone` — waits for #9 `agent.adoption`
27. `system.locale` — waits for #9 `agent.adoption`
28. `swap.zram-active`
29. `swap.no-file-backed` — waits for #28 `swap.zram-active`
30. `user.framelink.supplementary-groups`
31. `boot.autologin.getty-tty1`
32. `mount.tmp.tmpfs`
33. `apt.auto-upgrades-enabled` — waits for #25 `pkg.unattended-upgrades`
34. `apt.unattended-upgrades.allowed-origins` — waits for #25 `pkg.unattended-upgrades`
35. `apt.daily-timers.enabled-and-active`
36. `identity.hostname` — waits for #9 `agent.adoption`
37. `audio.modprobe.snd-usb-audio-index`
38. `unit.cpu-performance.content`
39. `unit.cpu-performance.enabled` — waits for #38 `unit.cpu-performance.content`
40. `cpu.governor.performance` — waits for #39 `unit.cpu-performance.enabled`
41. `session.bash-profile-exec-labwc` — waits for #11 `pkg.labwc`, #31 `boot.autologin.getty-tty1`
42. `labwc.autostart.content` — waits for #11 `pkg.labwc`, #15 `pkg.wlr-randr`
43. `labwc.autostart.executable` — waits for #42 `labwc.autostart.content`
44. `labwc.rc-xml.touch-map` — waits for #11 `pkg.labwc`
45. `display.dsi2-transform` — waits for #41 `session.bash-profile-exec-labwc`, #42 `labwc.autostart.content`, #43 `labwc.autostart.executable`
46. `unit.xdg-desktop-portal.dropin-desktop` — waits for #16 `pkg.xdg-desktop-portal`, #31 `boot.autologin.getty-tty1`
47. `app.http.local-origin`
48. `unit.chromium-kiosk.content` — waits for #12 `pkg.chromium`, #31 `boot.autologin.getty-tty1`
49. `unit.chromium-kiosk.enabled` — waits for #48 `unit.chromium-kiosk.content`
50. `unit.chromium-kiosk.running-matches-content` — waits for #48 `unit.chromium-kiosk.content`, #49 `unit.chromium-kiosk.enabled`
51. `wireplumber.conf.camera-monitors-disabled` — waits for #13 `pkg.wireplumber`, #31 `boot.autologin.getty-tty1`
52. `unit.framelink-camera.content` — waits for #18 `pkg.gstreamer1.0-tools`, #19 `pkg.gstreamer1.0-plugins-base`, #20 `pkg.gstreamer1.0-libcamera`, #21 `pkg.gstreamer1.0-pipewire`, #31 `boot.autologin.getty-tty1`
53. `unit.framelink-camera.enabled` — waits for #52 `unit.framelink-camera.content`
54. `portal.permission-store.camera` — waits for #17 `pkg.xdg-desktop-portal-gtk`, #31 `boot.autologin.getty-tty1`
55. `portal.camera-interface-published` — waits for #17 `pkg.xdg-desktop-portal-gtk`, #46 `unit.xdg-desktop-portal.dropin-desktop`
56. `camera.pipewire-node.framelink-cam` — waits for #51 `wireplumber.conf.camera-monitors-disabled`, #53 `unit.framelink-camera.enabled`
57. `kiosk.binary.pinned-release`
58. `kiosk.offline-cache.dir` — waits for #57 `kiosk.binary.pinned-release`
59. `kiosk.config.immich-url` — waits for #9 `agent.adoption`, #57 `kiosk.binary.pinned-release`
60. `kiosk.config.immich-api-key` — waits for #9 `agent.adoption`, #57 `kiosk.binary.pinned-release`
61. `kiosk.config.albums` — waits for #9 `agent.adoption`, #57 `kiosk.binary.pinned-release`
62. `kiosk.config.offline-mode-enabled` — waits for #57 `kiosk.binary.pinned-release`
63. `kiosk.config.offline-asset-count` — waits for #62 `kiosk.config.offline-mode-enabled`
64. `kiosk.listen-address` — waits for #57 `kiosk.binary.pinned-release`
65. `kiosk.process.supervised` — waits for #59 `kiosk.config.immich-url`, #60 `kiosk.config.immich-api-key`, #64 `kiosk.listen-address`
66. `app.config.identity` — waits for #9 `agent.adoption`
67. `app.config.room` — waits for #9 `agent.adoption`
68. `app.config.livekit-url` — waits for #9 `agent.adoption`
69. `app.config.livekit-token` — waits for #66 `app.config.identity`, #67 `app.config.room`, #68 `app.config.livekit-url`
70. `app.config.immich-kiosk-url` — waits for #64 `kiosk.listen-address`
71. `tool.xvf-host.installed`
72. `firmware.xvf3800.image`
73. `firmware.xvf3800.recognised` — waits for #71 `tool.xvf-host.installed`
74. `audio.xvf3800.gpo-x0d31-amp-enable` — waits for #71 `tool.xvf-host.installed`
75. `audio.mixer.pcm0-playback-switch` — waits for #37 `audio.modprobe.snd-usb-audio-index`
76. `audio.mixer.pcm1-playback-switch` — waits for #37 `audio.modprobe.snd-usb-audio-index`
77. `audio.wireplumber.playback-volume` — waits for #13 `pkg.wireplumber`, #31 `boot.autologin.getty-tty1`
78. `audio.mixer.pcm0-playback-volume` — waits for #37 `audio.modprobe.snd-usb-audio-index`, #75 `audio.mixer.pcm0-playback-switch`, #77 `audio.wireplumber.playback-volume`
79. `audio.mixer.pcm1-playback-volume` — waits for #37 `audio.modprobe.snd-usb-audio-index`, #76 `audio.mixer.pcm1-playback-switch`
80. `audio.mixer.headset-capture-volume` — waits for #37 `audio.modprobe.snd-usb-audio-index`
81. `audio.alsa.stored-state` — waits for #75 `audio.mixer.pcm0-playback-switch`, #76 `audio.mixer.pcm1-playback-switch`, #77 `audio.wireplumber.playback-volume`, #78 `audio.mixer.pcm0-playback-volume`, #79 `audio.mixer.pcm1-playback-volume`, #80 `audio.mixer.headset-capture-volume`
82. `gpio.button.line` — waits for #30 `user.framelink.supplementary-groups`
83. `boot.config.camera-auto-detect`
84. `boot.config.dtoverlay-vc4-kms-v3d-noaudio`
85. `boot.cmdline.wifi-regdom` — waits for #9 `agent.adoption`
86. `eeprom.config`

---

## 4. Area detail

One diagram per area that waits on something, showing that area's own resources and
whatever they wait on. Nodes carry their position from §3. A **rounded** node belongs to
another area and is drawn here only because something in this one names it.

### `agent` — 4 resources, walked between #1 and #10

```mermaid
flowchart LR
  r1["1 · agent.version"]
  r4["4 · agent.keypair"]
  r9["9 · agent.adoption"]
  r10["10 · agent.device-name"]
  r9 --> r10
```

### `boot` — 6 resources, walked between #2 and #85

```mermaid
flowchart LR
  r2["2 · boot.config.dtoverlay-waveshare-panel"]
  r3["3 · boot.cmdline.fbcon-rotate"]
  r31["31 · boot.autologin.getty-tty1"]
  r83["83 · boot.config.camera-auto-detect"]
  r84["84 · boot.config.dtoverlay-vc4-kms-v3d-noaudio"]
  r85["85 · boot.cmdline.wifi-regdom"]
  r9(["9 · agent.adoption"])
  r2 --> r3
  r9 --> r85
```

### `unit` — 11 resources, walked between #6 and #53

```mermaid
flowchart LR
  r6["6 · unit.fl-agent.content"]
  r7["7 · unit.fl-agent.enabled"]
  r8["8 · unit.fl-agent.running-matches-content"]
  r38["38 · unit.cpu-performance.content"]
  r39["39 · unit.cpu-performance.enabled"]
  r46["46 · unit.xdg-desktop-portal.dropin-desktop"]
  r48["48 · unit.chromium-kiosk.content"]
  r49["49 · unit.chromium-kiosk.enabled"]
  r50["50 · unit.chromium-kiosk.running-matches-content"]
  r52["52 · unit.framelink-camera.content"]
  r53["53 · unit.framelink-camera.enabled"]
  r12(["12 · pkg.chromium"])
  r16(["16 · pkg.xdg-desktop-portal"])
  r18(["18 · pkg.gstreamer1.0-tools"])
  r19(["19 · pkg.gstreamer1.0-plugins-base"])
  r20(["20 · pkg.gstreamer1.0-libcamera"])
  r21(["21 · pkg.gstreamer1.0-pipewire"])
  r31(["31 · boot.autologin.getty-tty1"])
  r6 --> r7
  r6 --> r8
  r7 --> r8
  r38 --> r39
  r16 --> r46
  r31 --> r46
  r12 --> r48
  r31 --> r48
  r48 --> r49
  r48 --> r50
  r49 --> r50
  r18 --> r52
  r19 --> r52
  r20 --> r52
  r21 --> r52
  r31 --> r52
  r52 --> r53
```

### `system` — 2 resources, walked between #26 and #27

```mermaid
flowchart LR
  r26["26 · system.timezone"]
  r27["27 · system.locale"]
  r9(["9 · agent.adoption"])
  r9 --> r26
  r9 --> r27
```

### `swap` — 2 resources, walked between #28 and #29

```mermaid
flowchart LR
  r28["28 · swap.zram-active"]
  r29["29 · swap.no-file-backed"]
  r28 --> r29
```

### `apt` — 3 resources, walked between #33 and #35

```mermaid
flowchart LR
  r33["33 · apt.auto-upgrades-enabled"]
  r34["34 · apt.unattended-upgrades.allowed-origins"]
  r35["35 · apt.daily-timers.enabled-and-active"]
  r25(["25 · pkg.unattended-upgrades"])
  r25 --> r33
  r25 --> r34
```

### `identity` — 1 resource, walked at #36

```mermaid
flowchart LR
  r36["36 · identity.hostname"]
  r9(["9 · agent.adoption"])
  r9 --> r36
```

### `audio` — 9 resources, walked between #37 and #81

```mermaid
flowchart LR
  r37["37 · audio.modprobe.snd-usb-audio-index"]
  r74["74 · audio.xvf3800.gpo-x0d31-amp-enable"]
  r75["75 · audio.mixer.pcm0-playback-switch"]
  r76["76 · audio.mixer.pcm1-playback-switch"]
  r77["77 · audio.wireplumber.playback-volume"]
  r78["78 · audio.mixer.pcm0-playback-volume"]
  r79["79 · audio.mixer.pcm1-playback-volume"]
  r80["80 · audio.mixer.headset-capture-volume"]
  r81["81 · audio.alsa.stored-state"]
  r13(["13 · pkg.wireplumber"])
  r31(["31 · boot.autologin.getty-tty1"])
  r71(["71 · tool.xvf-host.installed"])
  r71 --> r74
  r37 --> r75
  r37 --> r76
  r13 --> r77
  r31 --> r77
  r37 --> r78
  r75 --> r78
  r77 --> r78
  r37 --> r79
  r76 --> r79
  r37 --> r80
  r78 --> r81
  r79 --> r81
  r80 --> r81
  r75 --> r81
  r76 --> r81
  r77 --> r81
```

### `cpu` — 1 resource, walked at #40

```mermaid
flowchart LR
  r40["40 · cpu.governor.performance"]
  r39(["39 · unit.cpu-performance.enabled"])
  r39 --> r40
```

### `session` — 1 resource, walked at #41

```mermaid
flowchart LR
  r41["41 · session.bash-profile-exec-labwc"]
  r11(["11 · pkg.labwc"])
  r31(["31 · boot.autologin.getty-tty1"])
  r11 --> r41
  r31 --> r41
```

### `labwc` — 3 resources, walked between #42 and #44

```mermaid
flowchart LR
  r42["42 · labwc.autostart.content"]
  r43["43 · labwc.autostart.executable"]
  r44["44 · labwc.rc-xml.touch-map"]
  r11(["11 · pkg.labwc"])
  r15(["15 · pkg.wlr-randr"])
  r11 --> r42
  r15 --> r42
  r42 --> r43
  r11 --> r44
```

### `display` — 1 resource, walked at #45

```mermaid
flowchart LR
  r45["45 · display.dsi2-transform"]
  r41(["41 · session.bash-profile-exec-labwc"])
  r42(["42 · labwc.autostart.content"])
  r43(["43 · labwc.autostart.executable"])
  r42 --> r45
  r43 --> r45
  r41 --> r45
```

### `app` — 6 resources, walked between #47 and #70

```mermaid
flowchart LR
  r47["47 · app.http.local-origin"]
  r66["66 · app.config.identity"]
  r67["67 · app.config.room"]
  r68["68 · app.config.livekit-url"]
  r69["69 · app.config.livekit-token"]
  r70["70 · app.config.immich-kiosk-url"]
  r9(["9 · agent.adoption"])
  r64(["64 · kiosk.listen-address"])
  r9 --> r66
  r9 --> r67
  r9 --> r68
  r66 --> r69
  r67 --> r69
  r68 --> r69
  r64 --> r70
```

### `wireplumber` — 1 resource, walked at #51

```mermaid
flowchart LR
  r51["51 · wireplumber.conf.camera-monitors-disabled"]
  r13(["13 · pkg.wireplumber"])
  r31(["31 · boot.autologin.getty-tty1"])
  r13 --> r51
  r31 --> r51
```

### `portal` — 2 resources, walked between #54 and #55

```mermaid
flowchart LR
  r54["54 · portal.permission-store.camera"]
  r55["55 · portal.camera-interface-published"]
  r17(["17 · pkg.xdg-desktop-portal-gtk"])
  r31(["31 · boot.autologin.getty-tty1"])
  r46(["46 · unit.xdg-desktop-portal.dropin-desktop"])
  r17 --> r54
  r31 --> r54
  r46 --> r55
  r17 --> r55
```

### `camera` — 1 resource, walked at #56

```mermaid
flowchart LR
  r56["56 · camera.pipewire-node.framelink-cam"]
  r51(["51 · wireplumber.conf.camera-monitors-disabled"])
  r53(["53 · unit.framelink-camera.enabled"])
  r53 --> r56
  r51 --> r56
```

### `kiosk` — 9 resources, walked between #57 and #65

```mermaid
flowchart LR
  r57["57 · kiosk.binary.pinned-release"]
  r58["58 · kiosk.offline-cache.dir"]
  r59["59 · kiosk.config.immich-url"]
  r60["60 · kiosk.config.immich-api-key"]
  r61["61 · kiosk.config.albums"]
  r62["62 · kiosk.config.offline-mode-enabled"]
  r63["63 · kiosk.config.offline-asset-count"]
  r64["64 · kiosk.listen-address"]
  r65["65 · kiosk.process.supervised"]
  r9(["9 · agent.adoption"])
  r57 --> r58
  r57 --> r59
  r9 --> r59
  r57 --> r60
  r9 --> r60
  r57 --> r61
  r9 --> r61
  r57 --> r62
  r62 --> r63
  r57 --> r64
  r64 --> r65
  r59 --> r65
  r60 --> r65
```

### `firmware` — 2 resources, walked between #72 and #73

```mermaid
flowchart LR
  r72["72 · firmware.xvf3800.image"]
  r73["73 · firmware.xvf3800.recognised"]
  r71(["71 · tool.xvf-host.installed"])
  r71 --> r73
```

### `gpio` — 1 resource, walked at #82

```mermaid
flowchart LR
  r82["82 · gpio.button.line"]
  r30(["30 · user.framelink.supplementary-groups"])
  r30 --> r82
```

**No diagram for 6 areas:** `journal`, `pkg`, `user`, `mount`, `tool`, `eeprom`. Nothing in them declares a
dependency, so there is no picture to draw — every resource in them is reached at its
position in §3 with nothing gating it.

---

## 5. The whole graph in one picture

**This one is dense, and that is the honest description of it.** 72 of the catalog's
86 resources touch an edge; the other 14 are listed underneath rather than drawn, because a
node with no arrows is a row in §3 and not a shape. Boxes group by area. Use §2 and §4
first — this is here for the times somebody needs the whole thing at once.

```mermaid
flowchart TD
  subgraph a_agent["agent"]
    r9["9 · agent.adoption"]
    r10["10 · agent.device-name"]
  end
  subgraph a_boot["boot"]
    r2["2 · boot.config.dtoverlay-waveshare-panel"]
    r3["3 · boot.cmdline.fbcon-rotate"]
    r31["31 · boot.autologin.getty-tty1"]
    r85["85 · boot.cmdline.wifi-regdom"]
  end
  subgraph a_unit["unit"]
    r6["6 · unit.fl-agent.content"]
    r7["7 · unit.fl-agent.enabled"]
    r8["8 · unit.fl-agent.running-matches-content"]
    r38["38 · unit.cpu-performance.content"]
    r39["39 · unit.cpu-performance.enabled"]
    r46["46 · unit.xdg-desktop-portal.dropin-desktop"]
    r48["48 · unit.chromium-kiosk.content"]
    r49["49 · unit.chromium-kiosk.enabled"]
    r50["50 · unit.chromium-kiosk.running-matches-content"]
    r52["52 · unit.framelink-camera.content"]
    r53["53 · unit.framelink-camera.enabled"]
  end
  subgraph a_pkg["pkg"]
    r11["11 · pkg.labwc"]
    r12["12 · pkg.chromium"]
    r13["13 · pkg.wireplumber"]
    r15["15 · pkg.wlr-randr"]
    r16["16 · pkg.xdg-desktop-portal"]
    r17["17 · pkg.xdg-desktop-portal-gtk"]
    r18["18 · pkg.gstreamer1.0-tools"]
    r19["19 · pkg.gstreamer1.0-plugins-base"]
    r20["20 · pkg.gstreamer1.0-libcamera"]
    r21["21 · pkg.gstreamer1.0-pipewire"]
    r25["25 · pkg.unattended-upgrades"]
  end
  subgraph a_system["system"]
    r26["26 · system.timezone"]
    r27["27 · system.locale"]
  end
  subgraph a_swap["swap"]
    r28["28 · swap.zram-active"]
    r29["29 · swap.no-file-backed"]
  end
  subgraph a_user["user"]
    r30["30 · user.framelink.supplementary-groups"]
  end
  subgraph a_apt["apt"]
    r33["33 · apt.auto-upgrades-enabled"]
    r34["34 · apt.unattended-upgrades.allowed-origins"]
  end
  subgraph a_identity["identity"]
    r36["36 · identity.hostname"]
  end
  subgraph a_audio["audio"]
    r37["37 · audio.modprobe.snd-usb-audio-index"]
    r74["74 · audio.xvf3800.gpo-x0d31-amp-enable"]
    r75["75 · audio.mixer.pcm0-playback-switch"]
    r76["76 · audio.mixer.pcm1-playback-switch"]
    r77["77 · audio.wireplumber.playback-volume"]
    r78["78 · audio.mixer.pcm0-playback-volume"]
    r79["79 · audio.mixer.pcm1-playback-volume"]
    r80["80 · audio.mixer.headset-capture-volume"]
    r81["81 · audio.alsa.stored-state"]
  end
  subgraph a_cpu["cpu"]
    r40["40 · cpu.governor.performance"]
  end
  subgraph a_session["session"]
    r41["41 · session.bash-profile-exec-labwc"]
  end
  subgraph a_labwc["labwc"]
    r42["42 · labwc.autostart.content"]
    r43["43 · labwc.autostart.executable"]
    r44["44 · labwc.rc-xml.touch-map"]
  end
  subgraph a_display["display"]
    r45["45 · display.dsi2-transform"]
  end
  subgraph a_app["app"]
    r66["66 · app.config.identity"]
    r67["67 · app.config.room"]
    r68["68 · app.config.livekit-url"]
    r69["69 · app.config.livekit-token"]
    r70["70 · app.config.immich-kiosk-url"]
  end
  subgraph a_wireplumber["wireplumber"]
    r51["51 · wireplumber.conf.camera-monitors-disabled"]
  end
  subgraph a_portal["portal"]
    r54["54 · portal.permission-store.camera"]
    r55["55 · portal.camera-interface-published"]
  end
  subgraph a_camera["camera"]
    r56["56 · camera.pipewire-node.framelink-cam"]
  end
  subgraph a_kiosk["kiosk"]
    r57["57 · kiosk.binary.pinned-release"]
    r58["58 · kiosk.offline-cache.dir"]
    r59["59 · kiosk.config.immich-url"]
    r60["60 · kiosk.config.immich-api-key"]
    r61["61 · kiosk.config.albums"]
    r62["62 · kiosk.config.offline-mode-enabled"]
    r63["63 · kiosk.config.offline-asset-count"]
    r64["64 · kiosk.listen-address"]
    r65["65 · kiosk.process.supervised"]
  end
  subgraph a_tool["tool"]
    r71["71 · tool.xvf-host.installed"]
  end
  subgraph a_firmware["firmware"]
    r73["73 · firmware.xvf3800.recognised"]
  end
  subgraph a_gpio["gpio"]
    r82["82 · gpio.button.line"]
  end
  r2 --> r3
  r6 --> r7
  r6 --> r8
  r7 --> r8
  r9 --> r10
  r9 --> r26
  r9 --> r27
  r28 --> r29
  r25 --> r33
  r25 --> r34
  r9 --> r36
  r38 --> r39
  r39 --> r40
  r11 --> r41
  r31 --> r41
  r11 --> r42
  r15 --> r42
  r42 --> r43
  r11 --> r44
  r42 --> r45
  r43 --> r45
  r41 --> r45
  r16 --> r46
  r31 --> r46
  r12 --> r48
  r31 --> r48
  r48 --> r49
  r48 --> r50
  r49 --> r50
  r13 --> r51
  r31 --> r51
  r18 --> r52
  r19 --> r52
  r20 --> r52
  r21 --> r52
  r31 --> r52
  r52 --> r53
  r17 --> r54
  r31 --> r54
  r46 --> r55
  r17 --> r55
  r53 --> r56
  r51 --> r56
  r57 --> r58
  r57 --> r59
  r9 --> r59
  r57 --> r60
  r9 --> r60
  r57 --> r61
  r9 --> r61
  r57 --> r62
  r62 --> r63
  r57 --> r64
  r64 --> r65
  r59 --> r65
  r60 --> r65
  r9 --> r66
  r9 --> r67
  r9 --> r68
  r66 --> r69
  r67 --> r69
  r68 --> r69
  r64 --> r70
  r71 --> r73
  r71 --> r74
  r37 --> r75
  r37 --> r76
  r13 --> r77
  r31 --> r77
  r37 --> r78
  r75 --> r78
  r77 --> r78
  r37 --> r79
  r76 --> r79
  r37 --> r80
  r78 --> r81
  r79 --> r81
  r80 --> r81
  r75 --> r81
  r76 --> r81
  r77 --> r81
  r30 --> r82
  r9 --> r85
```

**Not drawn — 14 resources with no edge in either direction.** They wait on nothing and
nothing waits on them, so their position in §3 is the whole of what there is to say:

- #1 `agent.version`
- #4 `agent.keypair`
- #5 `journal.storage-persistent`
- #14 `pkg.pipewire-alsa`
- #22 `pkg.libspa-0.2-libcamera.absent`
- #23 `pkg.dfu-util`
- #24 `pkg.grim`
- #32 `mount.tmp.tmpfs`
- #35 `apt.daily-timers.enabled-and-active`
- #47 `app.http.local-origin`
- #72 `firmware.xvf3800.image`
- #83 `boot.config.camera-auto-detect`
- #84 `boot.config.dtoverlay-vc4-kms-v3d-noaudio`
- #86 `eeprom.config`

---

## 6. What the shape says

| | |
|---|---|
| Resources in the catalog | **86** |
| Dependency edges | **83** |
| Resources that declare at least one dependency | **51** |
| Resources something else waits on | **45** |
| Resources with no edge in either direction | **14** |
| Areas | **25** |
| Longest chain, in resources | **4** |

**The graph is wide, not deep.** The longest chain in it is 4 resources long:

#11 `pkg.labwc` → #42 `labwc.autostart.content` → #43 `labwc.autostart.executable` → #45 `display.dsi2-transform`

So no resource in this catalog is more than 3 hops from something that gates nothing.
Depth is not what the DAG is for here; refusing to attempt doomed work is.

**What most things wait on.** *Waiting on it directly* counts the resources that name it
in `dependsOn`; *blocked behind it* counts everything that can never be attempted while it
is not `InSync`, which is the number that matters when one has escalated and the frame has
stopped acting.

| Position | Resource | Waiting on it directly | Blocked behind it |
|---|---|---|---|
| 31 | `boot.autologin.getty-tty1` | 7 | 15 |
| 9 | `agent.adoption` | 11 | 13 |
| 57 | `kiosk.binary.pinned-release` | 6 | 9 |
| 37 | `audio.modprobe.snd-usb-audio-index` | 5 | 6 |
| 11 | `pkg.labwc` | 3 | 5 |
| 13 | `pkg.wireplumber` | 2 | 5 |
| 12 | `pkg.chromium` | 1 | 3 |
| 15 | `pkg.wlr-randr` | 1 | 3 |
| 18 | `pkg.gstreamer1.0-tools` | 1 | 3 |
| 19 | `pkg.gstreamer1.0-plugins-base` | 1 | 3 |

