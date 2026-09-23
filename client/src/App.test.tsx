import { StrictMode } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { FAVOURITES_STORAGE_KEY, loadFavourites, saveFavourites } from './favourites'

const playlistEntries = (categories: string[]) => categories.map(category =>
  `#EXTINF:-1 group-title="${category}",${category} channel\nhttps://example.invalid/${encodeURIComponent(category)}\n`,
).join('')

const uploadPlaylist = async (categories = ['Sports', 'News', 'Movies'], name = 'playlist.m3u') => {
  const content = `#EXTM3U\n${playlistEntries(categories)}`
  const file = new File([content], name, { type: 'audio/x-mpegurl' })
  // jsdom's File does not implement text().
  Object.defineProperty(file, 'text', { value: async () => content })
  fireEvent.change(screen.getByLabelText('Choose M3U file'), { target: { files: [file] } })
  await screen.findByRole('heading', { name: 'Select Categories' })
}

const checkbox = (name: string) => screen.getByRole<HTMLInputElement>('checkbox', { name })
const button = (name: string | RegExp) => screen.getByRole<HTMLButtonElement>('button', { name })
const renderApp = () => render(<StrictMode><App /></StrictMode>)

const blobText = (blob: Blob) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.onerror = () => reject(reader.error)
  reader.readAsText(blob)
})

beforeEach(() => {
  window.localStorage.clear()
})

afterEach(() => {
  cleanup()
})

describe('category favourites', () => {
  it('saves all selected categories, including those hidden by search', async () => {
    const user = userEvent.setup()
    renderApp()
    await uploadPlaylist()

    expect(button('Save selection as favourites').disabled).toBe(true)
    await user.click(checkbox('Sports'))
    await user.click(checkbox('News'))
    await user.type(screen.getByRole('textbox', { name: 'Search categories' }), 'Sports')
    await user.click(button('Save selection as favourites'))

    expect(loadFavourites().categories).toEqual(['Sports', 'News'])
    expect(screen.getByRole('status').textContent).toBe('2 favourites saved.')
    expect(button('Use favourites').disabled).toBe(false)
    expect(button('Update favourites').disabled).toBe(false)
    expect(button('Forget favourites')).toBeDefined()
    expect(checkbox('Sports').checked).toBe(true)
  })

  it('persists across remounts and reuploads, then downloads only the restored categories', async () => {
    const user = userEvent.setup()
    const firstVisit = renderApp()
    await uploadPlaylist()
    await user.click(checkbox('Sports'))
    await user.click(checkbox('News'))
    await user.click(button('Save selection as favourites'))

    firstVisit.unmount()
    renderApp()
    expect(screen.getByText(/2 favourite categories saved/)).toBeDefined()
    await uploadPlaylist(['Movies', 'News', 'Sports'], 'renamed-playlist.m3u')
    expect(checkbox('Sports').checked).toBe(false)
    expect(checkbox('News').checked).toBe(false)
    await user.click(checkbox('Movies'))
    await user.click(button('Use favourites'))

    expect(checkbox('Sports').checked).toBe(true)
    expect(checkbox('News').checked).toBe(true)
    expect(checkbox('Movies').checked).toBe(false)
    expect(screen.getByRole('status').textContent).toBe('2 favourites selected.')

    const createObjectURL = vi.fn<(blob: Blob) => string>(() => 'blob:favourites-download')
    vi.stubGlobal('URL', class extends URL { static createObjectURL = createObjectURL })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await user.click(button(/Download M3U File/))

    expect(createObjectURL).toHaveBeenCalledOnce()
    expect(await blobText(createObjectURL.mock.calls[0][0])).toBe(playlistEntries(['News', 'Sports']))
    expect(click).toHaveBeenCalledOnce()
    expect(click.mock.instances[0]).toHaveProperty('download', 'channels.m3u')
    expect(loadFavourites().categories).toEqual(['Sports', 'News'])
    expect(JSON.parse(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)!)).toEqual({
      version: 1,
      categories: ['Sports', 'News'],
    })
  })

  it('keeps unavailable favourites for the next file and does not apply them automatically', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports', 'News'])
    renderApp()
    await uploadPlaylist(['Movies', 'Sports'])
    await user.click(checkbox('Movies'))
    await user.click(button('Use favourites'))

    expect(checkbox('Sports').checked).toBe(true)
    expect(checkbox('Movies').checked).toBe(false)
    expect(screen.getByRole('status').textContent).toBe(
      '1 favourite selected; 1 unavailable in this playlist (kept in your favourites).',
    )
    expect(screen.getByText('1 / 2')).toBeDefined()
    expect(loadFavourites().categories).toEqual(['Sports', 'News'])

    await user.click(button('Change file'))
    expect(screen.getByText(/2 favourite categories saved/)).toBeDefined()
    await uploadPlaylist()
    expect(checkbox('Sports').checked).toBe(false)
    expect(screen.getByRole('status').textContent).toBe('')
    await user.click(button('Use favourites'))
    expect(checkbox('Sports').checked).toBe(true)
    expect(checkbox('News').checked).toBe(true)
  })

  it('leaves the current selection unchanged when no exact names match', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports'])
    renderApp()
    await uploadPlaylist(['sports', ' Sports ', 'Movies'])
    await user.click(checkbox('Movies'))
    await user.click(button('Use favourites'))

    expect(checkbox('Movies').checked).toBe(true)
    expect(checkbox('sports').checked).toBe(false)
    expect(screen.getByText('1 / 3')).toBeDefined()
    expect(screen.getByRole('status').textContent).toBe(
      'None of your favourites are available in this playlist. Your current selection is unchanged.',
    )
    expect(loadFavourites().categories).toEqual(['Sports'])
  })

  it('applies favourites independently of the active search and replaces the selection', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports', 'News'])
    renderApp()
    await uploadPlaylist()
    await user.click(checkbox('Movies'))
    const search = screen.getByRole<HTMLInputElement>('textbox', { name: 'Search categories' })
    await user.type(search, 'Movies')
    await user.click(button('Use favourites'))

    expect(search.value).toBe('Movies')
    expect(checkbox('Movies').checked).toBe(false)
    expect(screen.getByText('2 / 3')).toBeDefined()
    expect(screen.getByRole('status').textContent).toBe('2 favourites selected.')
    await user.click(button('Clear search'))
    expect(checkbox('Sports').checked).toBe(true)
    expect(checkbox('News').checked).toBe(true)
  })

  it('updates the entire saved set, but clearing checkboxes never erases favourites', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports', 'Unavailable'])
    renderApp()
    await uploadPlaylist()

    expect(button('Update favourites').disabled).toBe(true)
    await user.click(checkbox('News'))
    await user.click(checkbox('Movies'))
    await user.type(screen.getByRole('textbox', { name: 'Search categories' }), 'News')
    await user.click(button('Update favourites'))

    expect(loadFavourites().categories).toEqual(['News', 'Movies'])
    expect(checkbox('News').checked).toBe(true)
    await user.click(button('Clear'))
    expect(button('Update favourites').disabled).toBe(true)
    expect(screen.getByRole('status').textContent).toBe('')
    expect(loadFavourites().categories).toEqual(['News', 'Movies'])
    await user.click(button('Use favourites'))
    expect(screen.getByText('2 / 3')).toBeDefined()
  })

  it('forgets only the saved set and keeps the current selection', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports'])
    window.localStorage.setItem('unrelated-setting', 'keep')
    const firstVisit = renderApp()
    await uploadPlaylist()
    await user.click(button('Use favourites'))
    await user.tab()
    expect(document.activeElement).toBe(button('Update favourites'))
    await user.tab()
    expect(document.activeElement).toBe(button('Forget favourites'))
    await user.keyboard('{Enter}')

    expect(checkbox('Sports').checked).toBe(true)
    expect(button('Save selection as favourites').disabled).toBe(false)
    expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Favourites' }))
    await user.tab()
    expect(document.activeElement).toBe(button('Save selection as favourites'))
    expect(screen.getByRole('status').textContent).toBe(
      'Favourites forgotten. Your current selection is unchanged.',
    )
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBeNull()
    expect(window.localStorage.getItem('unrelated-setting')).toBe('keep')

    firstVisit.unmount()
    renderApp()
    await uploadPlaylist()
    expect(screen.queryByRole('button', { name: 'Use favourites' })).toBeNull()
    expect(button('Save selection as favourites').disabled).toBe(true)
  })

  it('clears selection feedback when the user manually changes categories', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports'])
    renderApp()
    await uploadPlaylist()
    await user.click(button('Use favourites'))
    await user.click(checkbox('News'))
    expect(screen.getByRole('status').textContent).toBe('')
    await user.click(button('Use favourites'))
    await user.click(button('Select all'))
    expect(screen.getByRole('status').textContent).toBe('')
    expect(screen.getByText('3 / 3')).toBeDefined()
    expect(loadFavourites().categories).toEqual(['Sports'])
  })
})

describe('favourites storage errors', () => {
  it('shows invalid saved data without changing it until the user explicitly saves', async () => {
    const user = userEvent.setup()
    window.localStorage.setItem(FAVOURITES_STORAGE_KEY, '{invalid')
    renderApp()
    expect(screen.getByRole('alert').textContent).toContain('invalid')
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBe('{invalid')
    await uploadPlaylist()
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    await user.click(checkbox('Sports'))
    await user.click(button('Save selection as favourites'))

    expect(loadFavourites().categories).toEqual(['Sports'])
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.getByRole('status').textContent).toBe('1 favourite saved.')
  })

  it('keeps manual selection and downloading usable when browser storage is blocked', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'localStorage', 'get').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError')
    })
    renderApp()
    expect(screen.getByRole('alert').textContent).toContain('Could not load favourites')
    await uploadPlaylist()
    await user.click(checkbox('Sports'))
    await user.click(button('Save selection as favourites'))

    expect(screen.getByRole('alert').textContent).toContain('Could not save favourites')
    expect(screen.getByRole('status').textContent).toBe('')
    expect(checkbox('Sports').checked).toBe(true)
    expect(screen.queryByRole('button', { name: 'Use favourites' })).toBeNull()
    expect(button(/Download M3U File/).disabled).toBe(false)

    const createObjectURL = vi.fn<(blob: Blob) => string>(() => 'blob:manual-download')
    vi.stubGlobal('URL', class extends URL { static createObjectURL = createObjectURL })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await user.click(button(/Download M3U File/))
    expect(await blobText(createObjectURL.mock.calls[0][0])).toBe(playlistEntries(['Sports']))
  })

  it('keeps the previous favourites after a failed update and supports retry', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports'])
    renderApp()
    await uploadPlaylist()
    await user.click(checkbox('News'))
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage full', 'QuotaExceededError')
    })
    await user.click(button('Update favourites'))

    expect(screen.getByRole('alert').textContent).toContain('Could not save favourites')
    expect(screen.getByRole('status').textContent).toBe('')
    expect(loadFavourites().categories).toEqual(['Sports'])
    expect(checkbox('News').checked).toBe(true)
    await user.click(button('Use favourites'))
    expect(checkbox('Sports').checked).toBe(true)
    expect(checkbox('News').checked).toBe(false)

    write.mockRestore()
    await user.click(checkbox('News'))
    await user.click(button('Update favourites'))
    expect(screen.queryByRole('alert')).toBeNull()
    expect(loadFavourites().categories).toEqual(['Sports', 'News'])
  })

  it('keeps favourites after a failed deletion and only clears them after a successful retry', async () => {
    const user = userEvent.setup()
    saveFavourites(['Sports'])
    renderApp()
    await uploadPlaylist()
    const remove = vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Storage blocked', 'SecurityError')
    })
    await user.click(button('Forget favourites'))

    expect(screen.getByRole('alert').textContent).toContain('Could not forget favourites')
    expect(screen.getByRole('status').textContent).toBe('')
    expect(loadFavourites().categories).toEqual(['Sports'])
    await user.click(button('Use favourites'))
    expect(checkbox('Sports').checked).toBe(true)

    remove.mockRestore()
    await user.click(button('Forget favourites'))
    expect(screen.queryByRole('alert')).toBeNull()
    expect(window.localStorage.getItem(FAVOURITES_STORAGE_KEY)).toBeNull()
    expect(checkbox('Sports').checked).toBe(true)
  })
})
