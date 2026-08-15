<script lang="ts">
	/**
	 * One adopted or blocked frame in the fleet list.
	 *
	 * The row is a link to the device's detail screen — the whole row, not a chevron — with
	 * the state actions living inside it as real buttons. A card that is entirely clickable
	 * except for two small holes is fiddly, so the actions stop the event themselves and the
	 * hit target stays honest.
	 *
	 * Blocked rows render at reduced opacity with the state chip in the danger tone and a
	 * single **Unblock** action, because the toggle that reveals them exists precisely so an
	 * accidental block can be walked back (§3.3).
	 */
	import type { DeviceView } from '$lib/api/types';
	import { timeAgo, timeExact } from '$lib/format';
	import { blockDevice, unblockDevice } from '$lib/stores/device-actions';
	import Button from './Button.svelte';
	import Chip from './Chip.svelte';
	import ConfirmDialog from './ConfirmDialog.svelte';
	import Fingerprint from './Fingerprint.svelte';
	import Icon from './Icon.svelte';
	import PresenceChip from './PresenceChip.svelte';

	interface Props {
		device: DeviceView;
	}

	let { device }: Props = $props();

	let busy = $state(false);
	let confirmBlock = $state(false);

	async function block() {
		busy = true;
		await blockDevice(device.deviceId);
		busy = false;
		confirmBlock = false;
	}

	async function unblock() {
		busy = true;
		await unblockDevice(device.deviceId);
		busy = false;
	}
</script>

<div class="row" class:blocked={device.state === 'blocked'}>
	<a class="target" href="/devices/{encodeURIComponent(device.deviceId)}">
		<span class="sr-only">Open {device.name ?? device.deviceId}</span>
	</a>

	<div class="lead">
		<h3 class="name">
			{device.name ?? 'Unnamed frame'}
			{#if !device.name}<span class="unnamed">no name set</span>{/if}
		</h3>
		<div class="id"><Fingerprint deviceId={device.deviceId} size="sm" /></div>
	</div>

	<div class="state">
		{#if device.state === 'blocked'}
			<Chip tone="danger" icon="ban" size="sm">Blocked</Chip>
		{:else}
			<PresenceChip {device} size="sm" verbose />
		{/if}
	</div>

	<div class="facts">
		{#if device.agentVersion}
			<Chip tone="tech" size="sm" title="Agent build reported at the last handshake">
				{device.agentVersion}
			</Chip>
		{/if}
		{#if device.hardwareSerial}
			<span class="serial" title="Hardware serial">{device.hardwareSerial}</span>
		{/if}
	</div>

	<div class="seen" title="Last proven contact: {timeExact(device.lastSeenUtc)}">
		{timeAgo(device.lastSeenUtc)}
	</div>

	<div class="actions">
		{#if device.state === 'blocked'}
			<Button variant="secondary" size="sm" icon="refresh" {busy} onclick={unblock}>
				Unblock
			</Button>
		{:else}
			<Button
				variant="ghost"
				size="sm"
				icon="ban"
				aria-label="Block {device.name ?? device.deviceId}"
				onclick={() => (confirmBlock = true)}
			>
				Block
			</Button>
		{/if}
		<span class="chevron" aria-hidden="true"><Icon name="chevronRight" size={16} /></span>
	</div>
</div>

<ConfirmDialog
	bind:open={confirmBlock}
	title="Block {device.name ?? 'this frame'}?"
	confirmLabel="Block it"
	{busy}
	onconfirm={block}
	oncancel={() => (confirmBlock = false)}
>
	Its connection is closed straight away and the photo slideshow stops on the frame itself.
	You can unblock it from the <b>Show blocked</b> view.
</ConfirmDialog>

<style>
	.row {
		position: relative;
		display: grid;
		grid-template-columns: minmax(11rem, 1.6fr) auto minmax(0, 1fr) auto auto;
		align-items: center;
		gap: var(--space-4);
		padding: var(--space-4) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid transparent;
		transition:
			background-color var(--dur-quick) var(--ease-standard),
			border-color var(--dur-quick) var(--ease-standard),
			transform var(--dur-quick) var(--ease-standard);
	}

	.row:hover {
		background: var(--surface-2);
		border-color: var(--line);
		transform: translateX(2px);
	}

	.row:has(.target:focus-visible) {
		background: var(--surface-2);
		border-color: var(--accent-line);
	}

	.blocked {
		opacity: 0.62;
	}
	.blocked:hover {
		opacity: 1;
	}

	/* The stretched link. Sits under the content so real buttons stay clickable. */
	.target {
		position: absolute;
		inset: 0;
		z-index: 0;
		border-radius: inherit;
	}

	.lead,
	.state,
	.facts,
	.seen,
	.actions {
		position: relative;
		z-index: 1;
		pointer-events: none;
	}

	/* …but anything actually interactive inside them gets its events back. */
	.lead :global(button),
	.actions :global(button),
	.actions :global(a) {
		pointer-events: auto;
	}

	.lead {
		display: grid;
		gap: 2px;
		min-width: 0;
	}

	.name {
		font-size: var(--text-md);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-snug);
		display: flex;
		align-items: baseline;
		gap: var(--space-2);
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	.unnamed {
		font-size: var(--text-2xs);
		font-weight: var(--weight-normal);
		letter-spacing: var(--track-wide);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.id {
		color: var(--text-3);
	}

	.facts {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		min-width: 0;
		overflow: hidden;
	}

	.serial {
		font-family: var(--font-mono);
		font-size: var(--text-2xs);
		letter-spacing: var(--track-code);
		color: var(--text-3);
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
	}

	.seen {
		font-size: var(--text-xs);
		color: var(--text-3);
		white-space: nowrap;
	}

	.actions {
		display: flex;
		align-items: center;
		gap: var(--space-2);
	}

	.chevron {
		color: var(--text-3);
		display: grid;
		place-items: center;
		transition:
			transform var(--dur-quick) var(--ease-standard),
			color var(--dur-quick) var(--ease-standard);
	}

	.row:hover .chevron {
		color: var(--accent);
		transform: translateX(3px);
	}

	@media (max-width: 60rem) {
		.row {
			grid-template-columns: 1fr auto;
			row-gap: var(--space-3);
		}
		.facts,
		.seen {
			grid-column: 1 / -1;
		}
		.actions {
			grid-column: 2;
			grid-row: 1;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.row:hover,
		.row:hover .chevron {
			transform: none;
		}
	}
</style>
