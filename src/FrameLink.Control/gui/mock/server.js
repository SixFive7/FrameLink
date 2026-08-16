#!/usr/bin/env node
/**
 * A stand-in for `fl-control`, for developing and verifying the GUI.
 *
 * Two jobs, and it is deliberately one file with no dependencies so it can do both without
 * anything being installed:
 *
 *  1. **`npm run mock`** — an in-memory operator API on :5199 that `vite dev` proxies to, so
 *     the whole GUI can be driven without a .NET build, a SQLite file or a real frame.
 *  2. **`npm run verify`** — the same API *plus* a plain static-file host serving the built
 *     `../wwwroot` with an SPA fallback, which is the acceptance test the build has to pass:
 *     the committed output must load with no dev server, no bundler and no framework runtime
 *     on the server side.
 *
 * The API is a faithful copy of the routes in `Endpoints/OperatorEndpoints.cs` — same paths,
 * same JSON casing, same status codes, and the same three refusals that matter to the GUI:
 * 401 `unauthorized`, 503 `not-configured`, 409 `not-adopted`. Where this file and the C#
 * disagree, the C# is right and this file is a bug.
 *
 * It is **not** a second implementation of the Fleet Manager. There is no `/agent` route, no
 * WebSocket, no keypair verification and no persistence — a device cannot talk to this.
 *
 * Usage:
 *   node mock/server.js                     API only, :5199
 *   node mock/server.js --serve             API + static ../wwwroot, :5199
 *   node mock/server.js --unconfigured      pretends FRAMELINK_OPERATOR_PASSWORD is unset
 *   node mock/server.js --empty             no seeded devices
 *   node mock/server.js --port 8080
 */

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const option = (name, fallback) => {
	const at = args.indexOf(`--${name}`);
	return at === -1 ? fallback : args[at + 1];
};

const PORT = Number(option('port', 5199));
const CONFIGURED = !flag('unconfigured');
const SERVE_STATIC = flag('serve');
const PASSWORD = option('password', 'correct-horse-battery-staple-frame');
const WWWROOT = resolve(fileURLToPath(new URL('../../wwwroot', import.meta.url)));

// ── the fake fleet ────────────────────────────────────────────────────────────────────────

const COOKIE = 'fl_operator';
const sessions = new Set();

const iso = (minutesAgo) => new Date(Date.now() - minutesAgo * 60_000).toISOString();

/** Seeded to exercise every branch of `presence.ts` and every device state. */
const devices = flag('empty')
	? []
	: [
			{
				deviceId: 'K4W2-9TRB-8ZQ1-3MHF',
				state: 'pending',
				online: false,
				hardwareSerial: '100000004e3f21bc',
				agentVersion: '0.4.2+9f1c3ae',
				agentStatus: 'Waiting to be adopted',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(4),
				lastSeenUtc: iso(1),
				stateChangedUtc: iso(4),
				lastRemoteAddress: '192.168.1.51'
			},
			{
				deviceId: 'PQ7X-0DGE-5NVC-2JKT',
				state: 'pending',
				online: false,
				hardwareSerial: '10000000a71d90f4',
				agentVersion: '0.4.2+9f1c3ae',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(19),
				lastSeenUtc: iso(2),
				stateChangedUtc: iso(19),
				lastRemoteAddress: '192.168.1.52'
			},
			{
				deviceId: 'A1B2-C3D4-E5F6-G7H8',
				state: 'adopted',
				online: true,
				name: "Oma's living room",
				hardwareSerial: '10000000bb42c701',
				agentVersion: '0.4.2+9f1c3ae',
				agentStatus: 'InSync',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(60 * 24 * 31),
				lastSeenUtc: iso(0),
				stateChangedUtc: iso(60 * 24 * 31),
				lastRemoteAddress: '192.168.1.31'
			},
			{
				deviceId: 'M9N8-P7Q6-R5S4-T3V2',
				state: 'adopted',
				online: true,
				name: 'Studeerkamer',
				hardwareSerial: '10000000cd11a3e9',
				agentVersion: '0.4.2+9f1c3ae',
				agentStatus: 'Degraded(audio.volume, expected 75 observed 40, attempt 3)',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(60 * 24 * 12),
				lastSeenUtc: iso(0),
				stateChangedUtc: iso(60 * 24 * 12),
				lastRemoteAddress: '192.168.1.32'
			},
			{
				deviceId: 'W1X2-Y3Z4-0A1B-2C3D',
				state: 'adopted',
				online: false,
				name: 'Keuken',
				hardwareSerial: '10000000ff8e2210',
				agentVersion: '0.3.9+11ab77c',
				agentStatus: 'Update failed: checksum mismatch, 4 attempts',
				protocolVersion: 0,
				protocolCompatible: false,
				firstSeenUtc: iso(60 * 24 * 40),
				lastSeenUtc: iso(60 * 26),
				stateChangedUtc: iso(60 * 24 * 40),
				lastRemoteAddress: '192.168.1.33'
			},
			{
				deviceId: 'HH11-JJ22-KK33-MM44',
				state: 'adopted',
				online: false,
				name: 'Logeerkamer',
				hardwareSerial: '10000000123aa9de',
				agentVersion: '0.4.2+9f1c3ae',
				agentStatus: 'InSync',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(60 * 24 * 9),
				lastSeenUtc: iso(60 * 24 * 4),
				stateChangedUtc: iso(60 * 24 * 9),
				lastRemoteAddress: '192.168.1.34'
			},
			{
				deviceId: 'BB55-CC66-DD77-EE88',
				state: 'adopted',
				online: false,
				name: 'Zolder',
				hardwareSerial: '100000007c4e0b52',
				agentVersion: '0.4.2+9f1c3ae',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(60 * 24 * 6),
				// Adopted after it was last seen: the UniFi reading of `Never enrolled`.
				lastSeenUtc: iso(60 * 24 * 5),
				stateChangedUtc: iso(60 * 24 * 2),
				lastRemoteAddress: '192.168.1.35'
			},
			{
				deviceId: 'ZZ99-XX88-VV77-TT66',
				state: 'blocked',
				online: false,
				hardwareSerial: 'unknown',
				agentVersion: '0.4.2+9f1c3ae',
				protocolVersion: 1,
				protocolCompatible: true,
				firstSeenUtc: iso(60 * 24 * 3),
				lastSeenUtc: iso(60 * 24 * 2),
				stateChangedUtc: iso(60 * 24 * 2),
				lastRemoteAddress: '203.0.113.77'
			}
		];

let revision = 7;
const fleetDefaults = flag('empty')
	? {}
	: {
			'immich.url': 'https://photos.example.org',
			'immich.apiKey': 'imk_9f2b71c0d84e4a1fae37',
			'slideshow.intervalSeconds': '30',
			'call.room': 'huisman',
			'call.autoAnswer': 'true',
			'display.backlightOn': '07:30',
			'display.backlightOff': '22:30',
			'audio.volume': '75',
			'repair.countdownSeconds': '25',
			'locale.timezone': 'Europe/Amsterdam',
			'kiosk.pinnedRelease': 'v0.42.0'
		};

const overrides = flag('empty')
	? {}
	: {
			'A1B2-C3D4-E5F6-G7H8': { 'slideshow.album': 'Oma & Opa', 'audio.volume': '90' },
			'M9N8-P7Q6-R5S4-T3V2': { 'display.backlightOff': '23:45' }
		};

// ── reconciliation ────────────────────────────────────────────────────────────────────────

/**
 * A stalled provision, reproduced from a real one.
 *
 * The first full provision of a real frame reported **37 in sync, 1 escalated, 12 blocked, 32
 * reboots expected**, and every one of the twelve was downstream of the single escalation. That
 * is the shape the reconciliation screen exists to draw, so it is the shape the mock serves.
 *
 * Two details here are load-bearing rather than decorative:
 *
 *  - `inSync + rebootsExpected` is 69, and that is the catalog size. The frame computes
 *    `rebootsExpected` as `catalogSize - inSync` from the same `inSync` it sends
 *    (`ReconcileLoop.PublishReportAsync`), so the identity holds in every report and is the only
 *    stable denominator the wire offers.
 *  - `resources` carries 50 entries, not 69. A mid-pass report carries only what the agent has
 *    walked so far, which is exactly the partial-list case the console has to survive.
 */
const chain = (blocker, names) => names.map((name) => ({
	name,
	status: 'blocked',
	blockedBy: blocker,
	delta: `waiting for '${blocker}'`,
	attempts: 0,
	attemptBudget: 5,
	escalations: 0
}));

const verified = [
	'agent.version', 'agent.keypair', 'agent.adoption', 'device.hostname', 'device.user',
	'boot.config.stock-baseline', 'boot.config.dtoverlay-vc4-kms-v3d-noaudio',
	'boot.config.camera-auto-detect', 'boot.cmdline.wifi-regdom', 'boot.cmdline.fbcon-rotate',
	'boot.autologin.getty-tty1', 'cfg80211.ieee80211_regdom', 'cpu.governor.performance',
	'journal.storage-persistent', 'apt.auto-upgrades-enabled', 'apt.conf.d',
	'apt.unattended-upgrades.allowed-origins', 'pkg.labwc', 'pkg.chromium', 'pkg.wireplumber',
	'pkg.pipewire-alsa', 'pkg.wlr-randr', 'pkg.xdg-desktop-portal', 'pkg.xdg-desktop-portal-gtk',
	'pkg.gstreamer1.0-tools', 'pkg.gstreamer1.0-plugins-base', 'pkg.gstreamer1.0-libcamera',
	'pkg.gstreamer1.0-pipewire', 'pkg.dfu-util', 'pkg.unattended-upgrades',
	'audio.mixer.pcm0-playback-volume', 'audio.mixer.pcm0-playback-switch',
	'audio.mixer.pcm1-playback-volume', 'audio.alsa.stored-state',
	'firmware.rpi-bootloader.version', 'session.bash-profile-exec-labwc', 'labwc.autostart.executable'
].map((name) => ({ name, status: 'in-sync', attempts: 1, attemptBudget: 5, escalations: 0 }));

const stalledResources = [
	...verified,
	{
		name: 'display.dsi2-transform',
		status: 'escalated',
		delta: "expected 'transform=90, 1280x800', observed 'no DSI connector; wlr-randr lists no output'",
		action: 'wlr-randr --output DSI-2 --transform 90',
		attempts: 5,
		attemptBudget: 5,
		escalations: 2
	},
	...chain('display.dsi2-transform', [
		'display.rotation',
		'labwc.autostart.content',
		'kiosk.binary.pinned-release',
		'camera.pipewire-node.framelink-cam'
	]),
	...chain('display.rotation', ['app.config.immich-kiosk-url', 'kiosk.config.immich-url']),
	...chain('kiosk.binary.pinned-release', [
		'kiosk.config.offline-mode-enabled',
		'kiosk.config.offline-asset-count',
		'kiosk.offline-cache.dir'
	]),
	...chain('kiosk.config.offline-mode-enabled', ['kiosk.process.supervised']),
	...chain('camera.pipewire-node.framelink-cam', ['call.identity']),
	// The one that is not a dependency at all: the frame could not *ask*. `Unevaluable` on the
	// agent, `blocked` on `the Fleet Manager` on the wire, and never a fault.
	{
		name: 'app.config.livekit-token',
		status: 'blocked',
		blockedBy: 'the Fleet Manager',
		delta: "expected 'a minted call token', could not be determined: no answer from the Fleet Manager",
		attempts: 0,
		attemptBudget: 5,
		escalations: 0
	}
];

const reports = flag('empty')
	? {}
	: {
			'M9N8-P7Q6-R5S4-T3V2': {
				deviceId: 'M9N8-P7Q6-R5S4-T3V2',
				sequence: 412,
				generatedUtc: iso(1),
				loopState: 'escalated',
				currentResource: 'display.dsi2-transform',
				currentPhase: 'act',
				inSync: 37,
				drifted: 1,
				blocked: 12,
				rebootsExpected: 32,
				resources: stalledResources
			},
			'A1B2-C3D4-E5F6-G7H8': {
				deviceId: 'A1B2-C3D4-E5F6-G7H8',
				sequence: 1904,
				generatedUtc: iso(3),
				loopState: 'converged',
				// A converged report is a whole-catalog census: `resources.length` equals
				// `inSync + rebootsExpected`, which is what tells the console it is complete.
				inSync: stalledResources.length,
				drifted: 0,
				blocked: 0,
				rebootsExpected: 0,
				resources: stalledResources.map((resource) => ({
					name: resource.name,
					status: 'in-sync',
					attempts: 1,
					attemptBudget: 5,
					escalations: 0
				}))
			}
		};

const deviceEvents = flag('empty')
	? {}
	: {
			'M9N8-P7Q6-R5S4-T3V2': [
				{
					deviceId: 'M9N8-P7Q6-R5S4-T3V2',
					kind: 'escalation',
					occurredUtc: iso(2),
					resource: 'display.dsi2-transform',
					summary:
						'The panel transform could not be applied after five attempts. Retry resets the ' +
						'budget; a remote shell is the other option.',
					delta:
						"expected 'transform=90, 1280x800', observed 'no DSI connector; wlr-randr lists no output'",
					attempts: 5
				},
				{
					deviceId: 'M9N8-P7Q6-R5S4-T3V2',
					kind: 'display',
					occurredUtc: iso(14),
					summary:
						'This frame’s own screen cannot show anything — no framebuffer and no connected ' +
						'DRM output — so the Fleet Manager is the only surface left.',
					attempts: 0
				},
				{
					deviceId: 'M9N8-P7Q6-R5S4-T3V2',
					kind: 'drift',
					occurredUtc: iso(31),
					resource: 'display.dsi2-transform',
					summary:
						'The panel is not rotated. Without it the slideshow renders sideways on a portrait ' +
						'panel and every touch lands in the wrong place.',
					delta: "expected 'transform=90, 1280x800', observed 'transform=normal'",
					attempts: 1
				},
				{
					deviceId: 'M9N8-P7Q6-R5S4-T3V2',
					kind: 'boot',
					occurredUtc: iso(46),
					summary: 'Agent started, back across a reboot boundary it asked for.',
					attempts: 0
				}
			],
			'A1B2-C3D4-E5F6-G7H8': [
				{
					deviceId: 'A1B2-C3D4-E5F6-G7H8',
					kind: 'converged',
					occurredUtc: iso(3),
					summary: 'Every resource reached in-sync. The product is running.',
					attempts: 0
				}
			]
		};

// ── plumbing ──────────────────────────────────────────────────────────────────────────────

const MIME = {
	'.html': 'text/html; charset=utf-8',
	'.js': 'text/javascript; charset=utf-8',
	'.css': 'text/css; charset=utf-8',
	'.json': 'application/json; charset=utf-8',
	'.svg': 'image/svg+xml',
	'.woff2': 'font/woff2',
	'.woff': 'font/woff',
	'.png': 'image/png',
	'.jpg': 'image/jpeg',
	'.ico': 'image/x-icon',
	'.txt': 'text/plain; charset=utf-8',
	'.map': 'application/json; charset=utf-8'
};

const json = (res, status, body, headers = {}) => {
	const payload = JSON.stringify(body);
	res.writeHead(status, {
		'content-type': 'application/json; charset=utf-8',
		'content-length': Buffer.byteLength(payload),
		...headers
	});
	res.end(payload);
};

const noContent = (res) => {
	res.writeHead(204);
	res.end();
};

const error = (res, status, code, detail) => json(res, status, { error: code, detail });

const readBody = (req) =>
	new Promise((done) => {
		let raw = '';
		req.on('data', (chunk) => (raw += chunk));
		req.on('end', () => {
			try {
				done(raw ? JSON.parse(raw) : undefined);
			} catch {
				done(undefined);
			}
		});
	});

const authed = (req) => {
	const cookie = req.headers.cookie ?? '';
	const match = cookie.match(new RegExp(`(?:^|;\\s*)${COOKIE}=([^;]+)`));
	return Boolean(match && sessions.has(match[1]));
};

const find = (id) => devices.find((device) => device.deviceId === id);

/** Open `/api/events` responses, so a mutation here nudges the console the way the server does. */
const streams = new Set();

const publish = (deviceId) => {
	for (const stream of streams) stream.write(`event: device\ndata: ${deviceId}\n\n`);
};

/**
 * `AgentHealth.Classify`, in JavaScript. The GUI must never do this itself — that is the whole
 * point of the `health` field — so the mock does what the server does and nothing else.
 */
const WORKING = new Set(['progressing', 'awaitingreboot']);
const BROKEN = new Set(['degraded', 'blocked', 'escalated']);

const classifyHealth = (agentStatus) => {
	const head = (agentStatus ?? '').split('(')[0].trim().toLowerCase();
	if (!head) return 'unknown';
	if (head === 'insync') return 'in-sync';
	if (WORKING.has(head)) return 'working';
	if (BROKEN.has(head)) return 'degraded';
	return 'unknown';
};

const view = (device) => ({ ...device, health: classifyHealth(device.agentStatus) });

// ── the operator API ──────────────────────────────────────────────────────────────────────

async function handleApi(req, res, url) {
	const path = url.pathname;
	const method = req.method ?? 'GET';

	// `OperatorGate`: everything under /api needs a session except GET /api/status and
	// POST /api/session.
	const exempt =
		path === '/api/status' || (path === '/api/session' && method === 'POST');

	if (!exempt && !authed(req)) {
		return error(
			res,
			401,
			CONFIGURED ? 'unauthorized' : 'not-configured',
			CONFIGURED ? 'Sign in with the operator password.' : problemSentence()
		);
	}

	if (path === '/api/status' && method === 'GET') {
		return json(res, 200, {
			configured: CONFIGURED,
			variable: 'FRAMELINK_OPERATOR_PASSWORD',
			...(CONFIGURED
				? {}
				: { problem: problemSentence(), composeExample: COMPOSE_EXAMPLE })
		});
	}

	if (path === '/api/session' && method === 'POST') {
		if (!CONFIGURED) return error(res, 503, 'not-configured', problemSentence());
		const body = await readBody(req);
		if (body?.password !== PASSWORD) {
			return error(res, 401, 'unauthorized', 'That is not the operator password.');
		}
		const token = `tok_${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
		sessions.add(token);
		return json(
			res,
			200,
			{ token, expiresUtc: new Date(Date.now() + 12 * 3600_000).toISOString() },
			{ 'set-cookie': `${COOKIE}=${token}; Path=/; HttpOnly; SameSite=Strict` }
		);
	}

	if (path === '/api/session' && method === 'DELETE') {
		sessions.clear();
		res.writeHead(204, { 'set-cookie': `${COOKIE}=; Path=/; Max-Age=0` });
		return res.end();
	}

	// `OperatorEndpoints.StreamFleetEventsAsync`. Nothing in this mock changes on its own, so
	// the stream only ever carries `ready` and keep-alives — enough for the console to show that
	// it is live, and enough that an EventSource is not left retrying a 404 forever.
	if (path === '/api/events' && method === 'GET') {
		res.writeHead(200, {
			'content-type': 'text/event-stream',
			'cache-control': 'no-cache',
			'x-accel-buffering': 'no'
		});
		res.write('event: ready\ndata: \n\n');
		const beat = setInterval(() => res.write(': keep-alive\n\n'), 25_000);
		streams.add(res);
		req.on('close', () => {
			clearInterval(beat);
			streams.delete(res);
		});
		return undefined;
	}

	if (path === '/api/devices' && method === 'GET') {
		const includeBlocked = url.searchParams.get('includeBlocked') === 'true';
		const rows = devices
			.filter((device) => includeBlocked || device.state !== 'blocked')
			.sort((a, b) => Date.parse(b.lastSeenUtc) - Date.parse(a.lastSeenUtc));
		return json(res, 200, { devices: rows.map(view), includeBlocked });
	}

	const oneDevice = path.match(/^\/api\/devices\/([^/]+)$/);
	if (oneDevice && method === 'GET') {
		const device = find(decodeURIComponent(oneDevice[1]));
		return device ? json(res, 200, view(device)) : notFoundDevice(res, oneDevice[1]);
	}

	const deviceAction = path.match(/^\/api\/devices\/([^/]+)\/(adopt|block|unblock)$/);
	if (deviceAction && method === 'POST') {
		const device = find(decodeURIComponent(deviceAction[1]));
		if (!device) return notFoundDevice(res, deviceAction[1]);

		if (deviceAction[2] === 'adopt') {
			// No body: the optional name rides in the query. And a blocked device is refused —
			// unblocking is what returns it to the queue, and adopting it is a second press.
			if (device.state === 'blocked') {
				return error(
					res,
					409,
					'blocked',
					'This device is blocked. Unblock it first — that returns it to the adoption ' +
						'queue, where it can be adopted deliberately.'
				);
			}
			const name = url.searchParams.get('name')?.trim();
			if (device.state !== 'adopted') device.stateChangedUtc = new Date().toISOString();
			device.state = 'adopted';
			if (name) device.name = name;
		} else if (deviceAction[2] === 'block') {
			device.state = 'blocked';
			device.online = false;
			device.stateChangedUtc = new Date().toISOString();
		} else {
			// Unblocking returns to pending and clears the name and overrides — see
			// `SqliteDeviceStore.ReturnToPendingAsync`.
			device.state = 'pending';
			device.stateChangedUtc = new Date().toISOString();
			delete device.name;
			delete overrides[device.deviceId];
		}
		revision++;
		publish(device.deviceId);
		return json(res, 200, view(device));
	}

	const deviceRoot = path.match(/^\/api\/devices\/([^/]+)$/);
	if (deviceRoot && method === 'DELETE') {
		const id = decodeURIComponent(deviceRoot[1]);
		const at = devices.findIndex((device) => device.deviceId === id);
		if (at === -1) return notFoundDevice(res, id);
		devices.splice(at, 1);
		delete overrides[id];
		publish(id);
		return noContent(res);
	}

	if (path === '/api/settings' && method === 'GET') {
		return json(res, 200, { revision, values: { ...fleetDefaults } });
	}

	const fleetKey = path.match(/^\/api\/settings\/(.+)$/);
	if (fleetKey) {
		const key = decodeURIComponent(fleetKey[1]);
		if (method === 'PUT') {
			const body = await readBody(req);
			if (typeof body?.value !== 'string') {
				return error(res, 400, 'bad-request', 'A value is required.');
			}
			fleetDefaults[key] = body.value;
			revision++;
			return noContent(res);
		}
		if (method === 'DELETE') {
			if (!(key in fleetDefaults)) {
				return error(res, 404, 'no-such-setting', `No setting named '${key}'.`);
			}
			delete fleetDefaults[key];
			revision++;
			return noContent(res);
		}
	}

	// `OperatorEndpoints.GetReconcileAsync`. 200 with an absent `report` for a frame that has
	// never sent one — that is a state the screen renders, not an error, so it is not a 404.
	const deviceReconcile = path.match(/^\/api\/devices\/([^/]+)\/reconcile$/);
	if (deviceReconcile && method === 'GET') {
		const id = decodeURIComponent(deviceReconcile[1]);
		const device = find(id);
		if (!device) return notFoundDevice(res, id);
		return json(res, 200, { deviceId: id, online: device.online, report: reports[id] });
	}

	// `OperatorEndpoints.GetDeviceEventsAsync`. Newest first, `limit` defaulting to 50.
	const deviceEventLog = path.match(/^\/api\/devices\/([^/]+)\/events$/);
	if (deviceEventLog && method === 'GET') {
		const id = decodeURIComponent(deviceEventLog[1]);
		const device = find(id);
		if (!device) return notFoundDevice(res, id);
		const limit = Math.min(1000, Math.max(1, Number(url.searchParams.get('limit')) || 50));
		return json(res, 200, { deviceId: id, events: (deviceEvents[id] ?? []).slice(0, limit) });
	}

	const deviceSettings = path.match(/^\/api\/devices\/([^/]+)\/settings$/);
	if (deviceSettings && method === 'GET') {
		const id = decodeURIComponent(deviceSettings[1]);
		const device = find(id);
		const own = overrides[id] ?? {};
		return json(res, 200, {
			deviceId: id,
			revision,
			fleetDefaults: { ...fleetDefaults },
			overrides: { ...own },
			// Empty unless adopted — the structural half of "a pending device receives nothing".
			effective: device?.state === 'adopted' ? { ...fleetDefaults, ...own } : {}
		});
	}

	const deviceSettingKey = path.match(/^\/api\/devices\/([^/]+)\/settings\/(.+)$/);
	if (deviceSettingKey) {
		const id = decodeURIComponent(deviceSettingKey[1]);
		const key = decodeURIComponent(deviceSettingKey[2]);
		const device = find(id);

		if (method === 'PUT') {
			const body = await readBody(req);
			if (typeof body?.value !== 'string') {
				return error(res, 400, 'bad-request', 'A value is required.');
			}
			if (device?.state !== 'adopted') {
				return error(
					res,
					409,
					'not-adopted',
					'Only an adopted device can hold settings. Adopt it first.'
				);
			}
			overrides[id] = { ...(overrides[id] ?? {}), [key]: body.value };
			revision++;
			return noContent(res);
		}

		if (method === 'DELETE') {
			if (!overrides[id] || !(key in overrides[id])) {
				return error(res, 404, 'no-such-setting', `No setting named '${key}'.`);
			}
			delete overrides[id][key];
			revision++;
			return noContent(res);
		}
	}

	return error(res, 404, 'no-such-route', `Nothing is mapped at ${method} ${path}.`);
}

const notFoundDevice = (res, id) =>
	error(res, 404, 'no-such-device', `No device with id '${decodeURIComponent(id)}'.`);

const problemSentence = () =>
	'The environment variable FRAMELINK_OPERATOR_PASSWORD is not set, so this Fleet Manager ' +
	'has no operator password and cannot adopt any device yet.';

const COMPOSE_EXAMPLE = `services:
  fl-control:
    image: framelink/fl-control:latest
    environment:
      FRAMELINK_OPERATOR_PASSWORD: "choose-a-long-passphrase-at-least-24-characters"
    volumes:
      - ./framelink-data:/var/lib/fl-control
    ports:
      - "8080:8080"
    restart: unless-stopped`;

// ── the static host ───────────────────────────────────────────────────────────────────────

/**
 * Mirrors what `UseStaticFiles()` + `MapFallback` do in `ControlApp`/`GuiEndpoints`: serve the
 * file if it exists under the web root, otherwise hand back `index.html` so client-side
 * routing works. Nothing here knows anything about SvelteKit.
 */
async function serveStatic(req, res, url) {
	const requested = normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, '');
	const candidate = join(WWWROOT, requested);

	// Path traversal guard. The real server gets this from the static-files middleware.
	if (!candidate.startsWith(WWWROOT)) {
		res.writeHead(403);
		return res.end('forbidden');
	}

	const file = await tryFile(candidate);
	const target = file ?? join(WWWROOT, 'index.html');

	try {
		const body = await readFile(target);
		res.writeHead(file ? 200 : 200, {
			'content-type': MIME[extname(target)] ?? 'application/octet-stream',
			'content-length': body.length,
			'cache-control': file && requested.startsWith('_app/immutable/')
				? 'public, max-age=31536000, immutable'
				: 'no-cache'
		});
		res.end(body);
	} catch {
		res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
		res.end(
			`Not found, and there is no index.html in ${WWWROOT}.\n` +
				'Run `npm run build` first — the SPA fallback needs the built shell.\n'
		);
	}
}

async function tryFile(path) {
	try {
		const info = await stat(path);
		return info.isFile() ? path : undefined;
	} catch {
		return undefined;
	}
}

// ── go ────────────────────────────────────────────────────────────────────────────────────

const server = createServer(async (req, res) => {
	const url = new URL(req.url ?? '/', `http://${req.headers.host ?? 'localhost'}`);

	try {
		if (url.pathname === '/healthz') {
			res.writeHead(200, { 'content-type': 'text/plain' });
			return res.end('ok');
		}

		if (url.pathname.startsWith('/api')) return await handleApi(req, res, url);

		if (SERVE_STATIC) return await serveStatic(req, res, url);

		res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
		res.end('This mock only answers /api. Run vite dev for the GUI, or pass --serve.\n');
	} catch (cause) {
		console.error(cause);
		error(res, 500, 'mock-failure', String(cause));
	}
});

server.listen(PORT, () => {
	console.log(`fl-control mock listening on http://127.0.0.1:${PORT}`);
	console.log(`  configured : ${CONFIGURED ? `yes (password: ${PASSWORD})` : 'NO — setup screen'}`);
	console.log(`  devices    : ${devices.length}`);
	console.log(`  static     : ${SERVE_STATIC ? WWWROOT : 'off (vite dev serves the GUI)'}`);
});
