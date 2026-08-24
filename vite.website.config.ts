import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'

export default defineConfig({
  root: fileURLToPath(new URL('./website', import.meta.url)),
  base: './',
  publicDir: false,
  build: {
    outDir: fileURLToPath(new URL('./dist-website', import.meta.url)),
    emptyOutDir: true,
  },
  server: {
    port: 4175,
    strictPort: true,
  },
})
