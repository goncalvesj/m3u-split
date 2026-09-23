import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  FAVOURITES_STORAGE_KEY,
  forgetFavourites,
  loadFavourites,
  matchFavourites,
  saveFavourites,
} from './favourites'

beforeEach(() => {
  window.localStorage.clear()
})

describe('loadFavourites', () => {
  it('starts without favourites when nothing is saved', () => {
    expect(loadFavourites()).toEqual({ categories: [], error: '' })
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBeNull()
  })

  it('loads and deduplicates category names without changing their spelling', () => {
    window.localStorage.setItem(FAVOURITES_STORAGE_KEY, JSON.stringify({
      version: 1,
      categories: ['Sports', ' sports ', 'Kids (HD) +', 'Sports', '\u00c9missions'],
    }))

    expect(loadFavourites()).toEqual({
      categories: ['Sports', ' sports ', 'Kids (HD) +', '\u00c9missions'],
      error: '',
    })
  })

  it.each([
    '',
    '{',
    'null',
    '[]',
    '"Sports"',
    '{"categories":["Sports"]}',
    '{"version":2,"categories":["Sports"]}',
    '{"version":1,"categories":"Sports"}',
    '{"version":1,"categories":[]}',
    '{"version":1,"categories":[""]}',
    '{"version":1,"categories":["Sports",42]}',
    '{"version":1,"categories":[null]}',
  ])('reports invalid data without overwriting it: %s', raw => {
    window.localStorage.setItem(FAVOURITES_STORAGE_KEY, raw)

    const result = loadFavourites()

    expect(result.categories).toEqual([])
    expect(result.error).toContain('invalid or use an unsupported version')
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBe(raw)
  })

  it('reports a storage read failure', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError')
    })

    expect(loadFavourites()).toEqual({
      categories: [],
      error: expect.stringContaining('Could not load favourites'),
    })
  })

  it('handles a browser that blocks access to localStorage itself', () => {
    vi.spyOn(window, 'localStorage', 'get').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError')
    })

    expect(loadFavourites().error).toContain('Could not load favourites')
    expect(saveFavourites(['Sports'])).toContain('Could not save favourites')
    expect(forgetFavourites()).toContain('Could not forget favourites')
  })
})

describe('saveFavourites', () => {
  it('stores only a version and unique category names', () => {
    expect(saveFavourites(['Sports', 'News', 'Sports'])).toBe('')
    expect(JSON.parse(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)!)).toEqual({
      version: 1,
      categories: ['Sports', 'News'],
    })
  })

  it('replaces the saved set only on an explicit save', () => {
    saveFavourites(['Sports', 'Unavailable'])

    expect(saveFavourites(['News'])).toBe('')
    expect(loadFavourites().categories).toEqual(['News'])
  })

  it.each([{ categories: [] }, { categories: [''] }])('rejects an empty selection without replacing favourites: $categories', ({ categories }) => {
    saveFavourites(['Sports'])

    expect(saveFavourites(categories)).toContain('Select at least one category')
    expect(loadFavourites().categories).toEqual(['Sports'])
  })

  it('reports quota failures and leaves the previous saved set intact', () => {
    saveFavourites(['Sports'])
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage full', 'QuotaExceededError')
    })

    expect(saveFavourites(['News'])).toContain('Could not save favourites')
    expect(loadFavourites().categories).toEqual(['Sports'])
  })
})

describe('forgetFavourites', () => {
  it('removes only the favourites entry', () => {
    saveFavourites(['Sports'])
    window.localStorage.setItem('unrelated-setting', 'keep')

    expect(forgetFavourites()).toBe('')
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBeNull()
    expect(window.localStorage.getItem('unrelated-setting')).toBe('keep')
    expect(forgetFavourites()).toBe('')
  })

  it('reports deletion failures without losing the saved set', () => {
    saveFavourites(['Sports'])
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError')
    })

    expect(forgetFavourites()).toContain('Could not forget favourites')
    expect(loadFavourites().categories).toEqual(['Sports'])
  })
})

describe('matchFavourites', () => {
  it('matches exact names independently of playlist order', () => {
    expect(matchFavourites(
      ['News', 'Sports', 'Missing'],
      ['Movies', 'Sports', 'News'],
    )).toEqual({ matched: ['News', 'Sports'], missing: ['Missing'] })
  })

  it('does not normalize case or whitespace or interpret names as regular expressions', () => {
    expect(matchFavourites(
      ['Sports', ' Sports ', 'sports', 'Kids (HD) +', '[UK] News', '\u00c9missions'],
      ['Sports', 'SPORTS', 'Kids (HD) +', '[UK] News', '\u00c9missions'],
    )).toEqual({
      matched: ['Sports', 'Kids (HD) +', '[UK] News', '\u00c9missions'],
      missing: [' Sports ', 'sports'],
    })
  })

  it('returns no matches without modifying either list', () => {
    const favourites = ['Sports']
    const categories = ['Movies']

    expect(matchFavourites(favourites, categories)).toEqual({
      matched: [],
      missing: ['Sports'],
    })
    expect(favourites).toEqual(['Sports'])
    expect(categories).toEqual(['Movies'])
  })

  it('handles empty lists and counts each favourite only once', () => {
    expect(matchFavourites([], ['Sports'])).toEqual({ matched: [], missing: [] })
    expect(matchFavourites(['Sports'], [])).toEqual({ matched: [], missing: ['Sports'] })
    expect(matchFavourites(['Sports', 'Sports'], ['Sports', 'Sports'])).toEqual({
      matched: ['Sports'],
      missing: [],
    })
  })
})
