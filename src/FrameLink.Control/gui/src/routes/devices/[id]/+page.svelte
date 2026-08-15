<script lang="ts">
	/**
	 * Everything known about one frame.
	 *
	 * Three panels: identity and presence, its settings with fleet-default-versus-override
	 * made visually obvious (§3.4), and its state actions.
	 *
	 * The row is fetched from `GET /api/devices/{id}` on mount and then kept live by the fleet
	 * poll, which is what makes a hard page load — a link pasted into a chat, a bookmark, a
	 * refresh — render immediately instead of showing a placeholder until the next poll.
	 *
	 * Settings come from `GET /api/devices/{id}/settings`, which returns the fleet defaults,
	 * this device's overrides and the effective result side by side — the one endpoint whose
	 * shape made a screen *easier* rather than harder.
	 */
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api, ApiError } from '$lib/api/client';
	import type { DeviceSettingsResponse } from '$lib/api/types';
	import { fleet } from '$lib/stores/fleet.svelte';
	import { adoptDevice, blockDevice, forgetDevice, unblockDevice } from '$lib/stores/device-actions';
	import { toasts } from '$lib/stores/toast.svelte';
	import { describePresence } from '$lib/presence';
	import { durationSince, timeAgo, timeExact } from '$lib/format';
	import { groupKeys } from '$lib/settings-catalog';
	import { rise, settle } from '$lib/design/motion';
	import AddSetting from '$lib/components/AddSetting.svelte';
	import Button from '$lib/components/Button.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import ConfirmDialog from '$lib/components/ConfirmDialog.svelte';
	import DetailRow from '$lib/components/DetailRow.svelte';
	import Fingerprint from '$lib/components/Fingerprint.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import PresenceChip from '$lib/components/PresenceChip.svelte';
	import SettingRow from '$lib/components/SettingRow.svelte';
	import TextField from '$lib/components/TextField.svelte';

	const deviceId = $derived(decodeURIComponent(page.params.id ?? ''));
	const device = $derived(fleet.find(deviceId));
	const presence = $derived(device ? describePresence(device) : undefined);

	let settings = $state<DeviceSettingsResponse | undefined>();
	let settingsProblem = $state<string | undefined>();

	/** False once the first single-device fetch has answered, either way. */
	let loading = $state(true);

	let renaming = $state(false);
	let newName = $state('');
	let busy = $state(false);
	let confirmBlock = $state(false);
	let confirmForget = $state(false);

	const allKeys = $derived(
		settings
			? [...new Set([...Object.keys(settings.fleetDefaults), ...Object.keys(settings.overrides)])]
			: []
	);
	const groups = $derived(groupKeys(allKeys));
	const adopted = $derived(device?.state === 'adopted');
	const overrideCount = $derived(Object.keys(settings?.overrides ?? {}).length);

	async function loadSettings() {
		if (!deviceId) return;
		try {
			settings = await api.deviceSettings(deviceId);
			settingsProblem = undefined;
		} catch (cause) {
			settingsProblem =
				cause instanceof ApiError ? cause.message : 'The settings for this device could not be read.';
		}
	}

	async function loadDevice() {
		if (!deviceId) return;
		try {
			// Straight into the fleet store, so this screen and the list agree and the ordinary
			// poll takes over from here without a second source of truth.
			fleet.merge(await api.device(deviceId));
		} catch (cause) {
			if (!(cause instanceof ApiError) || cause.status !== 404) {
				toasts.error('Could not read this device', cause);
			}
		} finally {
			loading = false;
		}
	}

	onMount(() => {
		// The device first, because a hard load has nothing to show until it lands. The fleet
		// poll then owns presence, and the settings are fetched here and again after any write
		// because nothing else moves them.
		void loadDevice();
		void loadSettings();
	});

	async function saveOverride(key: string, value: string) {
		try {
			await api.setDeviceSetting(deviceId, key, value);
			toasts.ok(`Override set for ${key}`, 'This frame now ignores the fleet default for it.');
			await loadSettings();
		} catch (cause) {
			if (cause instanceof ApiError && cause.isNotAdopted) {
				toasts.fail('Only an adopted device can hold settings', cause.message);
			} else {
				toasts.error(`Could not set ${key}`, cause);
			}
		}
	}

	async function removeOverride(key: string) {
		try {
			await api.removeDeviceSetting(deviceId, key);
			toasts.ok(`Override removed for ${key}`, 'This frame is back on the fleet default.');
			await loadSettings();
		} catch (cause) {
			toasts.error(`Could not remove the override for ${key}`, cause);
		}
	}

	async function adoptOrRename() {
		busy = true;
		// Adoption *is* the rename: `AdoptAsync` writes `display_name` and is safe to call on
		// an already-adopted device, so there is no separate rename route to reach for.
		await adoptDevice(deviceId, newName);
		busy = false;
		renaming = false;
	}

	async function runBlock() {
		busy = true;
		await blockDevice(deviceId);
		busy = false;
		confirmBlock = false;
	}

	async function runUnblock() {
		busy = true;
		await unblockDevice(deviceId);
		busy = false;
	}

	async function runForget() {
		busy = true;
		const ok = await forgetDevice(deviceId);
		busy = false;
		confirmForget = false;
		if (ok) void goto('/');
	}
</script>

<div class="page">
	<a class="back" href="/"><Icon name="arrowLeft" size={15} /> Fleet</a>

	{#if !device}
		<Card>
			<p class="muted">
				{loading
					? 'Looking for this device…'
					: 'This Fleet Manager has no device with that id. It may have been forgotten, or its pending record may have expired.'}
			</p>
		</Card>
	{:else}
		<header in:rise={{ y: 12 }}>
			<div class="title">
				{#if renaming}
					<div class="rename">
						<TextField
							bind:value={newName}
							label="Name"
							placeholder={device.name ?? "Oma's living room"}
							autofocus
							onkeydown={(event) => {
								if (event.key === 'Enter') void adoptOrRename();
								if (event.key === 'Escape') renaming = false;
							}}
						/>
						<Button variant="primary" icon="check" {busy} onclick={adoptOrRename}>Save</Button>
						<Button variant="quiet" onclick={() => (renaming = false)}>Cancel</Button>
					</div>
				{:else}
					<h1>
						{device.name ?? 'Unnamed frame'}
						{#if adopted}
							<button
								class="rename-trigger"
								onclick={() => {
									newName = device.name ?? '';
									renaming = true;
								}}
								aria-label="Rename"
								title="Rename"
							>
								<Icon name="pencil" size={15} />
							</button>
						{/if}
					</h1>
				{/if}

				<div class="chips">
					{#if device.state === 'blocked'}
						<Chip tone="danger" icon="ban">Blocked</Chip>
					{:else if device.state === 'pending'}
						<Chip tone="warn" icon="clock">Waiting to be adopted</Chip>
					{:else}
						<Chip tone="ok" icon="shieldCheck" size="md">Adopted</Chip>
						<PresenceChip {device} verbose />
					{/if}
				</div>
			</div>

			<div class="actions">
				{#if device.state === 'pending'}
					<Button variant="primary" icon="shieldCheck" {busy} onclick={() => void adoptOrRename()}>
						Adopt
					</Button>
					<Button variant="danger" icon="ban" onclick={() => (confirmBlock = true)}>Block</Button>
				{:else if device.state === 'blocked'}
					<Button variant="secondary" icon="refresh" {busy} onclick={runUnblock}>Unblock</Button>
				{:else}
					<Button variant="danger" icon="ban" onclick={() => (confirmBlock = true)}>Block</Button>
				{/if}
				<Button
					variant="quiet"
					icon="trash"
					title="Forget this device"
					aria-label="Forget this device"
					onclick={() => (confirmForget = true)}
				/>
			</div>
		</header>

		{#if presence && presence.presence !== 'online'}
			<div class="state-note {presence.tone}" in:rise={{ y: 8 }}>
				<Icon name={presence.tone === 'ok' ? 'info' : 'alert'} size={16} />
				<div>
					<b>{presence.label}.</b>
					{presence.meaning}
					{#if presence.presence === 'offline'}
						Offline for {durationSince(device.lastSeenUtc)}.
					{/if}
				</div>
			</div>
		{/if}

		<div class="columns">
			<section in:settle={{ index: 0, count: 2 }}>
				<Card>
					<h2>Identity</h2>
					<dl>
						<DetailRow label="Device id">
							<Fingerprint deviceId={device.deviceId} size="lg" />
						</DetailRow>
						<DetailRow label="Hardware serial" value={device.hardwareSerial} mono />
						<DetailRow label="Last seen from" value={device.lastRemoteAddress} mono />
						<DetailRow label="Agent version" value={device.agentVersion} mono />
						<DetailRow label="Agent self-report" value={device.agentStatus} />
						<DetailRow label="Protocol version">
							{#if device.protocolVersion === undefined}
								<span class="muted">—</span>
							{:else}
								<span class="proto">
									{device.protocolVersion}
									{#if device.protocolCompatible}
										<Chip tone="ok" size="sm">matches this server</Chip>
									{:else}
										<Chip tone="danger" size="sm">does not match this server</Chip>
									{/if}
								</span>
							{/if}
						</DetailRow>
						<DetailRow
							label="First seen"
							value={timeExact(device.firstSeenUtc)}
							title={timeAgo(device.firstSeenUtc)}
						/>
						<DetailRow
							label={device.online ? 'Last contact' : 'Offline since'}
							value={timeExact(device.lastSeenUtc)}
							title={timeAgo(device.lastSeenUtc)}
						/>
						{#if device.state !== 'pending'}
							<DetailRow
								label={device.state === 'blocked' ? 'Blocked since' : 'Adopted on'}
								value={timeExact(device.stateChangedUtc)}
								title={timeAgo(device.stateChangedUtc)}
							/>
						{/if}
					</dl>
				</Card>
			</section>

			<section in:settle={{ index: 1, count: 2 }}>
				<Card>
					<div class="settings-head">
						<h2>Settings</h2>
						{#if adopted && overrideCount > 0}
							<Chip tone="warn" icon="pencil" size="sm">
								{overrideCount} override{overrideCount === 1 ? '' : 's'}
							</Chip>
						{/if}
						{#if settings}
							<Chip tone="tech" size="sm">revision {settings.revision}</Chip>
						{/if}
					</div>

					{#if !adopted}
						<div class="locked">
							<Icon name="key" size={16} />
							<p>
								Only an adopted device can hold settings, so this frame receives nothing at
								all — no configuration, no tokens, no commands. The fleet defaults are shown
								below for reference; they will apply the moment it is adopted.
							</p>
						</div>
					{/if}

					{#if settingsProblem}
						<p class="problem">{settingsProblem}</p>
					{:else if allKeys.length === 0}
						<p class="muted">
							No fleet defaults and no overrides. Nothing to send.
						</p>
					{:else}
						<div class="groups">
							{#each groups as group (group.group)}
								<div class="group">
									<h3>{group.group}</h3>
									<div class="rows">
										{#each group.keys as key (key)}
											<SettingRow
												settingKey={key}
												fleetValue={settings?.fleetDefaults[key]}
												overrideValue={settings?.overrides[key]}
												mode="device"
												locked={!adopted}
												onsave={saveOverride}
												onremove={removeOverride}
											/>
										{/each}
									</div>
								</div>
							{/each}
						</div>
					{/if}

					{#if adopted}
						<AddSetting existing={Object.keys(settings?.overrides ?? {})} mode="device" onadd={saveOverride} />
					{/if}
				</Card>
			</section>
		</div>
	{/if}
</div>

<ConfirmDialog
	bind:open={confirmBlock}
	title="Block this frame?"
	confirmLabel="Block it"
	{busy}
	onconfirm={runBlock}
	oncancel={() => (confirmBlock = false)}
>
	Its connection closes immediately and the photo slideshow stops on the frame itself. It
	stays in the list behind the <b>Show blocked</b> toggle, so this is reversible.
</ConfirmDialog>

<ConfirmDialog
	bind:open={confirmForget}
	title="Forget this frame?"
	confirmLabel="Forget it"
	{busy}
	onconfirm={runForget}
	oncancel={() => (confirmForget = false)}
>
	The record and every per-device override are deleted. The frame itself is untouched — it
	keeps its keypair, and the next time it connects it reappears here as a new pending device
	under the same id.
</ConfirmDialog>

<style>
	.page {
		display: grid;
		gap: var(--space-6);
	}

	.back {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-sm);
		color: var(--text-3);
		text-decoration: none;
		justify-self: start;
		transition:
			color var(--dur-quick) var(--ease-standard),
			transform var(--dur-quick) var(--ease-standard);
	}

	.back:hover {
		color: var(--text-1);
		transform: translateX(-2px);
	}

	header {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: var(--space-6);
		flex-wrap: wrap;
	}

	.title {
		display: grid;
		gap: var(--space-3);
		min-width: 0;
	}

	h1 {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		font-size: var(--text-2xl);
	}

	.rename-trigger {
		color: var(--text-3);
		padding: var(--space-1);
		border-radius: var(--radius-xs);
		transition:
			color var(--dur-quick) var(--ease-standard),
			background-color var(--dur-quick) var(--ease-standard);
	}
	.rename-trigger:hover {
		color: var(--accent);
		background: var(--surface-2);
	}

	.rename {
		display: flex;
		align-items: flex-end;
		gap: var(--space-3);
		flex-wrap: wrap;
	}

	.chips {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		flex-wrap: wrap;
	}

	.actions {
		display: flex;
		align-items: center;
		gap: var(--space-2);
	}

	.state-note {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid var(--note-line);
		background: var(--note-fill);
		color: var(--text-2);
		font-size: var(--text-sm);
		line-height: var(--leading-snug);
	}

	.state-note b {
		color: var(--note-ink);
	}

	.state-note :global(.icon) {
		color: var(--note-ink);
		margin-top: 2px;
	}

	.state-note.warn {
		--note-ink: var(--accent);
		--note-fill: var(--accent-soft);
		--note-line: var(--accent-line);
	}
	.state-note.danger {
		--note-ink: var(--danger);
		--note-fill: var(--danger-soft);
		--note-line: var(--danger-line);
	}
	.state-note.muted {
		--note-ink: var(--text-1);
		--note-fill: var(--surface-1);
		--note-line: var(--line);
	}
	.state-note.ok {
		--note-ink: var(--ok);
		--note-fill: var(--ok-soft);
		--note-line: var(--ok-line);
	}
	.state-note.info {
		--note-ink: var(--info);
		--note-fill: var(--info-soft);
		--note-line: var(--info-line);
	}

	.columns {
		display: grid;
		grid-template-columns: minmax(0, 23rem) minmax(0, 1fr);
		gap: var(--space-6);
		align-items: start;
	}

	/* Identity stays in view while the settings column scrolls. The whole point of this
	   screen is comparing a value against the frame it belongs to. */
	.columns > section:first-child {
		position: sticky;
		top: calc(60px + var(--space-6));
	}

	h2 {
		font-size: var(--text-lg);
		margin-bottom: var(--space-3);
	}

	h3 {
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
		margin-bottom: var(--space-2);
	}

	dl {
		margin: 0;
	}

	.proto {
		display: inline-flex;
		align-items: center;
		gap: var(--space-3);
	}

	.settings-head {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		margin-bottom: var(--space-4);
	}

	.settings-head h2 {
		margin: 0;
		margin-right: auto;
	}

	.locked {
		display: flex;
		align-items: flex-start;
		gap: var(--space-3);
		padding: var(--space-4);
		margin-bottom: var(--space-5);
		border-radius: var(--radius-md);
		border: 1px dashed var(--line-strong);
		background: var(--surface-sunken);
		color: var(--text-2);
		font-size: var(--text-xs);
		line-height: var(--leading-normal);
	}

	.groups {
		display: grid;
		gap: var(--space-5);
	}

	.rows {
		display: grid;
		gap: var(--space-3);
	}

	.muted {
		color: var(--text-3);
		font-size: var(--text-sm);
	}

	.problem {
		color: var(--danger);
		font-size: var(--text-sm);
	}

	@media (max-width: 66rem) {
		.columns {
			grid-template-columns: minmax(0, 1fr);
		}
		.columns > section:first-child {
			position: static;
		}
	}
</style>
