/**
 * Presentation metadata for settings keys — and nothing else.
 *
 * version2.md §3.4 is emphatic that the settings model is "not a fixed list but a generic
 * mechanism, because the list will grow", and `ISettingsStore` honours that: a setting is an
 * opaque key and an opaque string value, and the server never validates either.
 *
 * The GUI honours it too. **Every screen renders whatever keys the server actually holds**,
 * and the "add a setting" field accepts any key at all. What lives below is purely additive:
 * when a key the operator is looking at happens to be one this catalog recognises, they get
 * a friendly label, a sentence of help, and an input control suited to the value. When it is
 * not recognised, they get the raw key and a text box, and everything still works.
 *
 * So: adding a key here is a nicety. Forgetting to add one costs nothing. Removing this file
 * entirely would leave a slightly duller but fully functional settings screen. If you ever
 * find yourself wanting to *reject* a key because it is not in this list, stop — that is the
 * exact hard-coding §3.4 rules out.
 *
 * **What is not optional is that the keys here are the keys something reads.** Nine of the
 * nineteen entries this file used to carry were read by nothing at all: `immich.url` against the
 * agent's `immich.serverUrl`, `audio.volume` against `audio.playbackVolume`,
 * `slideshow.intervalSeconds` against `slideshow.interval`, `locale.timezone` against
 * `locale.timeZone`, and five — `call.autoAnswer`, three `display.*` keys and `update.enabled` —
 * naming features nothing implements. An operator typing the key the interface suggested set a
 * value no agent would ever read, and nothing anywhere said so. `ControlSettingsCatalogTests`
 * now asserts every key here appears as a literal in the agent's or the server's own source, so
 * this class of drift fails the suite instead of failing silently on a frame.
 */

export type SettingKind = 'text' | 'secret' | 'number' | 'url' | 'duration' | 'boolean' | 'time';

export interface SettingDescriptor {
	/** Human label. Falls back to the raw key when absent. */
	label: string;
	/** One sentence of help, written for someone who has not read the spec. */
	hint: string;
	/** Which input control suits the value. Everything unknown gets `text`. */
	kind: SettingKind;
	/** Grouping heading on the settings screen. Unknown keys land in "Other". */
	group: string;
	/** Shown as the input placeholder. Never a default — the server holds no defaults. */
	example?: string;
	/**
	 * Whether "add a setting" offers this key as a suggestion. Defaults to true.
	 *
	 * **One key sets it false, and the reason is a decision rather than taste.** A described key
	 * and a *suggested* key are different invitations: a description helps somebody read a value
	 * that is already there, while a suggestion puts the key in front of somebody who was
	 * looking for something else. `audio.arrayFirmwareFlash` authorises a one-way write to
	 * hardware that cannot be repaired by rewriting the card, and decision 91's sequencing is
	 * binding — nothing is flashed until the Safe Mode recovery route has been rehearsed on this
	 * project's own arrays. It has its own panel on the frame's page, which composes the value
	 * and shows the warnings; it has no business in a dropdown of conveniences.
	 */
	suggest?: boolean;
}

export const SETTING_GROUPS = [
	'Photos',
	'Calls',
	'Display',
	'Audio',
	'Behaviour',
	'Locale',
	'Other'
] as const;

export const SETTING_CATALOG: Readonly<Record<string, SettingDescriptor>> = {
	'immich.serverUrl': {
		label: 'Immich server URL',
		hint: 'Where the photo library lives. Every frame reads its slideshow from here.',
		kind: 'url',
		group: 'Photos',
		example: 'https://immich.example.org'
	},
	'immich.apiKey': {
		label: 'Immich API key',
		hint: 'The credential the slideshow uses to read the library. Stored as given.',
		kind: 'secret',
		group: 'Photos'
	},
	'slideshow.albums': {
		label: 'Albums',
		hint:
			'Which albums the frame shows, by album ID — the ID in the album\'s own Immich address, ' +
			'not its name. Separate several with commas, or use "shared" for every album shared ' +
			'with this frame. Per-device is the usual case — each person gets their own. Leave it ' +
			'unset and the frame shows photos its own Immich account owns, which is nothing at all ' +
			'if the photos were shared with it rather than uploaded by it.',
		kind: 'text',
		group: 'Photos',
		example: '67c9021a-0000-0000-0000-000000000000'
	},
	'slideshow.interval': {
		label: 'Photo interval',
		hint: 'Seconds each photo stays on screen before the next one.',
		kind: 'duration',
		group: 'Photos',
		example: '30'
	},
	'slideshow.url': {
		label: 'Slideshow address',
		hint:
			'The full slideshow address the frame opens, query string and all. Leave it unset ' +
			'unless you know you need something other than the albums above.',
		kind: 'url',
		group: 'Photos'
	},
	'slideshow.offlineMode': {
		label: 'Keep photos for offline',
		hint: 'Whether the frame caches photos so it keeps showing them with the library down.',
		kind: 'text',
		group: 'Photos'
	},
	'slideshow.offlineAssetCount': {
		label: 'How many to keep',
		hint: 'How many photos the frame caches for the offline case above.',
		kind: 'number',
		group: 'Photos',
		example: '100'
	},
	'call.room': {
		label: 'Call room',
		hint:
			'The room every frame with this value joins. Calling stays single-room and ' +
			'one-button; group calling is just two frames sharing a room name.',
		kind: 'text',
		group: 'Calls',
		example: 'huisman'
	},
	'display.rotation': {
		label: 'Screen rotation',
		hint: 'How far round the picture is turned, in degrees.',
		kind: 'number',
		group: 'Display',
		example: '180'
	},
	'audio.playbackVolume': {
		label: 'Speaker volume',
		hint: 'Playback level for calls, 0 to 100.',
		kind: 'number',
		group: 'Audio',
		example: '75'
	},
	'audio.captureVolume': {
		label: 'Microphone gain',
		hint: 'Capture level for the mic array, 0 to 100.',
		kind: 'number',
		group: 'Audio'
	},
	'audio.arrayFirmwareFlash': {
		label: 'Microphone firmware authorisation',
		hint:
			'One write of the pinned firmware to one microphone unit, spent the instant it starts. ' +
			'Use the Microphone firmware panel on the frame’s own page — it composes this ' +
			'value from the pinned image and the frame it is looking at, so it cannot name the wrong ' +
			'frame. Setting it here by hand, and especially as a fleet default, arms a write on every ' +
			'frame that has not already spent that exact string.',
		kind: 'text',
		group: 'Audio',
		suggest: false
	},
	'audio.arrayBoardRevision': {
		label: 'Microphone board revision',
		hint:
			'Which hardware revision of the microphone bar this frame has, read off the printing on ' +
			'the board itself — no software can read it. Set it per frame, not fleet-wide. Leaving it ' +
			'blank changes nothing; filling it in can only ever *stop* a firmware write, never allow ' +
			'one, and a value that disagrees with what the frame reads from the bar stops it too.',
		kind: 'text',
		group: 'Audio',
		example: 'V1.1'
	},
	'repair.countdownSeconds': {
		label: 'Repair countdown',
		hint:
			'How long a working frame shows what it is about to do before rebooting to verify it. ' +
			'Setting up a new frame never counts down; left unset, a repair pauses for 60 seconds.',
		kind: 'duration',
		group: 'Behaviour',
		example: '60'
	},
	'provisioning.paceSeconds': {
		label: 'Provisioning pace',
		hint:
			'Seconds a frame being set up for the first time pauses before each restart, so you ' +
			'can watch it happen. Leave it at 0 and setting up runs at full speed.',
		kind: 'duration',
		group: 'Behaviour',
		example: '0'
	},
	'operator.name': {
		label: 'Who to contact',
		hint:
			'Your name, shown on a frame that has stopped and needs a person. Every frame keeps ' +
			'its own copy, so it can still say who to ask when it cannot reach this server.',
		kind: 'text',
		group: 'Behaviour',
		example: 'Jori'
	},
	'operator.contact': {
		label: 'How to reach you',
		hint:
			'A phone number, an email address, or where you are. Shown beside your name on a ' +
			'frame that has given up, for whoever is standing in front of it.',
		kind: 'text',
		group: 'Behaviour',
		example: '06 12 34 56 78'
	},
	'updates.osSecurityAuto': {
		label: 'Debian security updates',
		hint:
			'Whether the frame installs Debian security updates on its own. On unless you turn it ' +
			'off, and turning it off is a decision to make deliberately.',
		kind: 'text',
		group: 'Behaviour'
	},
	'updates.osUpgradePolicy': {
		label: 'Debian update scope',
		hint: 'Which Debian updates the frame takes. Security-only unless you widen it.',
		kind: 'text',
		group: 'Behaviour'
	},
	'logging.journalMaxUse': {
		label: 'Journal size cap',
		hint: 'How much disk the frame lets its own log take before it rolls the oldest away.',
		kind: 'text',
		group: 'Behaviour',
		example: '64M'
	},
	'power.cpuGovernor': {
		label: 'CPU governor',
		hint: 'How aggressively the frame clocks its processor.',
		kind: 'text',
		group: 'Behaviour',
		example: 'performance'
	},
	'device.hostname': {
		label: 'Hostname',
		hint: 'The name this frame answers to on the household network.',
		kind: 'text',
		group: 'Behaviour',
		example: 'framelink-hallway'
	},
	'packages.reportInterval': {
		label: 'Package check interval',
		hint:
			'How often a frame re-reads its own installed software. It only sends anything when ' +
			'something has actually changed, so this is cheap. Six hours if unset.',
		kind: 'duration',
		group: 'Behaviour',
		example: '06:00:00'
	},
	'locale.timeZone': {
		label: 'Time zone',
		hint: 'IANA zone name. Drives the backlight schedule and every clock on the frame.',
		kind: 'text',
		group: 'Locale',
		example: 'Europe/Amsterdam'
	},
	'locale.language': {
		label: 'Language',
		hint: 'BCP-47 tag for everything the frame says out loud or on screen.',
		kind: 'text',
		group: 'Locale',
		example: 'nl-NL'
	},
	'locale.keyboard': {
		label: 'Keyboard layout',
		hint: 'Which layout a keyboard plugged into the frame uses.',
		kind: 'text',
		group: 'Locale',
		example: 'us'
	},
	'locale.wifiCountry': {
		label: 'Wi-Fi country',
		hint: 'Two-letter country code. The radio needs it to pick legal channels and power.',
		kind: 'text',
		group: 'Locale',
		example: 'NL'
	}
};

/** What is known about a key — real metadata if catalogued, an honest fallback if not. */
export function describeSetting(key: string): SettingDescriptor & { known: boolean } {
	const known = SETTING_CATALOG[key];
	if (known) return { ...known, known: true };

	return {
		label: key,
		hint: 'A setting this Fleet Manager holds but the interface has no description for.',
		kind: key.toLowerCase().includes('secret') || key.toLowerCase().includes('key')
			? 'secret'
			: 'text',
		group: 'Other',
		known: false
	};
}

/** Catalogued keys not yet present on the server — offered as suggestions, never imposed. */
export function suggestedKeys(existing: Iterable<string>): string[] {
	const held = new Set(existing);
	return Object.entries(SETTING_CATALOG)
		.filter(([key, descriptor]) => !held.has(key) && descriptor.suggest !== false)
		.map(([key]) => key)
		.sort();
}

/** Groups a key set for rendering, keeping `SETTING_GROUPS` order and dropping empty groups. */
export function groupKeys(keys: string[]): Array<{ group: string; keys: string[] }> {
	const buckets = new Map<string, string[]>();
	for (const key of keys) {
		const group = describeSetting(key).group;
		(buckets.get(group) ?? buckets.set(group, []).get(group)!).push(key);
	}

	const ordered: Array<{ group: string; keys: string[] }> = [];
	for (const group of SETTING_GROUPS) {
		const bucket = buckets.get(group);
		if (bucket?.length) ordered.push({ group, keys: bucket.sort() });
		buckets.delete(group);
	}
	// Anything whose group is not in SETTING_GROUPS still renders — the catalog does not get
	// to hide a key the server is holding.
	for (const [group, bucket] of [...buckets].sort(([a], [b]) => a.localeCompare(b))) {
		ordered.push({ group, keys: bucket.sort() });
	}
	return ordered;
}
