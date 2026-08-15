/**
 * SPA mode.
 *
 * `ssr = false` because there is no Node process to render on — the output is a static
 * bundle served by `UseStaticFiles()` out of an ASP.NET container, and every byte of data the
 * app shows comes from `/api` behind an operator session.
 *
 * `prerender = false` for the same reason: there is nothing to prerender that is not gated,
 * and `adapter-static`'s `fallback: 'index.html'` is what makes client-side routing work
 * against a server that knows none of these paths (`GuiEndpoints.MapFallback`).
 */
export const ssr = false;
export const prerender = false;
export const trailingSlash = 'never';
