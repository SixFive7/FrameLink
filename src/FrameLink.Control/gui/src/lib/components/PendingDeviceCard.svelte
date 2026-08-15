<script lang="ts">
	/**
	 * A frame waiting to be adopted.
	 *
	 * This is the most important object in the whole console. §3.3 makes adoption the
	 * operator's core action, and the physical situation it happens in is specific: somebody
	 * is standing at a bench with one or more frames in front of them, each showing its own
	 * fingerprint and serial on its own screen, trying to work out which row is which frame.
	 *
	 * So the card is built as a matching aid first and a form second:
	 *
	 *  - the fingerprint is set at up to 48px — the same string the frame is displaying, at a
	 *    size that survives the distance between a laptop and a bench;
	 *  - the hardware serial sits directly under it at 22px, because that is the *other*
	 *    string on the frame's screen and the two are read as a pair;
	 *  - the card is accent-toned with a lit edge and a slow sweeping sheen, so a pending
	 *    device is impossible to scroll past;
	 *  - **Adopt** is the only primary button on the screen; **Block** is outlined, available
	 *    but not eager.
	 *
	 * Naming is optional and inline. `AdoptRequest.Name` is nullable and adoption without one
	 * is perfectly valid, so the field opens on demand instead of standing between the
	 * operator and the press.
	 */
	import type { DeviceView } from '$lib/api/types';
	import { timeAgo, timeExact } from '$lib/format';
	import { slip } from '$lib/design/motion';
	import { adoptDevice, blockDevice } from '$lib/stores/device-actions';
	import Button from './Button.svelte';
	import Card from './Card.svelte';
	import Chip from './Chip.svelte';
	import ConfirmDialog from './ConfirmDialog.svelte';
	import Fingerprint from './Fingerprint.svelte';
	import Icon from './Icon.svelte';
	import TextField from './TextField.svelte';

	interface Props {
		device: DeviceView;
	}

	let { device }: Props = $props();

	let naming = $state(false);
	let name = $state('');
	let adopting = $state(false);
	let blocking = $state(false);
	let confirmBlock = $state(false);

	/** Composed in script rather than in markup: interleaved `{#if}` blocks eat the spaces
	    between the clauses, and this line is read at a glance. */
	const report = $derived(
		[
			device.agentVersion ? `Agent ${device.agentVersion}` : undefined,
			device.agentStatus ? `reporting “${device.agentStatus}”` : undefined
		]
			.filter(Boolean)
			.join(', ')
	);

	async function adopt() {
		adopting = true;
		await adoptDevice(device.deviceId, name);
		adopting = false;
		naming = false;
		name = '';
	}

	async function block() {
		blocking = true;
		await blockDevice(device.deviceId);
		blocking = false;
		confirmBlock = false;
	}
</script>

<Card tone="accent" padding="none">
	<div class="sheen" aria-hidden="true"></div>

	<div class="inner">
		<header>
			<Chip tone="warn" icon="clock" size="sm">Waiting to be adopted</Chip>
			<span class="seen" title={timeExact(device.lastSeenUtc)}>
				Last contact {timeAgo(device.lastSeenUtc)}
			</span>
		</header>

		<div class="identity">
			<p class="caption">Fingerprint on the frame's screen</p>
			<Fingerprint deviceId={device.deviceId} size="bench" animate />

			<div class="serial">
				<span class="caption">Hardware serial</span>
				<span class="serial-value">{device.hardwareSerial ?? 'not reported'}</span>
			</div>
		</div>

		{#if report}
			<p class="report">
				<Icon name="info" size={14} />
				<span>{report}</span>
			</p>
		{/if}

		{#if naming}
			<div class="naming" transition:slip={{ x: -12 }}>
				<TextField
					bind:value={name}
					label="Name this frame"
					placeholder="Oma's living room"
					autofocus
					onkeydown={(event) => {
						if (event.key === 'Enter') void adopt();
						if (event.key === 'Escape') naming = false;
					}}
				/>
			</div>
		{/if}

		<footer>
			<Button variant="primary" size="lg" icon="shieldCheck" busy={adopting} onclick={adopt}>
				Adopt
			</Button>

			{#if !naming}
				<Button variant="ghost" size="lg" icon="pencil" onclick={() => (naming = true)}>
					Name it first
				</Button>
			{/if}

			<span class="spacer"></span>

			<Button variant="danger" size="lg" icon="ban" onclick={() => (confirmBlock = true)}>
				Block
			</Button>
		</footer>
	</div>
</Card>

<ConfirmDialog
	bind:open={confirmBlock}
	title="Block this frame?"
	confirmLabel="Block it"
	busy={blocking}
	onconfirm={block}
	oncancel={() => (confirmBlock = false)}
>
	Its connection is closed straight away and it will not run the photo slideshow. Blocked
	frames stay in the list behind the <b>Show blocked</b> toggle, so this is reversible.
</ConfirmDialog>

<style>
	.inner {
		position: relative;
		z-index: 1;
		display: grid;
		gap: var(--space-5);
		padding: var(--space-6);
	}

	header {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-4);
		flex-wrap: wrap;
	}

	.seen {
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	.caption {
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	.identity {
		display: grid;
		gap: var(--space-2);
	}

	.serial {
		display: flex;
		align-items: baseline;
		gap: var(--space-3);
		flex-wrap: wrap;
		margin-top: var(--space-2);
	}

	.serial-value {
		font-family: var(--font-mono);
		font-size: var(--text-xl);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-code);
		color: var(--text-1);
	}

	.report {
		display: flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-xs);
		color: var(--text-2);
	}

	.report span {
		color: var(--text-1);
		font-weight: var(--weight-medium);
	}

	.naming {
		max-width: 26rem;
	}

	footer {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		flex-wrap: wrap;
	}

	.spacer {
		flex: 1;
	}

	/*
		The sweep. A single wide highlight crossing the card every eight seconds — the one
		looping decorative animation in the app, and it earns its place: a pending frame is the
		thing the operator must not miss, and a moving highlight is caught by peripheral vision
		in a way that a static colour is not.
	*/
	.sheen {
		position: absolute;
		inset: 0;
		border-radius: inherit;
		overflow: hidden;
		pointer-events: none;
	}

	.sheen::after {
		content: '';
		position: absolute;
		top: -50%;
		bottom: -50%;
		width: 38%;
		background: linear-gradient(
			100deg,
			transparent,
			color-mix(in oklab, var(--accent) 9%, transparent),
			transparent
		);
		transform: skewX(-18deg);
		animation: sweep 8s var(--ease-glide) infinite;
	}

	@keyframes sweep {
		0% {
			left: -45%;
			opacity: 0;
		}
		12% {
			opacity: 1;
		}
		45% {
			opacity: 1;
		}
		60%,
		100% {
			left: 115%;
			opacity: 0;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.sheen::after {
			animation: none;
			opacity: 0;
		}
	}
</style>
