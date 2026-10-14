export interface PdaScanEvent {
  id: string
  barcode: string
  action: string
  field: string
  receivedAt: number
  bridgeVersion: string
}

export type ScanListener = (event: PdaScanEvent) => void
export type BackListener = () => void

export interface PdaBridge {
  readonly mode: 'mock' | 'native' | 'unavailable'
  readonly protocolVersion: string
  subscribeScan(listener: ScanListener): () => void
  subscribeBack(listener: BackListener): () => void
}

export interface MockPdaBridge extends PdaBridge {
  readonly mode: 'mock'
  simulateScan(barcode: string): void
}
