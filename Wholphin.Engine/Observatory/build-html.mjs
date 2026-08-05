// Wraps the built bundle into the single self-contained page Jellyfin embeds.
//
// Everything is inlined deliberately. The dashboard injects plugin page HTML straight into its own
// DOM and only evaluates scripts that are part of that document — so a self-contained page is the
// delivery mechanism that is actually guaranteed to run, and it is the one the plugin's existing
// config.html already proves works on this server.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const bundle = readFileSync(resolve(here, 'dist/observatory.js'), 'utf8');
const out = resolve(here, '../Configuration/observatory.html');

// The bundle is injected as a text node, so the only thing that can break out of the <script> is a
// literal "</script>" inside a string. Escaping it keeps the page well-formed.
const safe = bundle.replace(/<\/script>/gi, '<\\/script>');

const html = `<div id="OrcaObservatoryPage" data-role="page" class="page type-interior pluginConfigurationPage">
    <div data-role="content">
        <div class="content-primary">
            <div id="OrcaObservatoryRoot" style="padding:24px;font-family:system-ui,sans-serif;opacity:.7">
                Starting Orca Observatory&hellip;
                <div style="font-size:12px;margin-top:6px">If this message stays, the dashboard script did not run — check the browser console.</div>
            </div>
            <noscript>Orca Observatory needs JavaScript.</noscript>
        </div>
    </div>
    <script type="text/javascript">
${safe}
    </script>
</div>
`;

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, html, 'utf8');
console.log(`observatory.html written (${(html.length / 1024).toFixed(0)} KB)`);
