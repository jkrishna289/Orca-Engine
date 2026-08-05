// Wraps the built bundle into the single self-contained page the plugin serves at
// GET /OrcaEngine/Observatory/App.
//
// Everything is inlined so the plugin ships one embedded resource and the page has no second
// request to fail. It is a complete HTML document, not a fragment: the Observatory is served
// directly by the plugin rather than injected into Jellyfin's dashboard.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const bundle = readFileSync(resolve(here, 'dist/observatory.js'), 'utf8');
const out = resolve(here, '../Configuration/observatory-app.html');

// The bundle is injected as a text node, so the only thing that can break out of the <script> is a
// literal "</script>" inside a string. Escaping it keeps the page well-formed.
const safe = bundle.replace(/<\/script>/gi, '<\\/script>');

const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<meta name="referrer" content="same-origin">
<title>Orca Observatory</title>
</head>
<body>
<div id="OrcaObservatoryRoot">Starting Orca Observatory&hellip;</div>
<noscript>Orca Observatory needs JavaScript.</noscript>
<script type="text/javascript">
${safe}
</script>
</body>
</html>
`;

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, html, 'utf8');
console.log(`observatory-app.html written (${(html.length / 1024).toFixed(0)} KB)`);
