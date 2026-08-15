<script lang="ts">
	/**
	 * Unknown routes and unhandled client-side errors.
	 *
	 * A designed page rather than SvelteKit's default, because this app is served with an SPA
	 * fallback: `MapFallback` hands *every* unmatched path the shell, so a typo in the address
	 * bar lands here rather than on a server 404, and the honest answer is "there is no such
	 * screen" rather than a stack trace.
	 */
	import { page } from '$app/state';
	import { rise } from '$lib/design/motion';
	import Button from '$lib/components/Button.svelte';
	import Icon from '$lib/components/Icon.svelte';
</script>

<div class="page" in:rise={{ y: 14, duration: 420 }}>
	<span class="glyph" aria-hidden="true"><Icon name="frame" size={26} /></span>
	<p class="code">{page.status}</p>
	<h1>
		{page.status === 404 ? 'There is no screen at this address' : 'Something went wrong here'}
	</h1>
	<p class="detail">
		{page.error?.message ??
			'The Fleet Manager served the app shell, but the app has no route for this path.'}
	</p>
	<Button variant="primary" icon="arrowLeft" href="/">Back to the fleet</Button>
</div>

<style>
	.page {
		display: grid;
		justify-items: center;
		text-align: center;
		gap: var(--space-3);
		padding: var(--space-16) var(--space-6);
	}

	.glyph {
		display: grid;
		place-items: center;
		width: 60px;
		height: 60px;
		border-radius: var(--radius-pill);
		color: var(--text-3);
		background: var(--surface-2);
		border: 1px solid var(--line);
	}

	.code {
		font-family: var(--font-mono);
		font-size: var(--text-2xs);
		letter-spacing: var(--track-caps);
		color: var(--text-3);
	}

	h1 {
		font-size: var(--text-2xl);
	}

	.detail {
		max-width: 34rem;
		font-size: var(--text-sm);
		color: var(--text-2);
		margin-bottom: var(--space-3);
	}
</style>
