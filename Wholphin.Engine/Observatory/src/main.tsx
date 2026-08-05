import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import App from './App';
import css from './styles.css?raw';

// Jellyfin injects plugin pages straight into its own SPA DOM — there is no iframe, so nothing
// isolates this app's CSS from the dashboard's or vice versa. A shadow root is the isolation the
// iframe would have provided. Custom properties still inherit through it, so the host theme's
// colours remain available.
function mount(host: HTMLElement): Root {
  const shadow = host.shadowRoot ?? host.attachShadow({ mode: 'open' });
  shadow.innerHTML = '';

  const style = document.createElement('style');
  style.textContent = css;
  shadow.appendChild(style);

  const mountPoint = document.createElement('div');
  shadow.appendChild(mountPoint);

  const root = createRoot(mountPoint);
  root.render(<StrictMode><App /></StrictMode>);
  return root;
}

/** Attempts to mount. Returns false only while the page element is not in the document yet. */
function start(): boolean {
  const host = document.querySelector<HTMLElement>('#OrcaObservatoryRoot');
  if (!host) return false;
  if (host.dataset.mounted === 'true') return true;
  host.dataset.mounted = 'true';

  try {
    const root = mount(host);

    // 'viewdestroy' is the dashboard's only teardown signal. Without unmounting here, every
    // navigation away and back would leave another live React tree behind — still polling, still
    // holding an open event stream.
    const page = host.closest('[data-role="page"]') ?? host;
    page.addEventListener('viewdestroy', function teardown() {
      page.removeEventListener('viewdestroy', teardown);
      host.dataset.mounted = 'false';
      root.unmount();
    });
  } catch (error) {
    // A diagnostics tool that fails by showing nothing is worse than useless — you cannot tell a
    // crash from a script that never ran. Say which one it was, on the page.
    host.dataset.mounted = 'false';
    host.textContent = `Orca Observatory failed to start: ${String(error)}`;
    console.error('[OrcaObservatory] mount failed', error);
  }

  return true;
}

// The dashboard is a long-lived single-page app: DOMContentLoaded fired long before anyone
// navigated here, so waiting on it would wait forever. The page element is also not reliably in
// the document at the instant this script is evaluated, so poll briefly instead of assuming.
if (!start()) {
  let attempts = 0;
  const timer = window.setInterval(() => {
    if (start() || ++attempts > 100) window.clearInterval(timer);
  }, 100);
}
