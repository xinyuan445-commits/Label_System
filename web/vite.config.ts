import { fileURLToPath, URL } from 'node:url'
import { existsSync, readFileSync } from 'node:fs'

import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

const packageJson = JSON.parse(
  readFileSync(fileURLToPath(new URL('./package.json', import.meta.url)), 'utf8'),
) as { version: string }

const credentialPath = fileURLToPath(
  new URL('../u8-gateway/runtime-data/deployment/U8Gateway-credentials.txt', import.meta.url),
)

function readLocalU8ApiKey(): string {
  if (!existsSync(credentialPath)) return ''
  const line = readFileSync(credentialPath, 'utf8')
    .split(/\r?\n/)
    .find((value) => /^Internal API key\s*:/i.test(value))
  return line?.replace(/^Internal API key\s*:\s*/i, '').trim() ?? ''
}

const localU8ApiKey = readLocalU8ApiKey()

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(packageJson.version),
  },
  plugins: [vue()],
  server: {
    host: '0.0.0.0',
    port: 5175,
    strictPort: true,
    proxy: {
      '/__u8': {
        target: process.env.U8_GATEWAY_TARGET || 'http://172.16.1.159:9880',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/__u8/, ''),
        headers: localU8ApiKey ? { 'X-API-Key': localU8ApiKey } : {},
      },
    },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    sourcemap: true,
  },
})
