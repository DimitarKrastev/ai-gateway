import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const gatewayPort = process.env.GATEWAY_PORT ?? '6200'
const webPort = Number(process.env.WEB_PORT ?? '6201')
const gateway = `http://127.0.0.1:${gatewayPort}`

export default defineConfig({
  plugins: [react()],
  build: {
    // The gateway serves the built UI from its wwwroot.
    outDir: '../src/Gateway/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: webPort,
    strictPort: true,
    proxy: {
      '/api': gateway,
      '/anthropic': gateway,
      '/openai': gateway,
    },
  },
})
