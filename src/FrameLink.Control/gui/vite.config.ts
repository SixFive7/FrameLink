import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

import pkg from './package.json';

/**
 * The Fleet Manager GUI build.
 *
 * Since SvelteKit 2.62 the `sveltekit()` plugin takes the kit configuration inline and
 * `svelte.config.js` is ignored when it does — so this file is the whole build definition.
 * Do not add a `svelte.config.js`; the plugin warns and discards it.
 *
 * The output lands directly in `../wwwroot`, which ASP.NET serves with `UseStaticFiles()`
 * (version2.md §3.1 — never `MapStaticAssets()`, which serves empty 200s under the slim
 * builder). `wwwroot` is committed: the Fleet Manager ships as one container and §2.1/§3.1
 * want no loose asset trees beside the binary.
 */
export default defineConfig({
	plugins: [
		sveltekit({
			version: {
				// SvelteKit defaults this to a build timestamp, and inlines it into the entry
				// chunks — so every build produced different content hashes, a different
				// index.html and a different version.json for identical sources. With wwwroot
				// committed (see gui-build.stamp) that made an ordinary `dotnet build` dirty
				// the working tree with pure churn, which trains people to discard the bundle
				// reflexively and defeats the freshness check it exists to feed.
				//
				// Pinning it to the package version makes the build byte-reproducible and keeps
				// the field meaningful: it moves when the GUI is versioned, not when the clock is.
				name: pkg.version
			},

			compilerOptions: {
				// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
				runes: ({ filename }) =>
					filename.split(/[/\\]/).includes('node_modules') ? undefined : true
			},

			adapter: adapter({
				pages: '../wwwroot',
				assets: '../wwwroot',

				// SPA mode. Routing is client-side and the server does not know the routes —
				// `GuiEndpoints.MapFallback` hands every unmatched path this file.
				fallback: 'index.html',

				// The container serves through Kestrel, which does not negotiate pre-compressed
				// siblings by itself. Shipping .gz/.br files would only fatten the image.
				precompress: false,

				// Nothing is prerendered (the whole app is behind an operator session), so the
				// strict check has nothing to check.
				strict: false
			})
		})
	],

	server: {
		// `npm run dev` against a real `fl-control`, or against `npm run mock` on 5199.
		proxy: {
			'/api': { target: 'http://127.0.0.1:5199', changeOrigin: false },
			'/healthz': { target: 'http://127.0.0.1:5199', changeOrigin: false }
		}
	},

	build: {
		// The console is loaded once and cached. One request for the shell beats six.
		assetsInlineLimit: 2048,
		chunkSizeWarningLimit: 900
	}
});
