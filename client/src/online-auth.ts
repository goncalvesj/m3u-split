import {
  BrowserCacheLocation,
  InteractionRequiredAuthError,
  PublicClientApplication,
} from '@azure/msal-browser'
import type { AccountInfo, AuthenticationResult } from '@azure/msal-browser'
import type { OnlineConfiguration } from './online-config'

export interface OnlineAccount {
  id: string
  label: string
}

export interface OnlineCredentials {
  account: OnlineAccount
  accessToken: string
  expiresAt: number
}

export interface OnlineAuth {
  getAccount: () => OnlineAccount | null
  signIn: () => Promise<OnlineCredentials>
  acquireToken: () => Promise<OnlineCredentials>
  signOut: () => Promise<void>
}

const accountSummary = (account: AccountInfo): OnlineAccount => ({
  id: account.homeAccountId,
  label: account.name || account.username || 'Microsoft account',
})

export async function createOnlineAuth(config: OnlineConfiguration): Promise<OnlineAuth> {
  const installed = window.matchMedia?.('(display-mode: standalone)').matches ||
    ('standalone' in navigator && navigator.standalone === true)
  const msal = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: config.redirectUri,
      postLogoutRedirectUri: config.redirectUri,
    },
    cache: { cacheLocation: BrowserCacheLocation.SessionStorage },
    system: { navigatePopups: !installed, popupBridgeTimeout: 120_000 },
  })
  await msal.initialize()
  let pendingLogout: AccountInfo | null = null

  const currentAccount = () => {
    const active = msal.getActiveAccount()
    if (active?.tenantId === config.tenantId) return active
    const accounts = msal.getAllAccounts().filter(account => account.tenantId === config.tenantId)
    return accounts.length === 1 ? accounts[0] : null
  }

  const credentials = (result: AuthenticationResult): OnlineCredentials => {
    if (!result.account || !result.accessToken || !result.expiresOn ||
        result.expiresOn.getTime() <= Date.now()) {
      throw new InteractionRequiredAuthError('interaction_required', 'Microsoft did not return an unexpired API access token.')
    }
    pendingLogout = null
    msal.setActiveAccount(result.account)
    return {
      account: accountSummary(result.account),
      accessToken: result.accessToken,
      expiresAt: result.expiresOn.getTime(),
    }
  }

  return {
    getAccount: () => {
      const account = currentAccount()
      return account ? accountSummary(account) : null
    },
    signIn: () => msal.loginPopup({
      scopes: [config.apiScope],
      prompt: 'select_account',
    }).then(credentials),
    acquireToken: async () => {
      const account = currentAccount()
      if (!account) throw new InteractionRequiredAuthError('login_required', 'Select a Microsoft account first.')
      return credentials(await msal.acquireTokenSilent({ account, scopes: [config.apiScope] }))
    },
    signOut: async () => {
      const account = pendingLogout ?? currentAccount()
      if (!account) throw new Error('No Microsoft account is available to sign out.')
      pendingLogout = account
      await msal.logoutPopup({ account, postLogoutRedirectUri: config.redirectUri })
      pendingLogout = null
      msal.setActiveAccount(null)
    },
  }
}
