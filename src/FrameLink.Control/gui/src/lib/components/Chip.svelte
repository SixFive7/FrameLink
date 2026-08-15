<script lang="ts">
	/**
	 * A small labelled pill. One component for every status, tag and count in the app, so a
	 * "Blocked" chip and an "Overridden" chip are visibly the same kind of object.
	 *
	 * The `tone` maps onto the semantic colour roles, never onto a primitive ramp, so a chip
	 * is correct in both themes without knowing which one it is in.
	 */
	import type { Snippet } from 'svelte';
	import Icon, { type IconName } from './Icon.svelte';

	interface Props {
		tone?: 'ok' | 'warn' | 'danger' | 'info' | 'tech' | 'muted';
		icon?: IconName;
		/** A live dot instead of an icon — used by presence, where the dot itself is the signal. */
		dot?: boolean;
		/** Adds a slow breathing halo to the dot. Only for `Online`. */
		pulse?: boolean;
		size?: 'sm' | 'md';
		title?: string;
		children: Snippet;
	}

	let {
		tone = 'muted',
		icon,
		dot = false,
		pulse = false,
		size = 'md',
		title,
		children
	}: Props = $props();
</script>

<span class="chip {tone} {size}" {title}>
	{#if dot}
		<span class="dot" class:pulse aria-hidden="true"></span>
	{:else if icon}
		<Icon name={icon} size={size === 'sm' ? 12 : 13} />
	{/if}
	{@render children()}
</span>

<style>
	.chip {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		border-radius: var(--radius-pill);
		border: 1px solid var(--chip-line);
		background: var(--chip-fill);
		color: var(--chip-ink);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-wide);
		white-space: nowrap;
	}

	.sm {
		padding: 1px var(--space-2);
		font-size: var(--text-2xs);
	}
	.md {
		padding: 2px var(--space-3);
		font-size: var(--text-xs);
	}

	.ok {
		--chip-ink: var(--ok);
		--chip-fill: var(--ok-soft);
		--chip-line: var(--ok-line);
	}
	.warn {
		--chip-ink: var(--accent);
		--chip-fill: var(--accent-soft);
		--chip-line: var(--accent-line);
	}
	.danger {
		--chip-ink: var(--danger);
		--chip-fill: var(--danger-soft);
		--chip-line: var(--danger-line);
	}
	.info {
		--chip-ink: var(--info);
		--chip-fill: var(--info-soft);
		--chip-line: var(--info-line);
	}
	.tech {
		--chip-ink: var(--tech);
		--chip-fill: var(--tech-soft);
		--chip-line: var(--tech-line);
	}
	.muted {
		--chip-ink: var(--text-2);
		--chip-fill: var(--surface-2);
		--chip-line: var(--line);
	}

	.dot {
		width: 7px;
		height: 7px;
		border-radius: var(--radius-pill);
		background: currentColor;
		flex: none;
	}

	/* The breathing halo on an online device. Slow enough to be peripheral — the point is
	   that a room full of green dots looks alive, not that any one of them is animating. */
	.pulse {
		box-shadow: 0 0 0 0 currentColor;
		animation: breathe 2.6s var(--ease-glide) infinite;
	}

	@keyframes breathe {
		0% {
			box-shadow: 0 0 0 0 color-mix(in oklab, currentColor 55%, transparent);
		}
		70%,
		100% {
			box-shadow: 0 0 0 7px transparent;
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.pulse {
			animation: none;
		}
	}
</style>
