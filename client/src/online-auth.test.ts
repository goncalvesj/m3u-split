import type { AccountInfo, AuthenticationResult, Configuration } from '@azure/msal-browser'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createOnlineAuth } from './online-auth'
import type { OnlineConfiguration } from './online-config'

const sdk = vi.hoisted(() => ({
  initialize: vi.fn(),
  getActiveAccount: vi.fn<() => AccountInfo | null>(),
  getAllAccounts: vi.fn<() => AccountInfo[]>(),
  setActiveAccount: vi.fn(),
  loginPopup: vi.fn(),
  acquireTokenSilent: vi.fn(),
  logoutPopup: vi.fn(),
  configuration: vi.fn<(config: Configuration) => void>(),
}))

vi.mock('@azure/msal-browser', async importOriginal => {
  const original = await importOriginal<typeof import('@azure/msal-browser')>()
  return {
    ...original,
    PublicClientApplication: vi.fn(function (configuration: Configuration) {
      sdk.configuration(configuration)
      return sdk
    }),
  }
})

const config: OnlineConfiguration = {
  tenantId: '11111111-1111-4111-8111-111111111111',
  clientId: '22222222-2222-4222-8222-222222222222',
  apiScope: 'api://33333333-3333-4333-8333-333333333333/access_as_user',
  apiBaseUrl: 'https://function.example.invalid/api/',
  redirectUri: 'https://goncalvesj.github.io/m3u-split/auth.html',
}
const account: AccountInfo = {
  homeAccountId: 'home-account',
  localAccountId: '44444444-4444-4444-8444-444444444444',
  tenantId: config.tenantId,
  environment: 'login.microsoftonline.com',
  username: 'display@example.invalid',
  name: 'Allowed person',
}
const result = (): AuthenticationResult => ({
  account,
  authority: `https://login.microsoftonline.com/${config.tenantId}`,
  uniqueId: account.localAccountId,
  tenantId: account.tenantId,
  scopes: [config.apiScope],
  idToken: 'id-token-must-never-reach-the-api',
  idTokenClaims: {},
  accessToken: 'api-access-token',
  fromCache: false,
  expiresOn: new Date(Date.now() + 60_000),
  tokenType: 'Bearer',
  correlationId: 'test-correlation',
})

beforeEach(() => {
  vi.resetAllMocks()
  sdk.initialize.mockResolvedValue(undefined)
  sdk.getActiveAccount.mockReturnValue(null)
  sdk.getAllAccounts.mockReturnValue([])
  sdk.loginPopup.mockResolvedValue(result())
  sdk.acquireTokenSilent.mockResolvedValue(result())
  sdk.logoutPopup.mockResolvedValue(undefined)
})

describe('MSAL popup adapter', () => {
  it('initializes a tenant-specific public client without secrets or shared localStorage', async () => {
    const auth = await createOnlineAuth(config)
    expect(sdk.initialize).toHaveBeenCalledOnce()
    expect(sdk.configuration).toHaveBeenCalledWith({
      auth: {
        clientId: config.clientId,
        authority: `https://login.microsoftonline.com/${config.tenantId}`,
        redirectUri: config.redirectUri,
        postLogoutRedirectUri: config.redirectUri,
      },
      cache: { cacheLocation: 'sessionStorage' },
      system: { navigatePopups: true, popupBridgeTimeout: 120_000 },
    })
    expect(auth.getAccount()).toBeNull()
    expect(sdk.loginPopup).not.toHaveBeenCalled()
  })

  it('opens the provider directly for installed PWAs that cannot navigate about:blank popups', async () => {
    vi.stubGlobal('matchMedia', () => ({ matches: true }))
    await createOnlineAuth(config)
    expect(sdk.configuration.mock.calls[0][0].system?.navigatePopups).toBe(false)
  })

  it('requests the API scope using an account-picker popup and returns only its access token', async () => {
    const auth = await createOnlineAuth(config)
    const credentials = await auth.signIn()
    expect(sdk.loginPopup).toHaveBeenCalledWith({ scopes: [config.apiScope], prompt: 'select_account' })
    expect(credentials).toEqual({
      account: { id: account.homeAccountId, label: account.name },
      accessToken: 'api-access-token',
      expiresAt: expect.any(Number),
    })
    expect(sdk.setActiveAccount).toHaveBeenCalledWith(account)
    expect(credentials).not.toHaveProperty('idToken')
  })

  it('silently renews the API token for the explicitly selected account', async () => {
    sdk.getActiveAccount.mockReturnValue(account)
    const auth = await createOnlineAuth(config)
    await auth.acquireToken()
    expect(sdk.acquireTokenSilent).toHaveBeenCalledWith({ account, scopes: [config.apiScope] })
    expect(sdk.loginPopup).not.toHaveBeenCalled()
  })

  it('never opens a popup automatically when silent renewal needs interaction', async () => {
    sdk.getActiveAccount.mockReturnValue(account)
    sdk.acquireTokenSilent.mockRejectedValue({ errorCode: 'interaction_required' })
    const auth = await createOnlineAuth(config)
    await expect(auth.acquireToken()).rejects.toMatchObject({ errorCode: 'interaction_required' })
    expect(sdk.loginPopup).not.toHaveBeenCalled()
  })

  it('does not arbitrarily choose from multiple cached accounts or another tenant', async () => {
    sdk.getAllAccounts.mockReturnValue([account, { ...account, homeAccountId: 'another-account' }])
    const auth = await createOnlineAuth(config)
    expect(auth.getAccount()).toBeNull()
    await expect(auth.acquireToken()).rejects.toMatchObject({ errorCode: 'login_required' })
    sdk.getAllAccounts.mockReturnValue([{ ...account, tenantId: 'other-tenant' }])
    expect(auth.getAccount()).toBeNull()
    expect(sdk.acquireTokenSilent).not.toHaveBeenCalled()
  })

  it.each(['no-token', 'expired', 'no-expiry'])('refuses unusable API credentials: %s', async variant => {
    const response = result()
    if (variant === 'no-token') response.accessToken = ''
    if (variant === 'expired') response.expiresOn = new Date(0)
    if (variant === 'no-expiry') response.expiresOn = null
    sdk.loginPopup.mockResolvedValue(response)
    const auth = await createOnlineAuth(config)
    await expect(auth.signIn()).rejects.toMatchObject({ errorCode: 'interaction_required' })
  })

  it('signs out only the selected account through a popup without navigating the main window', async () => {
    sdk.getActiveAccount.mockReturnValue(account)
    window.localStorage.setItem('m3u-split:favourites', 'preserve')
    window.localStorage.setItem('unrelated', 'preserve')
    const auth = await createOnlineAuth(config)
    await auth.signOut()
    expect(sdk.logoutPopup).toHaveBeenCalledWith({ account, postLogoutRedirectUri: config.redirectUri })
    expect(sdk.setActiveAccount).toHaveBeenCalledWith(null)
    expect(window.localStorage.getItem('m3u-split:favourites')).toBe('preserve')
    expect(window.localStorage.getItem('unrelated')).toBe('preserve')
  })

  it('can retry server sign-out after MSAL cleared its account cache but the popup failed', async () => {
    sdk.getActiveAccount.mockReturnValue(account)
    const auth = await createOnlineAuth(config)
    sdk.logoutPopup.mockRejectedValueOnce({ errorCode: 'user_cancelled' })
    await expect(auth.signOut()).rejects.toMatchObject({ errorCode: 'user_cancelled' })
    sdk.getActiveAccount.mockReturnValue(null)
    expect(auth.getAccount()).toBeNull()
    await auth.signOut()
    expect(sdk.logoutPopup).toHaveBeenLastCalledWith({ account, postLogoutRedirectUri: config.redirectUri })
  })

  it('signs out the new account when the user signs in again instead of retrying a failed logout', async () => {
    sdk.getActiveAccount.mockReturnValue(account)
    const auth = await createOnlineAuth(config)
    sdk.logoutPopup.mockRejectedValueOnce({ errorCode: 'user_cancelled' })
    await expect(auth.signOut()).rejects.toMatchObject({ errorCode: 'user_cancelled' })

    const newAccount = { ...account, homeAccountId: 'new-account' }
    sdk.loginPopup.mockResolvedValue({ ...result(), account: newAccount })
    await auth.signIn()
    sdk.getActiveAccount.mockReturnValue(newAccount)
    await auth.signOut()

    expect(sdk.logoutPopup).toHaveBeenLastCalledWith({ account: newAccount, postLogoutRedirectUri: config.redirectUri })
  })

  it('reports initialization failure rather than pretending to have an anonymous MSAL client', async () => {
    sdk.initialize.mockRejectedValue(new Error('Storage unavailable'))
    await expect(createOnlineAuth(config)).rejects.toThrow('Storage unavailable')
  })
})
