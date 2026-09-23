import { useState, useRef } from 'react'
import { extractCategories, filterByCategories } from './m3u-parser'
import { forgetFavourites, loadFavourites, matchFavourites, saveFavourites } from './favourites'
import './App.css'

type ProcessingMode = 'client' | 'server'
const ENABLE_SERVER_MODE = false

function App() {
  const [mode, setMode] = useState<ProcessingMode>('client')
  const [categories, setCategories] = useState<string[]>([])
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [favourites, setFavourites] = useState(loadFavourites)
  const [favouritesMessage, setFavouritesMessage] = useState('')
  const [isProcessing, setIsProcessing] = useState(false)
  const [message, setMessage] = useState('')
  const [search, setSearch] = useState('')
  const [isDragging, setIsDragging] = useState(false)
  const [fileName, setFileName] = useState('')
  const [fileContent, setFileContent] = useState('')
  const [serverFileName, setServerFileName] = useState('')
  const fileInputRef = useRef<HTMLInputElement>(null)
  const favouritesHeadingRef = useRef<HTMLHeadingElement>(null)

  const processFile = async (file: File) => {
    setIsProcessing(true)
    setMessage('')
    setSelected(new Set())
    setFavouritesMessage('')
    setSearch('')
    setFileName(file.name)

    try {
      if (mode === 'client') {
        const content = await file.text()
        setFileContent(content)
        setCategories(extractCategories(content))
      } else {
        const formData = new FormData()
        formData.append('file', file)
        const data = await (await fetch('/api/upload', { method: 'POST', body: formData })).json()
        setCategories(data.categories ? Object.keys(data.categories) : [])
        setServerFileName(data.fileName || '')
      }
    } catch {
      setMessage(mode === 'client' ? 'Failed to read file' : 'Upload failed')
      setFileName('')
    } finally {
      setIsProcessing(false)
    }
  }

  const handleUpload = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) processFile(file)
  }

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(false)
    const file = e.dataTransfer.files[0]
    if (file && (file.name.endsWith('.m3u') || file.type === 'audio/x-mpegurl')) {
      processFile(file)
    } else {
      setMessage('Please drop a valid .m3u file')
    }
  }

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(true)
  }

  const handleDragLeave = () => setIsDragging(false)

  const toggle = (name: string) => {
    const next = new Set(selected)
    if (next.has(name)) {
      next.delete(name)
    } else {
      next.add(name)
    }
    setSelected(next)
    setFavouritesMessage('')
  }

  const query = search.toLowerCase()
  const filtered = query ? categories.filter(c => c.toLowerCase().includes(query)) : categories

  const selectAll = () => {
    setSelected(new Set(filtered))
    setFavouritesMessage('')
  }

  const selectNone = () => {
    setSelected(new Set())
    setFavouritesMessage('')
  }

  const handleSaveFavourites = () => {
    setFavouritesMessage('')
    const saved = [...selected]
    const error = saveFavourites(saved)
    if (error) {
      setFavourites(current => ({ ...current, error }))
      return
    }

    setFavourites({ categories: saved, error: '' })
    setFavouritesMessage(`${saved.length} favourite${saved.length === 1 ? '' : 's'} saved.`)
  }

  const handleUseFavourites = () => {
    const { matched, missing } = matchFavourites(favourites.categories, categories)
    if (!matched.length) {
      setFavouritesMessage('None of your favourites are available in this playlist. Your current selection is unchanged.')
      return
    }

    setSelected(new Set(matched))
    setFavouritesMessage(
      `${matched.length} favourite${matched.length === 1 ? '' : 's'} selected` +
      (missing.length ? `; ${missing.length} unavailable in this playlist (kept in your favourites).` : '.'),
    )
  }

  const handleForgetFavourites = () => {
    setFavouritesMessage('')
    const error = forgetFavourites()
    if (error) {
      setFavourites(current => ({ ...current, error }))
      return
    }

    setFavourites({ categories: [], error: '' })
    setFavouritesMessage('Favourites forgotten. Your current selection is unchanged.')
    favouritesHeadingRef.current?.focus()
  }

  const handleDownload = async () => {
    if (!selected.size) return setMessage('Select at least one category')
    setMessage('')

    try {
      const cats = [...selected]
      const result = mode === 'client'
        ? filterByCategories(fileContent, cats)
        : await (await fetch('/api/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fileName: serverFileName, categories: cats.map(name => ({ name })) }),
          })).text()

      const a = document.createElement('a')
      a.href = URL.createObjectURL(new Blob([result], { type: 'audio/x-mpegurl' }))
      a.download = 'channels.m3u'
      a.click()
    } catch {
      setMessage('Download failed')
    }
  }

  const reset = () => {
    setCategories([])
    setSelected(new Set())
    setFileName('')
    setFileContent('')
    setServerFileName('')
    setMessage('')
    setSearch('')
    setFavouritesMessage('')
    if (fileInputRef.current) fileInputRef.current.value = ''
  }

  const hasFile = categories.length > 0

  return (
    <div className="app">
      <header className="header">
        <div className="header-content">
          <h1>📺 M3U Split</h1>
          <p>Extract specific categories from your M3U playlist</p>
        </div>
      </header>

      <main className="main">
        {/* Step indicators */}
        <div className="steps">
          <div className={`step ${!hasFile ? 'active' : 'done'}`}>
            <span className="step-num">{hasFile ? '✓' : '1'}</span>
            <span className="step-label">Upload</span>
          </div>
          <div className="step-line" />
          <div className={`step ${hasFile && !selected.size ? 'active' : selected.size ? 'done' : ''}`}>
            <span className="step-num">{selected.size ? '✓' : '2'}</span>
            <span className="step-label">Select</span>
          </div>
          <div className="step-line" />
          <div className={`step ${selected.size ? 'active' : ''}`}>
            <span className="step-num">3</span>
            <span className="step-label">Download</span>
          </div>
        </div>

        {/* Mode toggle - hidden until backend is ready */}
        {ENABLE_SERVER_MODE && (
          <section className="card">
            <div className="card-header">
              <h2>Processing Mode</h2>
            </div>
            <div className="mode-toggle">
              <button
                type="button"
                className={`mode-btn ${mode === 'client' ? 'active' : ''}`}
                onClick={() => setMode('client')}
              >
                <span className="mode-icon">🔒</span>
                <span className="mode-title">Client-side</span>
                <span className="mode-desc">Private • No upload</span>
              </button>
              <button
                type="button"
                className={`mode-btn ${mode === 'server' ? 'active' : ''}`}
                onClick={() => setMode('server')}
              >
                <span className="mode-icon">☁️</span>
                <span className="mode-title">Server-side</span>
                <span className="mode-desc">API processing</span>
              </button>
            </div>
          </section>
        )}

        {/* File upload */}
        <section className="card">
          <div className="card-header">
            <h2>Upload M3U File</h2>
            {hasFile && (
              <button type="button" className="btn-text" onClick={reset}>
                Change file
              </button>
            )}
          </div>
          
          {!hasFile ? (
            <div
              className={`dropzone ${isDragging ? 'dragging' : ''} ${isProcessing ? 'processing' : ''}`}
              onDrop={handleDrop}
              onDragOver={handleDragOver}
              onDragLeave={handleDragLeave}
              onClick={() => fileInputRef.current?.click()}
            >
              <input
                ref={fileInputRef}
                type="file"
                aria-label="Choose M3U file"
                accept=".m3u,audio/x-mpegurl"
                onChange={handleUpload}
                disabled={isProcessing}
                hidden
              />
              {isProcessing ? (
                <>
                  <div className="spinner" />
                  <p>Processing file...</p>
                </>
              ) : (
                <>
                  <span className="dropzone-icon">📁</span>
                  <p>Drop your .m3u file here</p>
                  <span className="dropzone-hint">or click to browse</span>
                </>
              )}
            </div>
          ) : (
            <div className="file-info">
              <span className="file-icon">📄</span>
              <div className="file-details">
                <span className="file-name">{fileName}</span>
                <span className="file-meta">{categories.length} categories found</span>
              </div>
            </div>
          )}
          
          {message && <p className="message error">{message}</p>}
          {!hasFile && favourites.categories.length > 0 && (
            <p className="favourites-summary">
              {favourites.categories.length} favourite {favourites.categories.length === 1 ? 'category' : 'categories'} saved.
              {' '}Load a playlist to use them.
            </p>
          )}
          {!hasFile && favourites.error && (
            <p className="message favourites-error" role="alert">{favourites.error}</p>
          )}
        </section>

        {/* Category selection */}
        {hasFile && (
          <section className="card">
            <div className="card-header">
              <h2>Select Categories</h2>
              <span className="badge">{selected.size} / {categories.length}</span>
            </div>

            <div className="favourites" role="group" aria-labelledby="favourites-title">
              <h3 id="favourites-title" ref={favouritesHeadingRef} tabIndex={-1}>Favourites</h3>
              <p className="favourites-description" id="favourites-description">
                {favourites.categories.length
                  ? `${favourites.categories.length} ${favourites.categories.length === 1 ? 'category' : 'categories'} saved in this browser. Using favourites replaces your current selection.`
                  : 'Save selected category names in this browser to reuse next time.'}
              </p>
              <div className="favourites-actions">
                {favourites.categories.length > 0 ? (
                  <>
                    <button
                      type="button"
                      className="btn-small favourites-use"
                      onClick={handleUseFavourites}
                      disabled={isProcessing}
                      aria-describedby="favourites-description"
                    >
                      Use favourites
                    </button>
                    <button
                      type="button"
                      className="btn-small"
                      onClick={handleSaveFavourites}
                      disabled={!selected.size || isProcessing}
                      title="Replace favourites with all currently selected categories"
                    >
                      Update favourites
                    </button>
                    <button
                      type="button"
                      className="btn-text"
                      onClick={handleForgetFavourites}
                      disabled={isProcessing}
                    >
                      Forget favourites
                    </button>
                  </>
                ) : (
                  <button
                    type="button"
                    className="btn-small"
                    onClick={handleSaveFavourites}
                    disabled={!selected.size || isProcessing}
                  >
                    Save selection as favourites
                  </button>
                )}
              </div>
              <p className="favourites-status" role="status" aria-atomic="true">{favouritesMessage}</p>
              {favourites.error && (
                <p className="message favourites-error" role="alert">{favourites.error}</p>
              )}
            </div>

            <div className="category-toolbar">
              <div className="search-box">
                <span className="search-icon">🔍</span>
                <input
                  type="text"
                  aria-label="Search categories"
                  placeholder="Search categories..."
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
                {search && (
                  <button type="button" className="search-clear" aria-label="Clear search" onClick={() => setSearch('')}>
                    ✕
                  </button>
                )}
              </div>
              <div className="select-actions">
                <button type="button" className="btn-small" onClick={selectAll}>
                  Select all
                </button>
                <button type="button" className="btn-small" onClick={selectNone}>
                  Clear
                </button>
              </div>
            </div>

            <div className="categories">
              {!filtered.length ? (
                <div className="empty-state">
                  <p>No categories match "{search}"</p>
                </div>
              ) : (
                filtered.map(cat => (
                  <label key={cat} className={`category-item ${selected.has(cat) ? 'selected' : ''}`}>
                    <input
                      type="checkbox"
                      checked={selected.has(cat)}
                      onChange={() => toggle(cat)}
                    />
                    <span className="category-name">{cat}</span>
                  </label>
                ))
              )}
            </div>
          </section>
        )}

        {/* Download */}
        {hasFile && (
          <section className="card download-card">
            <button
              type="button"
              className="btn-primary"
              onClick={handleDownload}
              disabled={!selected.size}
            >
              <span>⬇️</span>
              Download M3U File
            </button>
            {!selected.size && (
              <p className="hint">Select at least one category to download</p>
            )}
          </section>
        )}
      </main>

      <footer className="footer">
        <p>Your data stays {mode === 'client' ? 'private — processed locally in your browser' : 'secure — processed on the server'}</p>
      </footer>
    </div>
  )
}

export default App
