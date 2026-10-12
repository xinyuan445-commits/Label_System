import { getRuntimeConfig } from '@/services/runtime-config'
import { createUniqueId } from '@/utils/unique-id'

import type { MockPdaBridge, PdaBridge, PdaScanEvent, ScanListener } from './types'

interface NativeScanMessage {
  source: 'label-system-pda'
  type: 'scan'
  payload: PdaScanEvent
}

const nativeBridgeOrigin = 'http://127.0.0.1:18765'

function isPdaScanEvent(value: unknown): value is PdaScanEvent {
  if (!value || typeof value !== 'object') {
    return false
  }

  const event = value as Partial<PdaScanEvent>
  return (
    typeof event.id === 'string' &&
    typeof event.barcode === 'string' &&
    typeof event.action === 'string' &&
    typeof event.field === 'string' &&
    typeof event.receivedAt === 'number' &&
    typeof event.bridgeVersion === 'string'
  )
}

class NativeMessageBridge implements PdaBridge {
  readonly mode = 'native'
  readonly protocolVersion: string
  private readonly listeners = new Set<ScanListener>()

  constructor(protocolVersion: string) {
    this.protocolVersion = protocolVersion
    window.addEventListener('message', this.handleMessage)
  }

  subscribeScan(listener: ScanListener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  private readonly handleMessage = (message: MessageEvent<unknown>): void => {
    if (
      message.source !== window.parent ||
      message.origin !== nativeBridgeOrigin ||
      !message.data ||
      typeof message.data !== 'object'
    ) {
      return
    }

    const data = message.data as Partial<NativeScanMessage>
    if (data.source !== 'label-system-pda' || data.type !== 'scan' || !isPdaScanEvent(data.payload)) {
      return
    }

    this.listeners.forEach((listener) => listener(data.payload as PdaScanEvent))
  }
}

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
      id: `${Date.now()}-${createUniqueId('scan')}`,
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
  if (window.parent !== window) {
    bridge = new NativeMessageBridge(config.bridgeProtocol)
  } else {
    bridge = config.enableMockScanner
      ? new BrowserMockBridge(config.bridgeProtocol)
      : new UnavailableBridge(config.bridgeProtocol)
  }

  return bridge
}

export function isMockPdaBridge(value: PdaBridge): value is MockPdaBridge {
  return value.mode === 'mock'
}
