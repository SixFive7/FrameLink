<script lang="ts">
	/**
	 * A copyable block of literal text — the Compose fragment on the setup screen, and any
	 * other place where the reader's next action is "paste this somewhere else".
	 *
	 * The copy button confirms in place and reverts after a moment, matching the behaviour of
	 * the server-rendered `SetupPage` so the two versions of that screen behave identically.
	 */
	import Icon from './Icon.svelte';

	interface Props {
		code: string;
		label?: string;
	}

	let { code, label }: Props = $props();

	let copied = $state(false);
	let timer: ReturnType<typeof setTimeout> | undefined;

	async function copy() {
		try {
			await navigator.clipboard.writeText(code);
			copied = true;
			clearTimeout(timer);
			timer = setTimeout(() => (copied = false), 1600);
		} catch {
			/* clipboard unavailable on an insecure origin; the text is still selectable */
		}
	}
</script>

<figure class="block">
	{#if label}<figcaption>{label}</figcaption>{/if}
	<button class="copy" class:copied type="button" onclick={copy}>
		<Icon name={copied ? 'check' : 'copy'} size={13} />
		{copied ? 'Copied' : 'Copy'}
	</button>
	<pre><code>{code}</code></pre>
</figure>

<style>
	.block {
		position: relative;
		margin: 0;
	}

	figcaption {
		font-size: var(--text-xs);
		font-weight: var(--weight-semibold);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
		margin-bottom: var(--space-2);
	}

	pre {
		margin: 0;
		padding: var(--space-4) var(--space-5);
		overflow-x: auto;
		border-radius: var(--radius-md);
		border: 1px solid var(--line);
		background: var(--surface-sunken);
		color: var(--text-2);
		font-size: var(--text-xs);
		line-height: var(--leading-loose);
		tab-size: 2;
	}

	.copy {
		position: absolute;
		top: var(--space-3);
		right: var(--space-3);
		z-index: 1;
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		padding: var(--space-1) var(--space-3);
		border-radius: var(--radius-xs);
		border: 1px solid var(--line);
		background: var(--surface-2);
		color: var(--text-2);
		font-size: var(--text-2xs);
		font-weight: var(--weight-semibold);
		opacity: 0.75;
		transition:
			opacity var(--dur-quick) var(--ease-standard),
			color var(--dur-quick) var(--ease-standard),
			border-color var(--dur-quick) var(--ease-standard),
			transform var(--dur-instant) var(--ease-standard);
	}

	.block:hover .copy {
		opacity: 1;
	}

	.copy:hover {
		color: var(--text-1);
		border-color: var(--line-strong);
	}

	.copy:active {
		transform: scale(0.96);
	}

	.copied {
		opacity: 1;
		color: var(--ok);
		border-color: var(--ok-line);
	}

	/* The caption sits above the block, so when there is one the button drops below it. */
	figcaption + .copy {
		top: calc(var(--space-3) + 1.6em);
	}
</style>
