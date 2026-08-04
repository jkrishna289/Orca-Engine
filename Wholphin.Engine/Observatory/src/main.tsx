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

function start() {
  const host = document.querySelector<HTMLElement>('#OrcaObservatoryRoot');
  if (!host || host.dataset.mounted === 'true') return;
  host.dataset.mounted = 'true';

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
}

// The page markup may already be in the DOM by the time this script runs, or not quite yet.
if (document.querySelector('#OrcaObservatoryRoot')) {
  start();
} else {
  document.addEventListener('DOMContentLoaded', start, { once: true });
}
