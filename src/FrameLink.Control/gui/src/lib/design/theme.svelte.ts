/**
 * FrameLink design system — theme mode.
 *
 * Three states, not two: `system` follows the operating system and is the default, because
 * an operator who has already told their laptop they want light mode should not have to tell
 * this app as well. `light` and `dark` pin it.
 *
 * The resolved theme is written to `document.documentElement.dataset.theme`, which is the
 * selector `tokens.css` switches on. A copy of this resolution runs inline in `app.html`
 * before first paint — without it the page renders dark and then snaps to light, which is
 * the one flash of unstyled content a two-theme design cannot hide behind a transition.
 */

import { browser } from '$app/environment';

export type ThemeMode = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

const STORAGE_KEY = 'framelink.theme';

class ThemeState {
	/** What the operator chose. */
	mode = $state<ThemeMode>('system');

	/** What the system currently reports, tracked live so `system` follows a mid-session change. */
	#system = $state<ResolvedTheme>('dark');

	/** What is actually on screen. */
	get resolved(): ResolvedTheme {
		return this.mode === 'system' ? this.#system : this.mode;
	}

	constructor() {
		if (!browser) return;

		const stored = localStorage.getItem(STORAGE_KEY);
		if (stored === 'light' || stored === 'dark' || stored === 'system') {
			this.mode = stored;
		}

		const query = matchMedia('(prefers-color-scheme: light)');
		this.#system = query.matches ? 'light' : 'dark';
		query.addEventListener('change', (event) => {
			this.#system = event.matches ? 'light' : 'dark';
		});
	}

	/** Applies the resolved theme to the document. Called once from the root layout. */
	attach() {
		$effect(() => {
			const resolved = this.resolved;
			document.documentElement.dataset.theme = resolved;
			document
				.querySelector('meta[name="theme-color"]')
				?.setAttribute('content', resolved === 'dark' ? '#080a10' : '#f2f4f9');
		});
	}

	set(mode: ThemeMode) {
		this.mode = mode;
		if (browser) {
			// `system` is stored too. Absence means "never chose"; storing it means "chose to
			// follow the system", and the two are different intents.
			localStorage.setItem(STORAGE_KEY, mode);
		}
	}

	/** Cycles system → light → dark → system, which is what the header control does. */
	cycle() {
		this.set(this.mode === 'system' ? 'light' : this.mode === 'light' ? 'dark' : 'system');
	}
}

export const theme = new ThemeState();
