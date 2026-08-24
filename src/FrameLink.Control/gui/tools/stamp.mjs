#!/usr/bin/env node
/**
 * Writes `../../gui-build.stamp`: a SHA-256 manifest of everything the GUI build reads and
 * everything it produces.
 *
 * The stamp exists to answer one question loudly — **is the committed `wwwroot` the output of
 * the committed `gui/` sources?** `wwwroot` is a build artifact that is deliberately committed
 * (§3.1: one container, no loose asset trees beside the binary), which means it can silently
 * fall behind the source the first time somebody edits a `.svelte` file and forgets to rebuild.
 * Nothing about a stale bundle looks broken; it just serves last week's console.
 *
 * `GuiFreshnessTests` in the C# suite reads this file back and re-hashes every path in it, so
 * a stale or hand-edited `wwwroot` is a red test rather than a discovery in production.
 *
 * The format is deliberately a flat list rather than one combined hash:
 *
 *   #  comment lines
 *   <sha256-hex>  <path relative to src/FrameLink.Control, forward slashes>
 *
 * Two reasons. There is no "combine the hashes" algorithm to reimplement identically on the C#
 * side — only `sha256(file bytes)`, which is a primitive and not a choice. And `git diff` on
 * this file names exactly which GUI files a commit touched, which is the review question
 * anyway.
 *
 * What is hashed is CONTENT, not the bytes on disk. This file used to hash raw bytes on the
 * reasoning that `.gitattributes` pins `* text=auto eol=lf`, so a working tree is LF on every
 * OS and the bytes on disk are the bytes in history. The premise is false in practice: git
 * only controls what it writes at checkout, and any tool that later rewrites a file with
 * "native" line endings — an editor, a formatter, a script calling Python's `write_text` —
 * leaves CRLF behind, with `git status` still clean because the CRLF still cleans to the same
 * LF blob. The stamp then described one person's disk: it was green for whoever ran the build
 * and red for everyone else, naming GUI files they had never touched. That is precisely the
 * shape of the stale-bundle failure this whole mechanism exists to make visible, so the check
 * was spending its credibility on an answer about the reader's checkout.
 *
 * So each file is hashed with CRLF collapsed to LF first, and a lone CR left alone — which is
 * not an approximation of git's `text` filter, it is that filter's rule. The bytes hashed are
 * therefore the bytes in the object database, and the stamp is a claim about the committed
 * content rather than about a working tree.
 *
 * Binary files are hashed byte for byte, decided by git's own heuristic: a NUL byte in the
 * first BINARY_SNIFF_LENGTH bytes. That guard earns its place — one of the three woff2 fonts
 * in `wwwroot` really does contain the byte pair CR LF, and normalising a font would throw
 * away a difference the stamp exists to see.
 *
 * `GuiFreshnessTests.Sha256OfContent` is the same two rules in C#. They have to stay the same
 * two rules: this writes the manifest, that verifies it, and neither can read the other.
 */

import { createHash } from 'node:crypto';
import { readFile, readdir, stat, writeFile } from 'node:fs/promises';
import { join, posix, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const GUI = resolve(fileURLToPath(new URL('..', import.meta.url)));
const PROJECT = resolve(GUI, '..');
const STAMP = join(PROJECT, 'gui-build.stamp');

/**
 * What the build reads. Everything else under `gui/` — `mock/`, `.vscode/`, `node_modules/`,
 * `.svelte-kit/` — is deliberately absent: a file that cannot change the output must not be
 * able to fail the freshness check, or the check trains people to regenerate it reflexively.
 */
const SOURCE_DIRECTORIES = ['src', 'static', 'tools'];
const SOURCE_FILES = ['package.json', 'package-lock.json', 'vite.config.ts', 'tsconfig.json', '.npmrc'];

/** What the build produces, and what `UseStaticFiles()` serves. */
const OUTPUT_DIRECTORY = resolve(PROJECT, 'wwwroot');

async function walk(directory) {
	const found = [];
	let entries;
	try {
		entries = await readdir(directory, { withFileTypes: true });
	} catch {
		return found;
	}

	for (const entry of entries) {
		const full = join(directory, entry.name);
		if (entry.isDirectory()) {
			found.push(...(await walk(full)));
		} else if (entry.isFile()) {
			found.push(full);
		}
	}
	return found;
}

/** How many leading bytes decide "is this text?" — git's own binary heuristic. */
const BINARY_SNIFF_LENGTH = 8000;

/**
 * The bytes to hash: a text file's content with CRLF collapsed to LF, a binary file's bytes
 * untouched. Mirrors `GuiFreshnessTests.Sha256OfContent` byte for byte.
 */
function contentOf(bytes) {
	if (bytes.subarray(0, BINARY_SNIFF_LENGTH).includes(0)) return bytes;

	const kept = Buffer.allocUnsafe(bytes.length);
	let length = 0;
	for (let i = 0; i < bytes.length; i++) {
		if (bytes[i] === 0x0d && bytes[i + 1] === 0x0a) continue;
		kept[length++] = bytes[i];
	}
	return kept.subarray(0, length);
}

async function hashOf(path) {
	return createHash('sha256')
		.update(contentOf(await readFile(path)))
		.digest('hex');
}

const files = [];
for (const directory of SOURCE_DIRECTORIES) {
	files.push(...(await walk(join(GUI, directory))));
}
for (const file of SOURCE_FILES) {
	const full = join(GUI, file);
	try {
		if ((await stat(full)).isFile()) files.push(full);
	} catch {
		// A missing optional file (.npmrc) is simply absent from the manifest, and its absence
		// is itself recorded — the C# side compares the whole list, not a subset.
	}
}
files.push(...(await walk(OUTPUT_DIRECTORY)));

const lines = await Promise.all(
	files.map(async (file) => `${await hashOf(file)}  ${posix.normalize(relative(PROJECT, file).split(/[\\/]/).join('/'))}`)
);

lines.sort((a, b) => (a.slice(66) < b.slice(66) ? -1 : a.slice(66) > b.slice(66) ? 1 : 0));

await writeFile(
	STAMP,
	[
		'# Generated by gui/tools/stamp.mjs. Do not edit by hand.',
		'#',
		'# SHA-256 of every file the Fleet Manager GUI build reads, and of every file it wrote',
		'# into wwwroot. Regenerated by the FrameLinkBuildGui target in FrameLink.Control.csproj',
		'# and verified by GuiFreshnessTests, which is what turns a stale committed bundle from',
		'# a silent wrong-console-in-production into a red test.',
		'#',
		'# To refresh it: dotnet build src/FrameLink.Control  (or: npm run build in gui/)',
		'',
		...lines,
		''
	].join('\n'),
	'utf8'
);

console.log(`gui-build.stamp: ${lines.length} files`);
