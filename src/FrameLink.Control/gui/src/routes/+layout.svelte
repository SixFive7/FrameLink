<script lang="ts">
	/**
	 * The app shell and the one place routing is guarded.
	 *
	 * `session.bootstrap()` decides which of the Fleet Manager's three conditions the whole
	 * app is in (§3.2) and this layout routes on it: unconfigured → /setup, signed out →
	 * /login, signed in → everything else. Individual screens never guard themselves, so
	 * there is exactly one answer to "what happens when the session expires".
	 *
	 * The chrome — header, nav, aurora — only exists for a signed-in operator. The setup and
	 * login screens are full-bleed: they are the product introducing itself, not a page
	 * inside it.
	 */
	import '$lib/design/fonts.css';
	import '$lib/design/tokens.css';
	import '$lib/design/base.css';

	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { theme } from '$lib/design/theme.svelte';
	import { session } from '$lib/stores/session.svelte';
	import { fleet } from '$lib/stores/fleet.svelte';
	import { swap } from '$lib/design/motion';
	import Aurora from '$lib/components/Aurora.svelte';
	import Button from '$lib/components/Button.svelte';
	import Chip from '$lib/components/Chip.svelte';
	import Icon from '$lib/components/Icon.svelte';
	import ThemeToggle from '$lib/components/ThemeToggle.svelte';
	import Toasts from '$lib/components/Toasts.svelte';

	let { children } = $props();

	theme.attach();

	onMount(() => {
		void session.bootstrap();
		return () => fleet.stop();
	});

	/** Where each session phase belongs. */
	const HOME: Record<string, string> = {
		unconfigured: '/setup',
		'signed-out': '/login',
		'signed-in': '/'
	};

	const chrome = $derived(session.phase === 'signed-in');

	$effect(() => {
		const phase = session.phase;
		if (phase === 'starting' || phase === 'unreachable') return;

		const path = page.url.pathname;
		const allowed =
			phase === 'signed-in'
				? path !== '/setup' && path !== '/login'
				: path === HOME[phase];

		if (!allowed) void goto(HOME[phase], { replaceState: true });
	});

	$effect(() => {
		// Polling belongs to the signed-in session, not to a screen: leaving the fleet list for
		// a device page must not stop the fleet from being current when you come back.
		if (session.phase === 'signed-in') fleet.start();
		else fleet.stop();
	});

	const pendingCount = $derived(fleet.pending.length);
</script>

<Aurora intensity={chrome ? 'ambient' : 'welcome'} />

{#if session.phase === 'starting'}
	<div class="boot" aria-live="polite">
		<span class="mark" aria-hidden="true"></span>
		<p>Reaching the Fleet Manager…</p>
	</div>
{:else if session.phase === 'unreachable'}
	<div class="boot">
		<span class="mark bad" aria-hidden="true"><Icon name="alert" size={22} /></span>
		<h1>The Fleet Manager did not answer</h1>
		<p>{session.problem}</p>
		<Button variant="secondary" icon="refresh" onclick={() => void session.bootstrap()}>
			Try again
		</Button>
	</div>
{:else}
	{#if chrome}
		<header class="topbar">
			<div class="bar">
				<a class="brand" href="/">
					<span class="logo" aria-hidden="true"><Icon name="frame" size={19} /></span>
					<span class="wordmark">
						FrameLink
						<small>Fleet Manager</small>
					</span>
				</a>

				<nav aria-label="Sections">
					<a href="/" class:current={page.url.pathname === '/' || page.url.pathname.startsWith('/devices')}>
						<Icon name="frame" size={15} />
						Fleet
						{#if pendingCount > 0}
							<Chip tone="warn" size="sm">{pendingCount}</Chip>
						{/if}
					</a>
					<a href="/settings" class:current={page.url.pathname === '/settings'}>
						<Icon name="sliders" size={15} />
						Fleet settings
					</a>
				</nav>

				<div class="tools">
					<ThemeToggle />
					<Button
						variant="quiet"
						icon="logout"
						title="Sign out"
						aria-label="Sign out"
						onclick={() => void session.signOut()}
					/>
				</div>
			</div>
		</header>
	{/if}

	<main class:chrome>
		{#key page.url.pathname}
			<div class="screen" in:swap={{ direction: 'in' }} out:swap={{ direction: 'out' }}>
				{@render children()}
			</div>
		{/key}
	</main>
{/if}

<Toasts />

<style>
	.boot {
		min-height: 100dvh;
		display: grid;
		place-content: center;
		justify-items: center;
		gap: var(--space-4);
		text-align: center;
		padding: var(--space-8);
		color: var(--text-2);
	}

	.boot h1 {
		color: var(--text-1);
	}

	.boot p {
		max-width: 34rem;
		font-size: var(--text-sm);
	}

	/* The boot mark: a ring that draws itself, so the first second of the app is still the
	   design language rather than a browser spinner. */
	.mark {
		width: 42px;
		height: 42px;
		border-radius: var(--radius-pill);
		border: 2px solid var(--line-strong);
		border-top-color: var(--accent);
		animation: spin 900ms linear infinite;
	}

	.mark.bad {
		display: grid;
		place-items: center;
		border-color: var(--danger-line);
		color: var(--danger);
		animation: none;
	}

	@keyframes spin {
		to {
			transform: rotate(1turn);
		}
	}

	.topbar {
		position: sticky;
		top: 0;
		z-index: 30;
		background: var(--surface-glass);
		backdrop-filter: blur(16px) saturate(150%);
		border-bottom: 1px solid var(--line);
	}

	.bar {
		max-width: var(--width-content);
		margin: 0 auto;
		padding: var(--space-3) var(--space-6);
		display: flex;
		align-items: center;
		gap: var(--space-6);
	}

	.brand {
		display: flex;
		align-items: center;
		gap: var(--space-3);
		text-decoration: none;
		color: var(--text-1);
	}

	.logo {
		display: grid;
		place-items: center;
		width: 34px;
		height: 34px;
		border-radius: var(--radius-sm);
		color: var(--text-on-accent);
		background: linear-gradient(160deg, var(--accent-strong), var(--accent));
		box-shadow: var(--shadow-accent);
		transition: transform var(--dur-base) var(--ease-spring);
	}

	.brand:hover .logo {
		transform: rotate(-6deg) scale(1.06);
	}

	.wordmark {
		display: grid;
		font-weight: var(--weight-bold);
		letter-spacing: var(--track-snug);
		line-height: 1.15;
	}

	.wordmark small {
		font-size: var(--text-2xs);
		font-weight: var(--weight-medium);
		letter-spacing: var(--track-caps);
		text-transform: uppercase;
		color: var(--text-3);
	}

	nav {
		display: flex;
		align-items: center;
		gap: var(--space-1);
		margin-left: auto;
	}

	nav a {
		position: relative;
		display: flex;
		align-items: center;
		gap: var(--space-2);
		padding: var(--space-2) var(--space-4);
		border-radius: var(--radius-sm);
		font-size: var(--text-sm);
		font-weight: var(--weight-medium);
		color: var(--text-2);
		text-decoration: none;
		transition:
			color var(--dur-quick) var(--ease-standard),
			background-color var(--dur-quick) var(--ease-standard);
	}

	nav a:hover {
		color: var(--text-1);
		background: var(--surface-2);
	}

	nav a.current {
		color: var(--text-1);
		background: var(--surface-2);
	}

	/* The active-section underline. Drawn with a pseudo-element so it can grow from the
	   centre rather than appear. */
	nav a.current::after {
		content: '';
		position: absolute;
		left: var(--space-4);
		right: var(--space-4);
		bottom: 2px;
		height: 2px;
		border-radius: var(--radius-pill);
		background: var(--accent);
		animation: underline var(--dur-base) var(--ease-entrance);
	}

	@keyframes underline {
		from {
			transform: scaleX(0);
		}
	}

	.tools {
		display: flex;
		align-items: center;
		gap: var(--space-2);
	}

	main {
		min-height: 100dvh;
	}

	main.chrome {
		min-height: calc(100dvh - 60px);
		max-width: var(--width-content);
		margin: 0 auto;
		padding: var(--space-8) var(--space-6) var(--space-16);
	}

	.screen {
		/* Route transitions overlap by design; without this the outgoing screen pushes the
		   incoming one down for a frame. */
		grid-area: 1 / 1;
	}

	main:not(.chrome) {
		display: grid;
	}

	@media (max-width: 46rem) {
		.bar {
			flex-wrap: wrap;
			gap: var(--space-3);
			padding: var(--space-3) var(--space-4);
		}
		nav {
			order: 3;
			width: 100%;
			margin-left: 0;
		}
		main.chrome {
			padding: var(--space-6) var(--space-4) var(--space-12);
		}
	}

	@media (prefers-reduced-motion: reduce) {
		.mark {
			animation-duration: 2.4s;
		}
		.brand:hover .logo {
			transform: none;
		}
		nav a.current::after {
			animation: none;
		}
	}
</style>
