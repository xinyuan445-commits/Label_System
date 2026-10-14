export type ShellEnvironment = 'development' | 'test' | 'production'
export type ScanSoundMode = 'short' | 'soft' | 'double' | 'off'
export type ScanFeedbackMode = 'tone' | 'voice' | 'off'
export type ScanFeedbackResult = 'success' | 'error'

export interface ShellConfig {
  environment: ShellEnvironment
  developmentUrl: string
  testUrl: string
  productionUrl: string
  scanSound: ScanSoundMode
  scanFeedbackMode: ScanFeedbackMode
  bridgeVersion: string
  apkVersion: string
  apkVersionCode: number
}

interface ShellMessage {
  source: 'label-system-pda'
  type: 'config' | 'config-saved' | 'config-error' | 'sound-tested' | 'feedback-played'
  payload?: unknown
}

const nativeBridgeOrigin = 'http://127.0.0.1:18765'
const browserStorageKey = 'label_system_shell_config'

const defaultConfig: ShellConfig = {
  environment: 'development',
  developmentUrl: 'http://172.16.10.45:5173/',
  testUrl: 'http://172.16.10.45:5174/',
  productionUrl: 'http://172.16.10.45:5175/',
  scanSound: 'short',
  scanFeedbackMode: 'tone',
  bridgeVersion: 'pda-bridge-v1',
  apkVersion: '0.5.0',
  apkVersionCode: 106,
}

function isEnvironment(value: unknown): value is ShellEnvironment {
  return value === 'development' || value === 'test' || value === 'production'
}

function isScanSound(value: unknown): value is ScanSoundMode {
  return value === 'short' || value === 'soft' || value === 'double' || value === 'off'
}

function isScanFeedbackMode(value: unknown): value is ScanFeedbackMode {
  return value === 'tone' || value === 'voice' || value === 'off'
}

function normalizeUrl(value: string): string {
  const url = new URL(value.trim())

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new Error('地址无效')
  }

  return url.toString()
}

function parseConfig(value: unknown): ShellConfig {
  if (!value || typeof value !== 'object') {
    throw new Error('配置无效')
  }

  const candidate = value as Partial<ShellConfig>

  if (!isEnvironment(candidate.environment)) {
    throw new Error('环境无效')
  }

  const scanSound = isScanSound(candidate.scanSound) ? candidate.scanSound : 'short'
  return {
    environment: candidate.environment,
    developmentUrl: normalizeUrl(String(candidate.developmentUrl || '')),
    testUrl: normalizeUrl(String(candidate.testUrl || '')),
    productionUrl: normalizeUrl(String(candidate.productionUrl || '')),
    scanSound,
    scanFeedbackMode: isScanFeedbackMode(candidate.scanFeedbackMode)
      ? candidate.scanFeedbackMode
      : scanSound === 'off' ? 'off' : 'tone',
    bridgeVersion: String(candidate.bridgeVersion || defaultConfig.bridgeVersion),
    apkVersion: String(candidate.apkVersion || defaultConfig.apkVersion),
    apkVersionCode:
      typeof candidate.apkVersionCode === 'number' && Number.isFinite(candidate.apkVersionCode)
        ? candidate.apkVersionCode
        : defaultConfig.apkVersionCode,
  }
}

function isNativeShell(): boolean {
  return window.parent !== window
}

function waitForNativeConfig(
  expectedTypes: Array<ShellMessage['type']>,
  action: () => void,
): Promise<ShellConfig> {
  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      window.removeEventListener('message', handleMessage)
      reject(new Error('设置连接失败'))
    }, 2000)

    function handleMessage(message: MessageEvent<unknown>): void {
      if (
        message.source !== window.parent ||
        message.origin !== nativeBridgeOrigin ||
        !message.data ||
        typeof message.data !== 'object'
      ) {
        return
      }

      const data = message.data as Partial<ShellMessage>
      if (data.source !== 'label-system-pda' || !expectedTypes.includes(data.type as ShellMessage['type'])) {
        return
      }

      window.clearTimeout(timeout)
      window.removeEventListener('message', handleMessage)

      if (data.type === 'config-error') {
        reject(new Error('保存失败'))
        return
      }

      try {
        resolve(parseConfig(data.payload))
      } catch {
        reject(new Error('配置无效'))
      }
    }

    window.addEventListener('message', handleMessage)
    action()
  })
}

function loadBrowserConfig(): ShellConfig {
  try {
    const stored = localStorage.getItem(browserStorageKey)
    return stored ? parseConfig(JSON.parse(stored)) : defaultConfig
  } catch {
    return defaultConfig
  }
}

export async function loadShellConfig(): Promise<ShellConfig> {
  if (!isNativeShell()) {
    return loadBrowserConfig()
  }

  return waitForNativeConfig(['config'], () => {
    window.parent.postMessage({ source: 'label-system-h5', type: 'config-request' }, nativeBridgeOrigin)
  })
}

export async function saveShellConfig(
  value: ShellConfig,
  options: { applyAddress?: boolean } = {},
): Promise<void> {
  const config = parseConfig(value)

  if (!isNativeShell()) {
    localStorage.setItem(browserStorageKey, JSON.stringify(config))
    const nextUrl = config[`${config.environment}Url`]
    if (options.applyAddress !== false && new URL(nextUrl).href !== window.location.href) {
      window.location.assign(nextUrl)
    }
    return
  }

  await waitForNativeConfig(['config-saved', 'config-error'], () => {
    window.parent.postMessage(
      { source: 'label-system-h5', type: 'config-save', payload: config },
      nativeBridgeOrigin,
    )
  })
}

function playBrowserTone(mode: ScanSoundMode): void {
  if (mode === 'off') return
  const AudioContextClass = window.AudioContext
  const context = new AudioContextClass()
  const play = (frequency: number, startAt: number, duration: number): void => {
    const oscillator = context.createOscillator()
    const gain = context.createGain()
    oscillator.frequency.value = frequency
    gain.gain.setValueAtTime(0.0001, startAt)
    gain.gain.exponentialRampToValueAtTime(0.28, startAt + 0.01)
    gain.gain.exponentialRampToValueAtTime(0.0001, startAt + duration)
    oscillator.connect(gain)
    gain.connect(context.destination)
    oscillator.start(startAt)
    oscillator.stop(startAt + duration + 0.02)
  }

  const now = context.currentTime
  if (mode === 'double') {
    play(1800, now, 0.09)
    play(1800, now + 0.15, 0.09)
  } else {
    play(mode === 'soft' ? 1100 : 1900, now, mode === 'soft' ? 0.16 : 0.1)
  }
  window.setTimeout(() => void context.close(), 500)
}

function playBrowserFeedback(mode: ScanFeedbackMode, result: ScanFeedbackResult): void {
  if (mode === 'off') return
  if (mode === 'voice') {
    if (!('speechSynthesis' in window)) return
    window.speechSynthesis.cancel()
    const utterance = new SpeechSynthesisUtterance(result === 'success' ? 'O K' : 'N G')
    utterance.lang = 'en-US'
    utterance.rate = 1.25
    window.speechSynthesis.speak(utterance)
    return
  }
  if (result === 'success') {
    playBrowserTone('short')
    return
  }
  const context = new window.AudioContext()
  const oscillator = context.createOscillator()
  const gain = context.createGain()
  oscillator.frequency.value = 360
  gain.gain.value = 0.25
  oscillator.connect(gain)
  gain.connect(context.destination)
  oscillator.start()
  oscillator.stop(context.currentTime + 0.32)
  window.setTimeout(() => void context.close(), 500)
}

export function playScanFeedback(result: ScanFeedbackResult): void {
  if (!isNativeShell()) {
    const config = loadBrowserConfig()
    playBrowserFeedback(config.scanFeedbackMode, result)
    return
  }
  window.parent.postMessage(
    { source: 'label-system-h5', type: 'feedback-play', payload: { result } },
    nativeBridgeOrigin,
  )
}

export async function testScanFeedback(
  mode: ScanFeedbackMode,
  result: ScanFeedbackResult,
): Promise<void> {
  if (!isNativeShell()) {
    playBrowserFeedback(mode, result)
    return
  }
  await waitForNativeConfig(['feedback-played', 'config-error'], () => {
    window.parent.postMessage(
      { source: 'label-system-h5', type: 'feedback-play', payload: { mode, result } },
      nativeBridgeOrigin,
    )
  })
}

export async function testScanSound(mode: ScanSoundMode): Promise<void> {
  if (!isNativeShell()) {
    playBrowserTone(mode)
    return
  }

  await waitForNativeConfig(['sound-tested', 'config-error'], () => {
    window.parent.postMessage(
      { source: 'label-system-h5', type: 'sound-test', payload: { mode } },
      nativeBridgeOrigin,
    )
  })
}

export function createDefaultShellConfig(): ShellConfig {
  return { ...defaultConfig }
}
