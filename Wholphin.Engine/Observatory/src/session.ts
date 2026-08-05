/**
 * Authentication for the standalone Observatory.
 *
 * Served outside the Jellyfin dashboard, there is no `ApiClient` global and no borrowed session, so
 * the page authenticates against Jellyfin itself and keeps the returned access token.
 *
 * The password is used for exactly one request and never stored, never logged, and never placed in
 * a URL. Only the token Jellyfin issues is persisted, and it is revocable from
 * Dashboard - Devices at any time.
 */

const TOKEN_KEY = 'orca.observatory.token';
const USER_KEY = 'orca.observatory.user';
const DEVICE_KEY = 'orca.observatory.device';

export interface Session {
  token: string;
  userName: string;
}

/** A stable per-browser device id, so Jellyfin lists this as one device rather than a new one per login. */
function deviceId(): string {
  let id = localStorage.getItem(DEVICE_KEY);
  if (!id) {
    id = (crypto.randomUUID?.() ?? String(Math.random()).slice(2)).replace(/-/g, '');
    localStorage.setItem(DEVICE_KEY, id);
  }
  return id;
}

/** Jellyfin requires this on every request, including the login itself — without it auth 400s. */
function clientHeader(): string {
  return `MediaBrowser Client="Orca Observatory", Device="Browser", DeviceId="${deviceId()}", Version="1.0.0"`;
}

export function currentSession(): Session | null {
  const token = localStorage.getItem(TOKEN_KEY);
  if (!token) return null;
  return { token, userName: localStorage.getItem(USER_KEY) ?? '' };
}

export function signOut(): void {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
}

/**
 * Exchanges credentials for an access token.
 *
 * @throws when the credentials are rejected, or when the account is not an administrator — every
 * Observatory endpoint requires elevation, so a standard user would otherwise sign in successfully
 * and then be met with a wall of 403s.
 */
export async function signIn(username: string, password: string): Promise<Session> {
  const response = await fetch(url('Users/AuthenticateByName'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: clientHeader() },
    // Sent once, held only for the duration of this call.
    body: JSON.stringify({ Username: username, Pw: password }),
  });

  if (response.status === 401) throw new Error('Incorrect username or password.');
  if (!response.ok) throw new Error(`Sign-in failed (${response.status}).`);

  const body = (await response.json()) as {
    AccessToken?: string;
    User?: { Name?: string; Policy?: { IsAdministrator?: boolean } };
  };

  const token = body.AccessToken;
  if (!token) throw new Error('Jellyfin did not return an access token.');

  if (!body.User?.Policy?.IsAdministrator) {
    throw new Error('That account is not an administrator. The Observatory requires admin access.');
  }

  const session: Session = { token, userName: body.User?.Name ?? username };
  localStorage.setItem(TOKEN_KEY, session.token);
  localStorage.setItem(USER_KEY, session.userName);
  return session;
}

/** Builds a same-origin URL. The page is served by the plugin, so the server is wherever this loaded from. */
export function url(path: string, params?: Record<string, unknown>): string {
  const built = new URL(path.replace(/^\//, ''), `${window.location.origin}${basePath()}`);
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value !== undefined && value !== null) built.searchParams.set(key, String(value));
  }
  return built.toString();
}

/**
 * The server root this page was served from.
 * The page lives at `<base>/OrcaEngine/Observatory/App`, so everything before that is the base —
 * which keeps this working when Jellyfin is hosted under a path prefix behind a reverse proxy.
 */
function basePath(): string {
  const marker = '/OrcaEngine/Observatory/App';
  const path = window.location.pathname;
  const index = path.indexOf(marker);
  return index > 0 ? `${path.slice(0, index)}/` : '/';
}

/** Fired on the window when a stored token is rejected, so the app can return to sign-in. */
export const SESSION_EXPIRED_EVENT = 'orca-session-expired';

/** Raised when the stored token is no longer valid, so the UI can drop back to the sign-in screen. */
export class SessionExpired extends Error {
  constructor() {
    super('Your session has expired. Please sign in again.');
  }
}

/** Authenticated fetch. Clears the session on 401 so a stale token cannot wedge the page. */
export async function authFetch(path: string, params?: Record<string, unknown>, init?: RequestInit): Promise<Response> {
  const session = currentSession();
  if (!session) throw new SessionExpired();

  const response = await fetch(url(path, params), {
    ...init,
    headers: {
      ...(init?.headers ?? {}),
      Authorization: `${clientHeader()}, Token="${session.token}"`,
      'X-Emby-Token': session.token,
    },
  });

  if (response.status === 401) {
    signOut();
    // Any of a dozen polls may be the one that discovers the token died. Announcing it once lets
    // the app drop back to sign-in from wherever it happened, rather than each caller guessing.
    window.dispatchEvent(new CustomEvent(SESSION_EXPIRED_EVENT));
    throw new SessionExpired();
  }

  return response;
}
