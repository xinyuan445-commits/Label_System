export type ShellEnvironment = 'development' | 'test' | 'production'

export interface ShellConfig {
  environment: ShellEnvironment
  developmentUrl: string
  testUrl: string
  productionUrl: string
  bridgeVersion: string
}

interface ShellMessage {
  source: 'label-system-pda'
  type: 'config' | 'config-saved' | 'config-error'
  payload?: unknown
}

const nativeBridgeOrigin = 'http://127.0.0.1:18765'
const browserStorageKey = 'label_system_shell_config'

const defaultConfig: ShellConfig = {
  environment: 'development',
  developmentUrl: 'http://172.16.10.45:5173/',
  testUrl: 'http://172.16.10.45:5174/',
  productionUrl: 'http://172.16.10.45:5175/',
  bridgeVersion: 'pda-bridge-v1',
}

function isEnvironment(value: unknown): value is ShellEnvironment {
  return value === 'development' || value === 'test' || value === 'production'
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

  return {
    environment: candidate.environment,
    developmentUrl: normalizeUrl(String(candidate.developmentUrl || '')),
    testUrl: normalizeUrl(String(candidate.testUrl || '')),
    productionUrl: normalizeUrl(String(candidate.productionUrl || '')),
    bridgeVersion: String(candidate.bridgeVersion || defaultConfig.bridgeVersion),
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

export async function saveShellConfig(value: ShellConfig): Promise<void> {
  const config = parseConfig(value)

  if (!isNativeShell()) {
    localStorage.setItem(browserStorageKey, JSON.stringify(config))
    const nextUrl = config[`${config.environment}Url`]
    if (new URL(nextUrl).href !== window.location.href) {
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

export function createDefaultShellConfig(): ShellConfig {
  return { ...defaultConfig }
}
