<script lang="ts">
	/**
	 * A device fingerprint, rendered to be matched against a frame's own screen.
	 *
	 * §3.3: "a pending frame displays its short fingerprint and hardware serial on screen, so
	 * the operator can tell which row is which frame on the bench." That is the entire brief
	 * for this component, and it drives every decision in it:
	 *
	 *  - the four groups are separated by *space*, not by the hyphens the id arrives with, so
	 *    the eye chunks them the way it would read them aloud;
	 *  - mono, tabular, with `--track-code` tracking, at up to `--text-4xl` on a pending card
	 *    — 48px is legible across a bench, which is the actual distance being designed for;
	 *  - the whole thing is one click to copy, because the other half of matching is pasting
	 *    the id into a note or a search;
	 *  - groups fade in one after another on the stagger step, which is a flourish, but a
	 *    functional one: it draws the eye along the reading order.
	 */
	import { fingerprintGroups } from '$lib/format';
	import { stagger } from '$lib/design/motion';
	import Icon from './Icon.svelte';

	interface Props {
		deviceId: string;
		size?: 'sm' | 'md' | 'lg' | 'bench';
		/** Adds a copy affordance. On by default; off inside a link or a button. */
		copyable?: boolean;
		/** Animates the groups in. Only worth it once per screen. */
		animate?: boolean;
	}

	let { deviceId, size = 'md', copyable = true, animate = false }: Props = $props();

	const groups = $derived(fingerprintGroups(deviceId));
	let copied = $state(false);
	let timer: ReturnType<typeof setTimeout> | undefined;

	async function copy() {
		try {
			await navigator.clipboard.writeText(deviceId);
			copied = true;
			clearTimeout(timer);
			timer = setTimeout(() => (copied = false), 1600);
		} catch {
			/* clipboard denied (insecure origin, or the operator said no): the text is still
			   selectable, so there is nothing to report and nothing to fix */
		}
	}
</script>

{#snippet groupsMarkup()}
	{#each groups as group, index (index)}
		<span
			class="group"
			class:animate
			style:animation-delay="{animate ? stagger(index, groups.length) : 0}ms">{group}</span
		>
	{/each}
{/snippet}

{#if copyable}
	<button
		class="fingerprint {size} interactive"
		class:copied
		type="button"
		onclick={copy}
		title="Copy {deviceId}"
	>
		{@render groupsMarkup()}
		<span class="badge" aria-hidden="true">
			<Icon name={copied ? 'check' : 'copy'} size={size === 'bench' ? 18 : 13} />
		</span>
		<span class="sr-only">{copied ? 'Copied' : 'Copy device id'}</span>
	</button>
{:else}
	<span class="fingerprint {size}">{@render groupsMarkup()}</span>
{/if}

<style>
	.fingerprint {
		display: inline-flex;
		align-items: baseline;
		flex-wrap: wrap;
		gap: 0.55em;
		font-family: var(--font-mono);
		font-variant-numeric: tabular-nums;
		font-weight: var(--weight-medium);
		letter-spacing: var(--track-code);
		color: var(--text-1);
		background: none;
		border: 0;
		padding: 0;
		text-align: left;
	}

	.interactive {
		cursor: pointer;
		position: relative;
		border-radius: var(--radius-xs);
		transition: color var(--dur-quick) var(--ease-standard);
	}
	.interactive:hover {
		color: var(--accent);
	}

	.sm {
		font-size: var(--text-xs);
		gap: 0.45em;
	}
	.md {
		font-size: var(--text-sm);
	}
	.lg {
		font-size: var(--text-xl);
		font-weight: var(--weight-semibold);
	}

	/* The bench size. Everything about this step exists so a frame two metres away and a row
	   on a laptop can be compared without walking over. */
	.bench {
		font-size: clamp(var(--text-2xl), 5.2vw, var(--text-4xl));
		font-weight: var(--weight-bold);
		letter-spacing: 0.03em;
		gap: 0.4em;
		line-height: 1.05;
	}

	/* A group never breaks internally. Four characters are the unit an operator reads and
	   compares, so the line may wrap between groups but never inside one. */
	.group {
		display: inline-block;
		white-space: nowrap;
	}

	.animate {
		animation: group-in var(--dur-base) var(--ease-entrance) backwards;
	}

	@keyframes group-in {
		from {
			opacity: 0;
			transform: translateY(0.22em);
		}
	}

	.badge {
		display: inline-flex;
		align-items: center;
		align-self: center;
		color: var(--text-3);
		opacity: 0;
		transform: translateX(-4px);
		transition:
			opacity var(--dur-quick) var(--ease-standard),
			transform var(--dur-quick) var(--ease-entrance),
			color var(--dur-quick) var(--ease-standard);
	}

	.interactive:hover .badge,
	.interactive:focus-visible .badge,
	.copied .badge {
		opacity: 1;
		transform: translateX(0);
	}

	.copied .badge {
		color: var(--ok);
	}

	@media (prefers-reduced-motion: reduce) {
		.animate {
			animation: none;
		}
	}
</style>
