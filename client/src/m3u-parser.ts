// Client-side M3U file parsing utilities

export interface Category {
  name: string
}

export interface ParsedM3U {
  content: string
  categories: Record<string, Category>
}

// Extract unique categories from M3U content
export function extractCategories(content: string): Record<string, Category> {
  const categories: Record<string, Category> = {}
  const regex = /group-title="([^"]*)"/g
  let match: RegExpExecArray | null

  while ((match = regex.exec(content)) !== null) {
    const name = match[1]
    if (name && !categories[name]) {
      categories[name] = { name }
    }
  }

  return categories
}

// Filter M3U content to include only selected categories
export function filterByCategories(content: string, selectedCategories: Category[]): string {
  if (!selectedCategories.length) return ''

  let result = ''

  for (const category of selectedCategories) {
    // Escape special regex characters in category name
    const escaped = category.name.replace(/[\\|]/g, '\\$&')
    const pattern = new RegExp(`(#EXTINF:-\\d+.*group-title="${escaped}".*\\n.*\\n)`, 'g')
    const matches = content.match(pattern)

    if (matches) {
      result += matches.join('')
    }
  }

  return result
}

// Read file as text
export function readFileAsText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as string)
    reader.onerror = () => reject(new Error('Failed to read file'))
    reader.readAsText(file)
  })
}
