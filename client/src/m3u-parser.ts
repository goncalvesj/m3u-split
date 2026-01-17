// Client-side M3U file parsing utilities

// Extract unique category names from M3U content
export const extractCategories = (content: string): string[] =>
  [...new Set([...content.matchAll(/group-title="([^"]+)"/g)].map(m => m[1]))]

// Filter M3U content to include only selected categories
export const filterByCategories = (content: string, categories: string[]): string => {
  if (!categories.length) return ''
  const escaped = categories.map(c => c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')
  return (content.match(new RegExp(`#EXTINF:-\\d+[^\\n]*group-title="(${escaped})"[^\\n]*\\n[^\\n]+\\n`, 'g')) || []).join('')
}
