import { getRuntimeConfig } from '@/services/runtime-config'
import { createUniqueId } from '@/utils/unique-id'

import type { BackListener, MockPdaBridge, PdaBridge, PdaScanEvent, ScanListener } from './types'

interface NativeMessage {
  source: 'label-system-pda'
  type: 'scan' | 'system-back'
  payload?: PdaScanEvent
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
  private readonly backListeners = new Set<BackListener>()

  constructor(protocolVersion: string) {
    this.protocolVersion = protocolVersion
    window.addEventListener('message', this.handleMessage)
  }

  subscribeScan(listener: ScanListener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  subscribeBack(listener: BackListener): () => void {
    const shouldEnable = this.backListeners.size === 0
    this.backListeners.add(listener)
    if (shouldEnable) this.setBackHandlerEnabled(true)
    return () => {
      this.backListeners.delete(listener)
      if (this.backListeners.size === 0) this.setBackHandlerEnabled(false)
    }
  }

  private setBackHandlerEnabled(enabled: boolean): void {
    window.parent.postMessage({
      source: 'label-system-h5',
      type: 'system-back-handler',
      payload: { enabled },
    }, nativeBridgeOrigin)
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

    const data = message.data as Partial<NativeMessage>
    if (data.source !== 'label-system-pda') {
      return
    }

    if (data.type === 'system-back') {
      this.backListeners.forEach((listener) => listener())
      return
    }

    if (data.type === 'scan' && isPdaScanEvent(data.payload)) {
      this.listeners.forEach((listener) => listener(data.payload as PdaScanEvent))
    }
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

  subscribeBack(_listener: BackListener): () => void {
    return () => undefined
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

  subscribeBack(_listener: BackListener): () => void {
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
