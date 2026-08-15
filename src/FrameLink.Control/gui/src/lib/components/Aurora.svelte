<script lang="ts">
	/**
	 * The ambient background.
	 *
	 * Two very low-alpha radial washes — one warm, one cool — drifting across the viewport on
	 * `--dur-ambient`, plus a fine noise grain that stops the large flat areas from banding on
	 * an 8-bit panel. This is the piece of the design language that carries furthest: the
	 * frame's own screens use the same two washes behind the repair narration, so a photo
	 * frame and a fleet console read as one product from across a room.
	 *
	 * It is `aria-hidden`, `pointer-events: none`, fixed, and composited on its own layer, so
	 * it costs one GPU layer and nothing else. Under `prefers-reduced-motion` the drift stops
	 * and the washes simply sit still — the colour is part of the design, the movement is not.
	 */
	interface Props {
		/** Pulls the warm wash forward. Used on the setup and login screens, which are the
		    two moments where the product is introducing itself rather than reporting. */
		intensity?: 'ambient' | 'welcome';
	}

	let { intensity = 'ambient' }: Props = $props();
</script>

<div class="aurora {intensity}" aria-hidden="true">
	<span class="wash warm"></span>
	<span class="wash cool"></span>
	<span class="grain"></span>
</div>

<style>
	.aurora {
		position: fixed;
		inset: 0;
		z-index: -1;
		overflow: hidden;
		pointer-events: none;
		background:
			radial-gradient(120% 90% at 50% -20%, var(--ground-2) 0%, var(--ground) 62%);
	}

	.wash {
		position: absolute;
		border-radius: 50%;
		filter: blur(90px);
		will-change: transform;
	}

	.warm {
		width: 70vmax;
		height: 70vmax;
		top: -32vmax;
		left: -14vmax;
		background: var(--veil-warm);
		animation: drift-warm var(--dur-ambient) var(--ease-glide) infinite alternate;
	}

	.cool {
		width: 60vmax;
		height: 60vmax;
		bottom: -30vmax;
		right: -18vmax;
		background: var(--veil-cool);
		animation: drift-cool calc(var(--dur-ambient) * 1.4) var(--ease-glide) infinite alternate;
	}

	.welcome .warm {
		width: 92vmax;
		height: 92vmax;
		top: -40vmax;
		left: 50%;
		margin-left: -46vmax;
		opacity: 1.6;
	}

	/*
		Grain. A 4×4 repeating micro-gradient rather than a base64 PNG: no asset, no request,
		and it survives a theme switch because it is drawn from the current text colour.
	*/
	.grain {
		position: absolute;
		inset: 0;
		opacity: 0.022;
		background-image:
			repeating-linear-gradient(0deg, currentColor 0 1px, transparent 1px 3px),
			repeating-linear-gradient(90deg, currentColor 0 1px, transparent 1px 3px);
		color: var(--text-1);
		mix-blend-mode: overlay;
	}

	@keyframes drift-warm {
		from {
			transform: translate3d(0, 0, 0) scale(1);
		}
		to {
			transform: translate3d(6vmax, 4vmax, 0) scale(1.12);
		}
	}

	@keyframes drift-cool {
		from {
			transform: translate3d(0, 0, 0) scale(1.08);
		}
		to {
			transform: translate3d(-7vmax, -3vmax, 0) scale(1);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.wash {
			animation: none;
		}
	}
</style>
