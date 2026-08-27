import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { resolve } from 'path'

const root = import.meta.dirname

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: resolve(root, '../DocuEngAIne.Api/wwwroot'),
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:7285',
        changeOrigin: true,
        secure: false,
      },
      '/swagger': {
        target: 'https://localhost:7285',
        changeOrigin: true,
        secure: false,
      },
    },
  },
})
