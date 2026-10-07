export interface OnlineConfiguration {
  tenantId: string
  clientId: string
  apiScope: string
  apiBaseUrl: string
  redirectUri: string
}

interface OnlineEnvironment {
  BASE_URL: string
  VITE_ONLINE_PROFILES_ENABLED?: string
  VITE_ENTRA_TENANT_ID?: string
  VITE_ENTRA_SPA_CLIENT_ID?: string
  VITE_ENTRA_API_SCOPE?: string
  VITE_ONLINE_API_BASE_URL?: string
}

export type OnlineConfigurationResult =
  | { kind: 'unconfigured' }
  | { kind: 'invalid'; message: string }
  | { kind: 'configured'; config: OnlineConfiguration }

export const isIdentityId = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000'

export const isOnlineProfilesEnabled = (environment: OnlineEnvironment) =>
  environment.VITE_ONLINE_PROFILES_ENABLED?.trim().toLowerCase() === 'true'

export function readOnlineConfiguration(
  environment: OnlineEnvironment,
  location: Pick<Location, 'origin'>,
): OnlineConfigurationResult {
  if (!isOnlineProfilesEnabled(environment)) return { kind: 'unconfigured' }
  const tenantId = environment.VITE_ENTRA_TENANT_ID?.trim() ?? ''
  const clientId = environment.VITE_ENTRA_SPA_CLIENT_ID?.trim() ?? ''
  const apiScope = environment.VITE_ENTRA_API_SCOPE?.trim() ?? ''
  const apiBase = environment.VITE_ONLINE_API_BASE_URL?.trim() ?? ''
  if (![tenantId, clientId, apiScope, apiBase].some(Boolean)) return { kind: 'unconfigured' }

  const invalid = (message: string): OnlineConfigurationResult => ({ kind: 'invalid', message })
  if (!isIdentityId(tenantId) || !isIdentityId(clientId)) {
    return invalid('Online sign-in needs valid tenant and SPA application IDs. Local files still work.')
  }

  const scopeParts = /^api:\/\/([^/]+)\/([a-zA-Z][a-zA-Z0-9_.-]{0,127})$/.exec(apiScope)
  if (!scopeParts || !isIdentityId(scopeParts[1]) ||
      scopeParts[1].toLowerCase() === clientId.toLowerCase()) {
    return invalid('Online sign-in needs a delegated scope from a separate API registration, such as api://<API-ID>/access_as_user.')
  }

  let apiUrl: URL
  try {
    apiUrl = new URL(apiBase)
  } catch {
    return invalid('Online sign-in needs an absolute Function API base URL, not a relative /api path.')
  }
  const localHttp = apiUrl.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(apiUrl.hostname)
  if ((!localHttp && apiUrl.protocol !== 'https:') || apiUrl.username || apiUrl.password ||
      apiUrl.search || apiUrl.hash || apiUrl.origin === location.origin) {
    return invalid('The online API must use a separate HTTPS origin (HTTP localhost is allowed for development), without credentials or query parameters.')
  }

  return {
    kind: 'configured',
    config: {
      tenantId: tenantId.toLowerCase(),
      clientId: clientId.toLowerCase(),
      apiScope,
      apiBaseUrl: `${apiUrl.href.replace(/\/+$/, '')}/`,
      redirectUri: new URL(`${environment.BASE_URL}auth.html`, location.origin).href,
    },
  }
}
