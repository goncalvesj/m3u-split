import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  // Base path for GitHub Pages deployment (https://<username>.github.io/<repo>/)
  base: '/m3u-split/',
  plugins: [react()],
  test: {
    environment: 'jsdom',
    restoreMocks: true,
    unstubGlobals: true,
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:7071',
        changeOrigin: true,
      },
    },
  },
})
