<script lang="ts">
	/**
	 * The toast stack.
	 *
	 * Bottom-right, `aria-live="polite"`, one column, newest at the bottom. Each toast rises
	 * in on the entrance curve and leaves sideways on the exit curve, so arriving and leaving
	 * never look like the same event. A thin bar drains across the bottom for the toast's
	 * lifetime — not decoration: it is the only cue that a message is about to vanish, and it
	 * pauses on hover so a long error can actually be read.
	 */
	import { toasts } from '$lib/stores/toast.svelte';
	import { rise, slip } from '$lib/design/motion';
	import Icon from './Icon.svelte';
</script>

<div class="stack" aria-live="polite" aria-atomic="false">
	{#each toasts.items as toast (toast.id)}
		<div
			class="toast {toast.tone}"
			in:rise={{ y: 14 }}
			out:slip={{ x: 28 }}
			style:--ttl="{toast.ttl}ms"
			role={toast.tone === 'danger' ? 'alert' : 'status'}
		>
			<span class="glyph">
				<Icon
					name={toast.tone === 'ok' ? 'check' : toast.tone === 'danger' ? 'alert' : 'info'}
					size={16}
				/>
			</span>

			<div class="body">
				<p class="title">{toast.title}</p>
				{#if toast.detail}<p class="detail">{toast.detail}</p>{/if}
			</div>

			<button class="close" onclick={() => toasts.dismiss(toast.id)} aria-label="Dismiss">
				<Icon name="x" size={14} />
			</button>

			<span class="drain" aria-hidden="true"></span>
		</div>
	{/each}
</div>

<style>
	.stack {
		position: fixed;
		z-index: 60;
		right: var(--space-5);
		bottom: var(--space-5);
		display: grid;
		gap: var(--space-3);
		width: min(24rem, calc(100vw - var(--space-8)));
		pointer-events: none;
	}

	.toast {
		position: relative;
		overflow: hidden;
		display: grid;
		grid-template-columns: auto 1fr auto;
		align-items: start;
		gap: var(--space-3);
		padding: var(--space-3) var(--space-4);
		border-radius: var(--radius-md);
		border: 1px solid var(--toast-line);
		background: var(--surface-glass);
		backdrop-filter: blur(14px) saturate(150%);
		box-shadow: var(--shadow-4);
		pointer-events: auto;
	}

	.ok {
		--toast-ink: var(--ok);
		--toast-line: var(--ok-line);
	}
	.danger {
		--toast-ink: var(--danger);
		--toast-line: var(--danger-line);
	}
	.info {
		--toast-ink: var(--info);
		--toast-line: var(--info-line);
	}

	.glyph {
		display: grid;
		place-items: center;
		width: 24px;
		height: 24px;
		border-radius: var(--radius-pill);
		color: var(--toast-ink);
		background: color-mix(in oklab, var(--toast-ink) 16%, transparent);
	}

	.body {
		min-width: 0;
	}

	.title {
		font-size: var(--text-sm);
		font-weight: var(--weight-semibold);
		line-height: var(--leading-snug);
	}

	.detail {
		margin-top: 2px;
		font-size: var(--text-xs);
		color: var(--text-2);
		line-height: var(--leading-snug);
		overflow-wrap: anywhere;
	}

	.close {
		color: var(--text-3);
		padding: var(--space-1);
		border-radius: var(--radius-xs);
		transition: color var(--dur-quick) var(--ease-standard);
	}
	.close:hover {
		color: var(--text-1);
	}

	.drain {
		position: absolute;
		left: 0;
		bottom: 0;
		height: 2px;
		width: 100%;
		transform-origin: left;
		background: var(--toast-ink);
		opacity: 0.6;
		animation: drain var(--ttl) linear forwards;
	}

	.toast:hover .drain {
		animation-play-state: paused;
	}

	@keyframes drain {
		from {
			transform: scaleX(1);
		}
		to {
			transform: scaleX(0);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.drain {
			animation: none;
			transform: scaleX(0.001);
		}
	}
</style>
