import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import css from './styles.css?raw';

// A standalone document served by the plugin itself. None of the machinery the embedded version
// needed applies here: no shadow root (there is no host page to isolate from), no waiting on
// Jellyfin's view container, no 'viewdestroy' teardown. The page owns the document.
const style = document.createElement('style');
style.textContent = css;
document.head.appendChild(style);

const host = document.getElementById('OrcaObservatoryRoot');

if (!host) {
  document.body.textContent = 'Orca Observatory: mount point missing.';
} else {
  try {
    createRoot(host).render(<StrictMode><App /></StrictMode>);
  } catch (error) {
    // A diagnostics page that fails silently cannot be diagnosed.
    host.textContent = `Orca Observatory failed to start: ${String(error)}`;
    console.error('[OrcaObservatory] mount failed', error);
  }
}
