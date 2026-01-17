import { useState, useRef, useMemo } from 'react'
import type { Category } from './m3u-parser'
import { extractCategories, filterByCategories, readFileAsText } from './m3u-parser'
import './App.css'

type ProcessingMode = 'client' | 'server'

// Feature flag: set to true when backend API is ready
const ENABLE_SERVER_MODE = false

function App() {
  const [mode, setMode] = useState<ProcessingMode>('client')
  const [categories, setCategories] = useState<Record<string, Category>>({})
  const [selectedCategories, setSelectedCategories] = useState<string[]>([])
  const [isProcessing, setIsProcessing] = useState(false)
  const [message, setMessage] = useState('')
  const [searchQuery, setSearchQuery] = useState('')
  const [isDragging, setIsDragging] = useState(false)
  const [fileName, setFileName] = useState('')
  const fileInputRef = useRef<HTMLInputElement>(null)

  // Client-side state
  const [fileContent, setFileContent] = useState('')

  // Server-side state
  const [serverFileName, setServerFileName] = useState('')

  const processFile = async (file: File) => {
    setIsProcessing(true)
    setMessage('')
    setSelectedCategories([])
    setSearchQuery('')
    setFileName(file.name)

    try {
      if (mode === 'client') {
        const content = await readFileAsText(file)
        setFileContent(content)
        setCategories(extractCategories(content))
      } else {
        const formData = new FormData()
        formData.append('file', file)
        const res = await fetch('/api/upload', { method: 'POST', body: formData })
        const data = await res.json()
        const cats: Record<string, Category> = {}
        if (data.categories) {
          for (const [key, val] of Object.entries(data.categories)) {
            cats[key] = typeof val === 'object' && val !== null ? (val as Category) : { name: key }
          }
        }
        setCategories(cats)
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

  const toggleCategory = (name: string) => {
    setSelectedCategories(prev =>
      prev.includes(name) ? prev.filter(n => n !== name) : [...prev, name]
    )
  }

  const categoryEntries = Object.entries(categories)
  
  const filteredCategories = useMemo(() => {
    if (!searchQuery.trim()) return categoryEntries
    const query = searchQuery.toLowerCase()
    return categoryEntries.filter(([key]) => key.toLowerCase().includes(query))
  }, [categoryEntries, searchQuery])

  const selectAll = () => setSelectedCategories(filteredCategories.map(([key]) => key))
  const selectNone = () => setSelectedCategories([])

  const handleDownload = async () => {
    if (!selectedCategories.length) {
      setMessage('Select at least one category')
      return
    }
    setMessage('')

    try {
      let result: string
      if (mode === 'client') {
        const selected = selectedCategories.map(name => ({ name }))
        result = filterByCategories(fileContent, selected)
      } else {
        const res = await fetch('/api/download', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            fileName: serverFileName,
            categories: selectedCategories.map(name => ({ name })),
          }),
        })
        result = await res.text()
      }

      const blob = new Blob([result], { type: 'audio/x-mpegurl' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = 'channels.m3u'
      a.click()
      URL.revokeObjectURL(url)
    } catch {
      setMessage('Download failed')
    }
  }

  const reset = () => {
    setCategories({})
    setSelectedCategories([])
    setFileName('')
    setFileContent('')
    setServerFileName('')
    setMessage('')
    setSearchQuery('')
    if (fileInputRef.current) fileInputRef.current.value = ''
  }

  const hasFile = categoryEntries.length > 0

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
          <div className={`step ${hasFile && !selectedCategories.length ? 'active' : selectedCategories.length ? 'done' : ''}`}>
            <span className="step-num">{selectedCategories.length ? '✓' : '2'}</span>
            <span className="step-label">Select</span>
          </div>
          <div className="step-line" />
          <div className={`step ${selectedCategories.length ? 'active' : ''}`}>
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
                <span className="file-meta">{categoryEntries.length} categories found</span>
              </div>
            </div>
          )}
          
          {message && <p className="message error">{message}</p>}
        </section>

        {/* Category selection */}
        {hasFile && (
          <section className="card">
            <div className="card-header">
              <h2>Select Categories</h2>
              <span className="badge">{selectedCategories.length} / {categoryEntries.length}</span>
            </div>

            <div className="category-toolbar">
              <div className="search-box">
                <span className="search-icon">🔍</span>
                <input
                  type="text"
                  placeholder="Search categories..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                />
                {searchQuery && (
                  <button type="button" className="search-clear" onClick={() => setSearchQuery('')}>
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
              {filteredCategories.length === 0 ? (
                <div className="empty-state">
                  <p>No categories match "{searchQuery}"</p>
                </div>
              ) : (
                filteredCategories.map(([key]) => (
                  <label key={key} className={`category-item ${selectedCategories.includes(key) ? 'selected' : ''}`}>
                    <input
                      type="checkbox"
                      checked={selectedCategories.includes(key)}
                      onChange={() => toggleCategory(key)}
                    />
                    <span className="category-name">{key}</span>
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
              disabled={!selectedCategories.length}
            >
              <span>⬇️</span>
              Download M3U File
            </button>
            {selectedCategories.length === 0 && (
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
