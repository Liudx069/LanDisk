import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The server serves the built assets from its own wwwroot, so the output goes straight there.
// No CDN, no runtime Node.js dependency: everything the browser needs is bundled locally.
export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: '../LanDisk.Server/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    target: 'es2020',
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name]-[hash].js',
        chunkFileNames: 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name]-[hash][extname]'
      }
    }
  },
  server: {
    port: 5199,
    strictPort: true
  }
})
