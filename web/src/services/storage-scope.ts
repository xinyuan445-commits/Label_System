import { getRuntimeConfig } from '@/services/runtime-config'

// Existing unscoped browser data belongs to the original 9890/008 endpoint.
// Keep it in place; other endpoints must never adopt it automatically.
const legacyApiBaseUrl = 'http://172.16.1.159:9890'

export function scopedStorageKey(key: string): string {
  const config = getRuntimeConfig()
  const api = config.apiBaseUrl.replace(/\/+$/, '')
  return api === legacyApiBaseUrl ? key
    : `${key}.scope.${encodeURIComponent(`${config.environment}|${api}`)}`
}
