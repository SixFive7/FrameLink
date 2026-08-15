<script lang="ts">
	/**
	 * What a screen shows when there is nothing to show.
	 *
	 * Deliberately not a shrug. An empty fleet is the *normal* first state of this product —
	 * the operator has a container running and a frame in their hands — so the empty state's
	 * job is to say what happens next, not to apologise for being blank.
	 */
	import type { Snippet } from 'svelte';
	import Icon, { type IconName } from './Icon.svelte';

	interface Props {
		icon?: IconName;
		title: string;
		children?: Snippet;
		action?: Snippet;
	}

	let { icon = 'frame', title, children, action }: Props = $props();
</script>

<div class="empty">
	<span class="halo" aria-hidden="true">
		<Icon name={icon} size={26} />
	</span>
	<h3>{title}</h3>
	{#if children}<p>{@render children()}</p>{/if}
	{#if action}<div class="action">{@render action()}</div>{/if}
</div>

<style>
	.empty {
		display: grid;
		justify-items: center;
		text-align: center;
		gap: var(--space-3);
		padding: var(--space-12) var(--space-6);
		color: var(--text-2);
	}

	.halo {
		display: grid;
		place-items: center;
		width: 60px;
		height: 60px;
		border-radius: var(--radius-pill);
		color: var(--text-3);
		background: var(--surface-2);
		border: 1px solid var(--line);
		box-shadow: inset 0 1px 0 var(--line-strong);
		margin-bottom: var(--space-2);
	}

	h3 {
		color: var(--text-1);
	}

	p {
		max-width: 34rem;
		font-size: var(--text-sm);
		line-height: var(--leading-normal);
	}

	.action {
		margin-top: var(--space-2);
	}
</style>
