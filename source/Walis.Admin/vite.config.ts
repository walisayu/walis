import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    host: '127.0.0.1',
    port: 11124,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://127.0.0.1:11126', changeOrigin: true },
      '/health': { target: 'http://127.0.0.1:11126', changeOrigin: true }
    }
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true
  }
})
