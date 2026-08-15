<script lang="ts">
	/**
	 * The only button in the app.
	 *
	 * Five variants, one motion behaviour. The press is the `press` primitive from the design
	 * system: a 3% scale-down on `:active` over `--dur-instant`, applied to every variant, so
	 * a control that is doing something always feels like it is doing something. Hover adds a
	 * 1px lift and a shadow step — small enough not to reflow, large enough to notice.
	 *
	 * `busy` is not a separate variant. Any button can be busy, and while it is, it keeps its
	 * width (the label stays in the flow at zero opacity) so a row of buttons does not
	 * reshuffle the instant one is pressed.
	 */
	import type { Snippet } from 'svelte';
	import Icon, { type IconName } from './Icon.svelte';

	interface Props {
		variant?: 'primary' | 'secondary' | 'ghost' | 'danger' | 'quiet';
		size?: 'sm' | 'md' | 'lg';
		icon?: IconName;
		iconAfter?: IconName;
		busy?: boolean;
		disabled?: boolean;
		type?: 'button' | 'submit';
		href?: string;
		title?: string;
		full?: boolean;
		'aria-label'?: string;
		onclick?: (event: MouseEvent) => void;
		children?: Snippet;
	}

	let {
		variant = 'secondary',
		size = 'md',
		icon,
		iconAfter,
		busy = false,
		disabled = false,
		type = 'button',
		href,
		title,
		full = false,
		'aria-label': ariaLabel,
		onclick,
		children
	}: Props = $props();

	const iconSize = $derived(size === 'lg' ? 20 : size === 'sm' ? 15 : 17);
</script>

{#snippet inner()}
	{#if icon}<Icon name={icon} size={iconSize} />{/if}
	{#if children}<span class="label">{@render children()}</span>{/if}
	{#if iconAfter}<Icon name={iconAfter} size={iconSize} />{/if}
	{#if busy}<span class="spinner" aria-hidden="true"></span>{/if}
{/snippet}

{#if href}
	<a
		class="btn {variant} {size}"
		class:full
		class:busy
		{href}
		{title}
		aria-label={ariaLabel}
		aria-busy={busy}
	>
		{@render inner()}
	</a>
{:else}
	<button
		class="btn {variant} {size}"
		class:full
		class:busy
		{type}
		{title}
		disabled={disabled || busy}
		aria-label={ariaLabel}
		aria-busy={busy}
		{onclick}
	>
		{@render inner()}
	</button>
{/if}

<style>
	.btn {
		position: relative;
		display: inline-flex;
		align-items: center;
		justify-content: center;
		gap: var(--space-2);
		border-radius: var(--radius-sm);
		border: 1px solid transparent;
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-normal);
		text-decoration: none;
		white-space: nowrap;
		cursor: pointer;
		isolation: isolate;
		transition:
			transform var(--dur-instant) var(--ease-standard),
			background-color var(--dur-quick) var(--ease-standard),
			border-color var(--dur-quick) var(--ease-standard),
			box-shadow var(--dur-quick) var(--ease-standard),
			color var(--dur-quick) var(--ease-standard);
	}

	.btn:disabled {
		cursor: not-allowed;
		opacity: 0.55;
	}

	/* A disabled primary drops the gradient entirely rather than fading it. A half-opacity
	   amber gradient on a near-black ground reads as muddy brown, which looks like a rendering
	   fault rather than a disabled control. */
	.primary:disabled {
		background: var(--surface-3);
		color: var(--text-3);
		box-shadow: none;
		opacity: 1;
	}

	/* The press. Every variant, every size, one rule. */
	.btn:not(:disabled):active {
		transform: scale(0.97);
		transition-duration: var(--dur-instant);
	}

	.btn:not(:disabled):hover {
		transform: translateY(-1px);
	}

	.sm {
		padding: var(--space-1) var(--space-3);
		font-size: var(--text-xs);
		min-height: 30px;
	}
	.md {
		padding: var(--space-2) var(--space-4);
		font-size: var(--text-sm);
		min-height: 38px;
	}
	.lg {
		padding: var(--space-3) var(--space-6);
		font-size: var(--text-md);
		min-height: 48px;
		border-radius: var(--radius-md);
	}

	.full {
		width: 100%;
	}

	/* Primary carries the brand and its glow. Used once per screen at most: the adopt action,
	   the sign-in action, the save action. */
	.primary {
		background: linear-gradient(180deg, var(--accent-strong), var(--accent));
		color: var(--text-on-accent);
		box-shadow: var(--shadow-accent);
	}
	.primary:not(:disabled):hover {
		box-shadow:
			var(--shadow-accent),
			0 0 0 4px var(--accent-soft);
	}

	.secondary {
		background: var(--surface-2);
		border-color: var(--line);
		color: var(--text-1);
		box-shadow: var(--shadow-1);
	}
	.secondary:not(:disabled):hover {
		background: var(--surface-3);
		border-color: var(--line-strong);
		box-shadow: var(--shadow-2);
	}

	.ghost {
		background: transparent;
		border-color: var(--line);
		color: var(--text-2);
	}
	.ghost:not(:disabled):hover {
		background: var(--surface-1);
		border-color: var(--line-strong);
		color: var(--text-1);
	}

	/* Danger is outlined at rest and fills on hover. A destructive action should look
	   available, not eager. */
	.danger {
		background: transparent;
		border-color: var(--danger-line);
		color: var(--danger);
	}
	.danger:not(:disabled):hover {
		background: var(--danger);
		border-color: var(--danger);
		color: var(--danger-contrast);
	}

	.quiet {
		background: transparent;
		color: var(--text-3);
		padding-inline: var(--space-2);
	}
	.quiet:not(:disabled):hover {
		background: var(--surface-2);
		color: var(--text-1);
	}

	.busy .label,
	.busy :global(.icon) {
		opacity: 0;
	}

	.spinner {
		position: absolute;
		inset: 50% auto auto 50%;
		width: 1em;
		height: 1em;
		margin: -0.5em 0 0 -0.5em;
		border-radius: var(--radius-pill);
		border: 2px solid currentColor;
		border-top-color: transparent;
		animation: spin 620ms linear infinite;
	}

	@keyframes spin {
		to {
			transform: rotate(1turn);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.btn:not(:disabled):hover,
		.btn:not(:disabled):active {
			transform: none;
		}
		.spinner {
			animation-duration: 1.6s;
		}
	}
</style>
