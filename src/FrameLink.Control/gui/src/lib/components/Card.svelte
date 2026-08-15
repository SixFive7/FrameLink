<script lang="ts">
	/**
	 * The surface everything sits on.
	 *
	 * Three levels of emphasis, all built from the same recipe: a translucent fill, a hairline
	 * border, and a shadow step. `accent` adds a warm wash and a lit top edge — reserved for
	 * the one thing on a screen that wants the operator's hands, which in this app is almost
	 * always a pending device.
	 */
	import type { Snippet } from 'svelte';

	interface Props {
		tone?: 'plain' | 'accent' | 'danger' | 'sunken';
		/** Lifts on hover. Only for cards that are themselves a link or a target. */
		interactive?: boolean;
		padding?: 'none' | 'sm' | 'md' | 'lg';
		class?: string;
		children: Snippet;
	}

	let {
		tone = 'plain',
		interactive = false,
		padding = 'md',
		class: className = '',
		children
	}: Props = $props();
</script>

<div class="card {tone} pad-{padding} {className}" class:interactive>
	{@render children()}
</div>

<style>
	.card {
		position: relative;
		border-radius: var(--radius-lg);
		border: 1px solid var(--line);
		background: var(--surface-1);
		box-shadow: var(--shadow-2);
		transition:
			transform var(--dur-base) var(--ease-standard),
			border-color var(--dur-base) var(--ease-standard),
			box-shadow var(--dur-base) var(--ease-standard),
			background-color var(--dur-base) var(--ease-standard);
	}

	.pad-none {
		padding: 0;
	}
	.pad-sm {
		padding: var(--space-4);
	}
	.pad-md {
		padding: var(--space-5) var(--space-6);
	}
	.pad-lg {
		padding: var(--space-8);
	}

	.accent {
		border-color: var(--accent-line);
		background:
			linear-gradient(180deg, var(--accent-soft), transparent 55%), var(--surface-2);
		box-shadow:
			var(--shadow-3),
			0 0 60px -22px var(--accent-glow);
	}

	/* The lit top edge. One pixel of brand light along the leading edge of the card, which is
	   what makes an accent card read as illuminated rather than merely tinted. */
	.accent::before {
		content: '';
		position: absolute;
		inset: 0 0 auto;
		height: 1px;
		border-radius: inherit;
		background: linear-gradient(
			90deg,
			transparent,
			var(--accent) 22%,
			var(--accent-strong) 50%,
			var(--accent) 78%,
			transparent
		);
		opacity: 0.85;
	}

	.danger {
		border-color: var(--danger-line);
		background: linear-gradient(180deg, var(--danger-soft), transparent 55%), var(--surface-1);
	}

	.sunken {
		background: var(--surface-sunken);
		box-shadow: none;
	}

	.interactive:hover {
		transform: translateY(-2px);
		border-color: var(--line-strong);
		box-shadow: var(--shadow-3);
	}

	@media (prefers-reduced-motion: reduce) {
		.interactive:hover {
			transform: none;
		}
	}
</style>
