<script lang="ts">
	/**
	 * System → light → dark → system.
	 *
	 * A segmented control rather than a single toggle, because `system` is a real third state
	 * and hiding it behind a long-press is how an operator ends up with a console that
	 * disagrees with their laptop at sunset. The active pill slides between segments on the
	 * standard curve — one shared element moving, not three fading.
	 */
	import { theme, type ThemeMode } from '$lib/design/theme.svelte';
	import Icon, { type IconName } from './Icon.svelte';

	const OPTIONS: Array<{ mode: ThemeMode; icon: IconName; label: string }> = [
		{ mode: 'system', icon: 'monitor', label: 'Follow system' },
		{ mode: 'light', icon: 'sun', label: 'Light' },
		{ mode: 'dark', icon: 'moon', label: 'Dark' }
	];

	const index = $derived(OPTIONS.findIndex((option) => option.mode === theme.mode));
</script>

<div class="toggle" role="radiogroup" aria-label="Colour theme">
	<span class="thumb" style:--index={index} aria-hidden="true"></span>
	{#each OPTIONS as option (option.mode)}
		<button
			type="button"
			role="radio"
			aria-checked={theme.mode === option.mode}
			class:active={theme.mode === option.mode}
			title={option.label}
			onclick={() => theme.set(option.mode)}
		>
			<Icon name={option.icon} size={15} label={option.label} />
		</button>
	{/each}
</div>

<style>
	.toggle {
		position: relative;
		display: inline-grid;
		grid-auto-flow: column;
		gap: 2px;
		padding: 3px;
		border-radius: var(--radius-pill);
		border: 1px solid var(--line);
		background: var(--surface-1);
	}

	.thumb {
		position: absolute;
		top: 3px;
		left: 3px;
		width: 30px;
		height: 26px;
		border-radius: var(--radius-pill);
		background: var(--surface-3);
		box-shadow: var(--shadow-1);
		transform: translateX(calc(var(--index) * 32px));
		transition: transform var(--dur-base) var(--ease-standard);
	}

	button {
		position: relative;
		z-index: 1;
		display: grid;
		place-items: center;
		width: 30px;
		height: 26px;
		border-radius: var(--radius-pill);
		color: var(--text-3);
		transition: color var(--dur-quick) var(--ease-standard);
	}

	button:hover {
		color: var(--text-2);
	}

	button.active {
		color: var(--accent);
	}

	@media (prefers-reduced-motion: reduce) {
		.thumb {
			transition: none;
		}
	}
</style>
