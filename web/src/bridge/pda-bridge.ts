import { getRuntimeConfig } from '@/services/runtime-config'

import type { MockPdaBridge, PdaBridge, PdaScanEvent, ScanListener } from './types'

class BrowserMockBridge implements MockPdaBridge {
  readonly mode = 'mock'
  readonly protocolVersion: string
  private readonly listeners = new Set<ScanListener>()

  constructor(protocolVersion: string) {
    this.protocolVersion = protocolVersion
  }

  subscribeScan(listener: ScanListener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  simulateScan(barcode: string): void {
    const normalized = barcode.trim()

    if (!normalized) {
      return
    }

    const event: PdaScanEvent = {
      id: `${Date.now()}-${crypto.randomUUID()}`,
      barcode: normalized,
      action: 'mock.pda.SCAN',
      field: 'barcode_string',
      receivedAt: Date.now(),
      bridgeVersion: this.protocolVersion,
    }

    this.listeners.forEach((listener) => listener(event))
  }
}

class UnavailableBridge implements PdaBridge {
  readonly mode = 'unavailable'
  readonly protocolVersion: string

  constructor(protocolVersion: string) {
    this.protocolVersion = protocolVersion
  }

  subscribeScan(_listener: ScanListener): () => void {
    return () => undefined
  }
}

let bridge: PdaBridge | null = null

export function getPdaBridge(): PdaBridge {
  if (bridge) {
    return bridge
  }

  const config = getRuntimeConfig()
  bridge = config.enableMockScanner
    ? new BrowserMockBridge(config.bridgeProtocol)
    : new UnavailableBridge(config.bridgeProtocol)

  return bridge
}

export function isMockPdaBridge(value: PdaBridge): value is MockPdaBridge {
  return value.mode === 'mock'
}
