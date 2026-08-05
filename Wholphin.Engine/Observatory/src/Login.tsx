import { useState, type FormEvent } from 'react';
import { signIn, type Session } from './session';

/**
 * Sign-in for the standalone Observatory.
 *
 * The password lives in component state for the duration of one submit and is never stored or
 * logged; only the token Jellyfin returns is kept. Sign-in is refused for non-administrators
 * up front, because every endpoint behind this page requires elevation.
 */
export default function Login({ onSignedIn }: { onSignedIn: (session: Session) => void }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const session = await signIn(username, password);
      setPassword('');
      onSignedIn(session);
    } catch (e) {
      setPassword('');
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="obs-login">
      <form className="obs-login-card" onSubmit={submit}>
        <div className="obs-brand">
          <span className="obs-brand-mark" aria-hidden="true">◎</span>
          <div>
            <h1>Orca Observatory</h1>
            <p className="obs-muted obs-small">Sign in with your Jellyfin administrator account</p>
          </div>
        </div>

        {error && <p className="obs-problem" role="alert">{error}</p>}

        <label className="obs-field">
          <span>Username</span>
          <input
            className="obs-input"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoFocus
            required
          />
        </label>

        <label className="obs-field">
          <span>Password</span>
          <input
            className="obs-input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
          />
        </label>

        <button className="obs-btn obs-btn-primary obs-btn-wide" type="submit" disabled={busy || !username}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <p className="obs-muted obs-small obs-center">
          Authenticates against this Jellyfin server. Your password is not stored — only the access
          token it returns, which you can revoke from Dashboard → Devices.
        </p>
      </form>
    </div>
  );
}
