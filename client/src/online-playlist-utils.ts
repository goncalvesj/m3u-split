const MAX_SOURCE_URL_LENGTH = 2048
const INTERACTION_CODES = ['interaction_required', 'consent_required', 'login_required', 'no_account_error']

export function checkSourceUrl(value: string): string {
  const text = value.trim()
  if (!text) return 'Enter the playlist URL from your provider.'
  if (text.length > MAX_SOURCE_URL_LENGTH) {
    return `This URL is too long. Playlist URLs can have up to ${MAX_SOURCE_URL_LENGTH} characters.`
  }

  let url: URL
  try {
    url = new URL(text)
  } catch {
    return 'Enter a complete playlist URL that starts with http:// or https://.'
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return 'Use a playlist URL that starts with http:// or https://.'
  }
  if (url.username || url.password) {
    return 'Remove the user name and password before the host name (user:password@). You can use credentials in the query string.'
  }
  if (text.includes('#')) return 'Remove the # part from the end of the URL.'
  return ''
}

export function needsInteraction(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) return false
  if ('name' in error && error.name === 'InteractionRequiredAuthError') return true
  return 'errorCode' in error && INTERACTION_CODES.includes(String(error.errorCode))
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`
}

export const formatCount = (value: number) => value.toLocaleString()

export const formatTime = (time: number) =>
  new Date(time).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
