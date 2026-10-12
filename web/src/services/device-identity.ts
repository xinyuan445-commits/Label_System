import { getRuntimeConfig } from '@/services/runtime-config'
import { createUniqueId } from '@/utils/unique-id'

const deviceIdKey = 'label-system:device-id:v1'

export function getDeviceId(): string {
  try {
    const existing = localStorage.getItem(deviceIdKey)
    if (existing) return existing
    const created = createUniqueId('device')
    localStorage.setItem(deviceIdKey, created)
    return created
  } catch {
    return 'device-volatile'
  }
}

export function getDeviceName(): string {
  return navigator.userAgent.slice(0, 180)
}

export function getDeviceAppVersion(): string {
  return getRuntimeConfig().h5Version
}
