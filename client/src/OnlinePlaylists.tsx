import { useCallback, useEffect, useRef, useState } from 'react'
import type { OnlineAccount, OnlineAuth, OnlineCredentials } from './online-auth'
import { fetchOnlineSession, OnlineApiError } from './online-api'
import { readOnlineConfiguration } from './online-config'
import OnlineImport from './OnlineImport'
import OnlineProfiles from './OnlineProfiles'

type OnlineState =
  | { kind: 'initializing' | 'guest' | 'signing-in' | 'checking' | 'signing-out'; message: string }
  | { kind: 'verified'; message: string; expiresAt: number }
  | { kind: 'error'; message: string; reason: 'setup' | 'sign-in' | 'sign-out' | 'access' | 'reauth' }

function authFailure(error: unknown): { message: string; reason: 'sign-in' | 'reauth' } {
  const code = typeof error === 'object' && error !== null && 'errorCode' in error ? error.errorCode : ''
  if (code === 'user_cancelled') {
    return { reason: 'sign-in', message: 'Microsoft sign-in was cancelled. Your local playlist is unchanged.' }
  }
  if (['popup_window_error', 'empty_window_error', 'block_nested_popups'].includes(String(code))) {
    return {
      reason: 'sign-in',
      message: 'The sign-in popup was blocked. Allow popups for this site and retry. If the installed app cannot open it, use the site in a regular browser window. Your loaded local playlist remains here.',
    }
  }
  if (['interaction_required', 'consent_required', 'login_required', 'no_account_error'].includes(String(code))) {
    return { reason: 'reauth', message: 'Microsoft needs you to sign in again or grant the API permission. Online access has not been verified.' }
  }
  if (code === 'interaction_in_progress') {
    return { reason: 'sign-in', message: 'A Microsoft sign-in window is already open. Complete or close it, then retry.' }
  }
  return {
    reason: 'sign-in',
    message: 'Microsoft authentication did not complete. Check your connection, popup permissions and app configuration, then retry. Your local playlist is unchanged.',
  }
}

export default function OnlinePlaylists() {
  const [configuration] = useState(() => readOnlineConfiguration(import.meta.env, window.location))
  const config = configuration.kind === 'configured' ? configuration.config : null
  const [auth, setAuth] = useState<OnlineAuth | null>(null)
  const [account, setAccount] = useState<OnlineAccount | null>(null)
  const [state, setState] = useState<OnlineState>({ kind: 'initializing', message: 'Preparing Microsoft sign-in...' })
  const [setupAttempt, setSetupAttempt] = useState(0)
  const initialization = useRef<Promise<OnlineAuth> | null>(null)
  const operation = useRef(0)
  const request = useRef<AbortController | null>(null)

  const cancelPendingAccess = useCallback(() => {
    request.current?.abort()
    return ++operation.current
  }, [])

  const checkAccess = useCallback(async (client: OnlineAuth, supplied?: OnlineCredentials) => {
    if (!config) return
    const current = cancelPendingAccess()
    const controller = new AbortController()
    request.current = controller
    setState({ kind: 'checking', message: 'Checking access with the online API...' })
    try {
      const credentials = supplied ?? await client.acquireToken()
      if (current !== operation.current) return
      setAccount(credentials.account)
      const identity = await fetchOnlineSession(config, credentials.accessToken, controller.signal)
      if (current !== operation.current) return
      const expiresAt = Math.min(credentials.expiresAt, identity.expiresAt)
      if (expiresAt <= Date.now()) {
        setState({ kind: 'error', reason: 'reauth', message: 'Online access verification expired. Check access again to renew it.' })
      } else {
        setState({ kind: 'verified', expiresAt, message: 'Online access verified by the API.' })
      }
    } catch (error) {
      if (current !== operation.current) return
      if (error instanceof OnlineApiError) {
        setState({ kind: 'error', reason: error.kind === 'unauthorized' ? 'reauth' : 'access', message: error.message })
      } else {
        setState({ kind: 'error', ...authFailure(error) })
      }
    }
  }, [config, cancelPendingAccess])

  // URL import calls fail like the session check when the API or Microsoft rejects this session.
  const handleImportAccessError = useCallback((error: unknown) => {
    setState(current => {
      if (current.kind !== 'verified') return current
      return error instanceof OnlineApiError
        ? { kind: 'error', reason: error.kind === 'unauthorized' ? 'reauth' : 'access', message: error.message }
        : { kind: 'error', reason: 'reauth', message: 'Microsoft needs you to sign in again or grant the API permission. Online access has not been verified.' }
    })
  }, [])

  useEffect(() => {
    if (!config) return
    let active = true
    initialization.current ??= import('./online-auth').then(module => module.createOnlineAuth(config))
    void initialization.current.then(client => {
      if (!active) return
      const cached = client.getAccount()
      setAuth(client)
      setAccount(cached)
      if (cached) {
        void checkAccess(client)
      } else {
        setState({ kind: 'guest', message: 'You are not signed in. Local files do not need an account.' })
      }
    }).catch(() => {
      if (active) setState({
        kind: 'error', reason: 'setup',
        message: 'Microsoft sign-in could not initialize. Check your connection and browser storage permissions, then retry. Local files still work.',
      })
    })
    return () => {
      active = false
      cancelPendingAccess()
    }
  }, [config, setupAttempt, checkAccess, cancelPendingAccess])

  useEffect(() => {
    if (state.kind !== 'verified') return
    const expire = () => setState({
      kind: 'error', reason: 'reauth',
      message: 'Online access verification expired. Check access again to renew it.',
    })
    const checkExpiry = () => { if (Date.now() >= state.expiresAt) expire() }
    const offline = () => setState({
      kind: 'error', reason: 'access',
      message: 'You are offline. Online access must be checked again when connected. Local files still work.',
    })
    const timer = window.setTimeout(expire, Math.min(Math.max(state.expiresAt - Date.now(), 0), 2_147_483_647))
    window.addEventListener('focus', checkExpiry)
    window.addEventListener('offline', offline)
    document.addEventListener('visibilitychange', checkExpiry)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('focus', checkExpiry)
      window.removeEventListener('offline', offline)
      document.removeEventListener('visibilitychange', checkExpiry)
    }
  }, [state])

  const signIn = async () => {
    if (!auth) return
    const current = cancelPendingAccess()
    setState({ kind: 'signing-in', message: 'Complete Microsoft sign-in in the popup window...' })
    try {
      const credentials = await auth.signIn()
      if (current !== operation.current) return
      setAccount(credentials.account)
      await checkAccess(auth, credentials)
    } catch (error) {
      if (current === operation.current) {
        setAccount(auth.getAccount())
        setState({ kind: 'error', ...authFailure(error) })
      }
    }
  }

  const signOut = async () => {
    if (!auth) return
    const current = cancelPendingAccess()
    setState({ kind: 'signing-out', message: 'Signing out in the Microsoft popup. Online access is no longer verified...' })
    try {
      await auth.signOut()
      if (current !== operation.current) return
      setAccount(null)
      setState({ kind: 'guest', message: 'Signed out of this app. Your local file, selection and favourites are unchanged.' })
    } catch {
      if (current !== operation.current) return
      setAccount(auth.getAccount())
      setState({
        kind: 'error', reason: 'sign-out',
        message: 'Microsoft sign-out did not finish. The app cache may already be cleared, but your Microsoft session may still be active. Allow popups and retry sign-out. Your local playlist is unchanged.',
      })
    }
  }

  const busy = ['initializing', 'signing-in', 'checking', 'signing-out'].includes(state.kind)
  const signOutFailed = state.kind === 'error' && state.reason === 'sign-out'

  return (
    <section className="card online-playlists" aria-labelledby="online-playlists-title">
      <div className="card-header">
        <h2 id="online-playlists-title">Online playlists</h2>
        <span className="badge">Optional</span>
      </div>
      <p>This separate, optional area lets an allowed Microsoft account import a provider playlist by URL on the server. Local files above always work without an account.</p>
      {configuration.kind === 'unconfigured' ? (
        <p>Online sign-in is not configured for this build. You can still process and download local playlists.</p>
      ) : configuration.kind === 'invalid' ? (
        <p role="alert" className="online-error">{configuration.message}</p>
      ) : (
        <>
          {account && <p>Microsoft account: <strong>{account.label}</strong>. Sign-in alone does not grant online API access.</p>}
          <p
            role={state.kind === 'error' ? 'alert' : 'status'}
            aria-label="Online access"
            aria-atomic="true"
            className={state.kind === 'error' ? 'online-error' : 'online-status'}
          >{state.message}</p>
          {state.kind === 'verified' && (
            <p>URL import, saved profiles with one-click refresh and secret TV links for Smart IPTV are available.</p>
          )}
          <div className="online-actions">
            {!auth && state.kind === 'error' ? (
              <button type="button" className="btn-small" onClick={() => {
                initialization.current = null
                setState({ kind: 'initializing', message: 'Preparing Microsoft sign-in...' })
                setSetupAttempt(attempt => attempt + 1)
              }}>Retry sign-in setup</button>
            ) : (
              <>
                <button type="button" className="btn-small" disabled={!auth || busy} onClick={() => void signIn()}>
                  {account ? 'Sign in again / change account' : 'Sign in with Microsoft'}
                </button>
                {account && (
                  <button type="button" className="btn-small" disabled={busy} onClick={() => auth && void checkAccess(auth)}>
                    Check online access
                  </button>
                )}
                {(account || signOutFailed) && (
                  <button type="button" className="btn-small" disabled={busy} onClick={() => void signOut()}>
                    {signOutFailed ? 'Retry Microsoft sign-out' : 'Sign out'}
                  </button>
                )}
              </>
            )}
          </div>
          <p className="online-hint">Sign-in and sign-out use a popup, never a full-page redirect. Local files and browser favourites are not uploaded or cleared.</p>
          {auth && account && config && (
            <OnlineImport
              key={account.id}
              config={config}
              auth={auth}
              active={state.kind === 'verified'}
              onAccessError={handleImportAccessError}
            />
          )}
          {auth && account && config && (
            <OnlineProfiles
              key={`profiles:${account.id}`}
              config={config}
              auth={auth}
              active={state.kind === 'verified'}
              onAccessError={handleImportAccessError}
            />
          )}
        </>
      )}
    </section>
  )
}
