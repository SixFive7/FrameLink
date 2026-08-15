<script lang="ts">
	/**
	 * The fleet. The main screen.
	 *
	 * Three regions, in the order the operator's attention should fall:
	 *
	 *  1. **Pending.** Unmissable by construction — full-width accent cards with a sweeping
	 *     sheen and a 48px fingerprint, above everything else, with a live count in the
	 *     header. §3.3 makes adoption the core action; the screen is laid out around that one
	 *     fact rather than around a uniform table.
	 *  2. **Adopted**, sorted by trouble first (`presence.ts` gives each state a weight), so
	 *     an incompatible or degraded frame rises to the top of a long list on its own.
	 *  3. **Blocked**, hidden behind a toggle. §3.3 requires an accidental block to be
	 *     reversible, so the toggle is a visible control with a count on it, not a filter
	 *     buried in a menu.
	 *
	 * The list is keyed and animated: rows FLIP into their new position when the server's
	 * last-seen ordering changes under a poll, and a frame that appears mid-poll arrives with
	 * the same `settle` entrance as everything else. That is not decoration — a fleet list
	 * that silently rewrites itself every four seconds is how you lose the row you were
	 * reading.
	 */
	import { fleet } from '$lib/stores/fleet.svelte';
	import { plural, timeAgo } from '$lib/format';
	import { reorder, rise, settle } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Card from '$lib/components/Card.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import DeviceRow from '$lib/components/DeviceRow.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import PendingDeviceCard from '$lib/components/PendingDeviceCard.svelte';

	const pending = $derived(fleet.pending);
	const adopted = $derived(fleet.adopted);
	const blocked = $derived(fleet.blocked);

	const online = $derived(adopted.filter((device) => device.online).length);
</script>

<div class="fleet">
	<header class="head" in:rise={{ y: 10 }}>
		<div>
			<h1>Fleet</h1>
			<p class="summary">
				{#if fleet.loading}
					Reading the device list…
				{:else}
					{plural(adopted.length, 'adopted frame')}, {online} online{#if pending.length},
						<b
							>{pending.length === 1
								? 'one waiting to be adopted'
								: `${pending.length} waiting to be adopted`}</b
						>{/if}
				{/if}
			</p>
		</div>

		<div class="head-tools">
			{#if blocked.length > 0}
				<label class="toggle">
					<input type="checkbox" bind:checked={fleet.includeBlocked} />
					<span class="track"><span class="knob"></span></span>
					Show blocked
					<Chip tone="danger" size="sm">{blocked.length}</Chip>
				</label>
			{/if}

			<Button
				variant="quiet"
				icon="refresh"
				title="Refresh now"
				aria-label="Refresh now"
				onclick={() => void fleet.refresh()}
			/>
		</div>
	</header>

	{#if fleet.error}
		<div class="banner" role="status" in:rise={{ y: 8 }}>
			<Icon name="alert" size={15} />
			<span>{fleet.error}</span>
			{#if fleet.refreshedAt}
				<span class="stale">Showing the list from {timeAgo(new Date(fleet.refreshedAt).toISOString())}.</span>
			{/if}
		</div>
	{/if}

	{#if pending.length > 0}
		<section class="pending" aria-labelledby="pending-heading">
			<div class="section-head">
				<h2 id="pending-heading">
					<span class="beacon" aria-hidden="true"></span>
					{pending.length === 1 ? 'A frame is waiting' : `${pending.length} frames are waiting`}
				</h2>
				<p>
					Match the fingerprint below against the one on the frame's own screen, then adopt
					it. A pending frame receives nothing until you do — no configuration, no tokens,
					no photos.
				</p>
			</div>

			<div class="pending-list">
				{#each pending as device, index (device.deviceId)}
					<div
						in:settle={{ index, count: pending.length, y: 16 }}
						animate:reorder
					>
						<PendingDeviceCard {device} />
					</div>
				{/each}
			</div>
		</section>
	{/if}

	<section aria-labelledby="adopted-heading">
		<div class="section-head plain">
			<h2 id="adopted-heading">Adopted</h2>
			{#if adopted.length > 0}
				<span class="count">{plural(adopted.length, 'frame')}</span>
			{/if}
		</div>

		{#if fleet.loading}
			<Card padding="none">
				<div class="skeletons">
					{#each [0, 1, 2] as row (row)}
						<div class="skeleton" style:animation-delay="{row * 120}ms"></div>
					{/each}
				</div>
			</Card>
		{:else if adopted.length === 0}
			<Card padding="none">
				<EmptyState title={pending.length > 0 ? 'Nothing adopted yet' : 'No frames yet'}>
					{#if pending.length > 0}
						There is a frame waiting above. Adopt it and it will appear here.
					{:else}
						Point a frame at this Fleet Manager and it turns up here on its own — that is the
						whole enrollment step. Nothing needs to be created in advance.
					{/if}
				</EmptyState>
			</Card>
		{:else}
			<Card padding="none">
				<div class="rows">
					{#each adopted as device, index (device.deviceId)}
						<div in:settle={{ index, count: adopted.length }} animate:reorder>
							<DeviceRow {device} />
						</div>
					{/each}
				</div>
			</Card>
		{/if}
	</section>

	{#if fleet.includeBlocked && blocked.length > 0}
		<section aria-labelledby="blocked-heading" in:rise={{ y: 10 }}>
			<div class="section-head plain">
				<h2 id="blocked-heading">Blocked</h2>
				<span class="count">{plural(blocked.length, 'frame')}</span>
			</div>

			<Card padding="none" tone="sunken">
				<div class="rows">
					{#each blocked as device, index (device.deviceId)}
						<div in:settle={{ index, count: blocked.length }} animate:reorder>
							<DeviceRow {device} />
						</div>
					{/each}
				</div>
			</Card>

			<p class="blocked-note">
				A blocked frame is refused at its next handshake and its product stops. Unblocking
				returns it to the queue above rather than adopting it — trusting a device again is a
				separate, deliberate press.
			</p>
		</section>
	{/if}
</div>

<style>
	.fleet {
		display: grid;
		gap: var(--space-8);
	}

	.head {
		display: flex;
		align-items: flex-end;
		justify-content: space-between;
		gap: var(--space-6);
		flex-wrap: wrap;
	}

	h1 {
		font-size: var(--text-2xl);
	}

	.summary {
		margin-top: var(--space-1);
		font-size: var(--text-sm);
		color: var(--text-2);
	}

	.summary b {
		color: var(--accent);
		font-weight: var(--weight-semibold);
	}

	.head-tools {
		display: flex;
		align-items: center;
		gap: var(--space-3);
	}

	/* The "show blocked" toggle. A real switch rather than a checkbox, because §3.3 makes it
	   the recovery path from a mis-click and it should look like a control, not a filter. */
	.toggle {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		font-size: var(--text-sm);
		color: var(--text-2);
		cursor: pointer;
		user-select: none;
	}

	.toggle input {
		position: absolute;
		opacity: 0;
		pointer-events: none;
	}

	.track {
		width: 36px;
		height: 20px;
		border-radius: var(--radius-pill);
		background: var(--surface-3);
		border: 1px solid var(--line);
		padding: 2px;
		transition:
			background-color var(--dur-base) var(--ease-standard),
			border-color var(--dur-base) var(--ease-standard);
	}

	.knob {
		display: block;
		width: 14px;
		height: 14px;
		border-radius: var(--radius-pill);
		background: var(--text-3);
		transition:
			transform var(--dur-base) var(--ease-spring),
			background-color var(--dur-base) var(--ease-standard);
	}

	.toggle input:checked + .track {
		background: var(--accent-soft);
		border-color: var(--accent-line);
	}

	.toggle input:checked + .track .knob {
		transform: translateX(16px);
		background: var(--accent);
	}

	.toggle input:focus-visible + .track {
		outline: 2px solid var(--focus);
		outline-offset: 2px;
	}

	.banner {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		padding: var(--space-3) var(--space-5);
		border-radius: var(--radius-md);
		border: 1px solid var(--danger-line);
		background: var(--danger-soft);
		color: var(--danger);
		font-size: var(--text-sm);
		flex-wrap: wrap;
	}

	.stale {
		color: var(--text-3);
		font-size: var(--text-xs);
	}

	.section-head {
		margin-bottom: var(--space-4);
	}

	.section-head.plain {
		display: flex;
		align-items: baseline;
		gap: var(--space-3);
	}

	.section-head h2 {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		font-size: var(--text-xl);
	}

	.section-head p {
		margin-top: var(--space-2);
		max-width: 46rem;
		font-size: var(--text-sm);
		color: var(--text-2);
	}

	.count {
		font-size: var(--text-xs);
		color: var(--text-3);
	}

	/* A soft beacon beside the pending heading. Radiating rings, very slow — the visual echo
	   of a frame on a bench with its screen lit, waiting. */
	.beacon {
		position: relative;
		width: 9px;
		height: 9px;
		border-radius: var(--radius-pill);
		background: var(--accent);
		box-shadow: 0 0 12px var(--accent-glow);
	}

	.beacon::after {
		content: '';
		position: absolute;
		inset: 0;
		border-radius: inherit;
		box-shadow: 0 0 0 0 var(--accent);
		animation: radiate 2.4s var(--ease-glide) infinite;
	}

	@keyframes radiate {
		0% {
			box-shadow: 0 0 0 0 color-mix(in oklab, var(--accent) 60%, transparent);
		}
		70%,
		100% {
			box-shadow: 0 0 0 12px transparent;
		}
	}

	.pending-list {
		display: grid;
		gap: var(--space-4);
	}

	.rows {
		display: grid;
		padding: var(--space-2);
		gap: 2px;
	}

	.skeletons {
		display: grid;
		gap: var(--space-2);
		padding: var(--space-4);
	}

	.skeleton {
		height: 52px;
		border-radius: var(--radius-md);
		background: linear-gradient(
			90deg,
			var(--surface-1) 0%,
			var(--surface-3) 50%,
			var(--surface-1) 100%
		);
		background-size: 200% 100%;
		animation: shimmer 1.4s var(--ease-glide) infinite;
	}

	@keyframes shimmer {
		to {
			background-position: -200% 0;
		}
	}

	.blocked-note {
		margin-top: var(--space-3);
		font-size: var(--text-xs);
		color: var(--text-3);
		max-width: 46rem;
	}

	@media (prefers-reduced-motion: reduce) {
		.beacon::after,
		.skeleton {
			animation: none;
		}
		.knob {
			transition: none;
		}
	}
</style>
