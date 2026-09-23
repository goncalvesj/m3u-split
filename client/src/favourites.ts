export const FAVOURITES_STORAGE_KEY = 'm3u-split:favourites'

type StoredFavourites = {
  version: 1
  categories: string[]
}

type FavouritesState = {
  categories: string[]
  error: string
}

const INVALID_FAVOURITES_MESSAGE =
  'Saved favourites are invalid or use an unsupported version. Select categories and save again to replace them.'

const isStoredFavourites = (value: unknown): value is StoredFavourites =>
  typeof value === 'object' &&
  value !== null &&
  'version' in value &&
  value.version === 1 &&
  'categories' in value &&
  Array.isArray(value.categories) &&
  value.categories.length > 0 &&
  value.categories.every(category => typeof category === 'string' && category.length > 0)

export const loadFavourites = (): FavouritesState => {
  let raw: string | null
  try {
    raw = window.localStorage.getItem(FAVOURITES_STORAGE_KEY)
  } catch {
    return {
      categories: [],
      error: 'Could not load favourites. Allow site storage in your browser, then reload. You can still select categories and download.',
    }
  }

  if (raw === null) return { categories: [], error: '' }

  let stored: unknown
  try {
    stored = JSON.parse(raw)
  } catch {
    return { categories: [], error: INVALID_FAVOURITES_MESSAGE }
  }

  if (!isStoredFavourites(stored)) {
    return { categories: [], error: INVALID_FAVOURITES_MESSAGE }
  }

  return { categories: [...new Set(stored.categories)], error: '' }
}

export const saveFavourites = (categories: readonly string[]): string => {
  const uniqueCategories = [...new Set(categories)]
  if (!uniqueCategories.length || uniqueCategories.some(category => !category.length)) {
    return 'Select at least one category before saving favourites.'
  }

  const stored: StoredFavourites = { version: 1, categories: uniqueCategories }
  try {
    window.localStorage.setItem(FAVOURITES_STORAGE_KEY, JSON.stringify(stored))
  } catch {
    return 'Could not save favourites. Browser storage may be blocked or full. Allow site storage or free some space, then try again.'
  }

  return ''
}

export const forgetFavourites = (): string => {
  try {
    window.localStorage.removeItem(FAVOURITES_STORAGE_KEY)
  } catch {
    return 'Could not forget favourites. Allow site storage in your browser, then try again.'
  }

  return ''
}

export const matchFavourites = (favourites: readonly string[], categories: readonly string[]) => {
  const available = new Set(categories)
  const saved = [...new Set(favourites)]
  return {
    matched: saved.filter(category => available.has(category)),
    missing: saved.filter(category => !available.has(category)),
  }
}
