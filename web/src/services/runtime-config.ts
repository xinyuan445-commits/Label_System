export type AppEnvironment = 'development' | 'test' | 'production'

export interface RuntimeConfig {
  environment: AppEnvironment
  apiBaseUrl: string
  h5Version: string
  bridgeProtocol: string
  enableMockLogin: boolean
  enableMockScanner: boolean
  enableVConsole: boolean
  offlineSessionHours: number
}

const fallbackConfig: RuntimeConfig = {
  environment: 'development',
  apiBaseUrl: 'http://127.0.0.1:3000',
  h5Version: '0.1.0-dev.1',
  bridgeProtocol: 'pda-bridge-v1',
  enableMockLogin: true,
  enableMockScanner: true,
  enableVConsole: false,
  offlineSessionHours: 24,
}

let runtimeConfig = fallbackConfig

function isEnvironment(value: unknown): value is AppEnvironment {
  return value === 'development' || value === 'test' || value === 'production'
}

function parseRuntimeConfig(value: unknown): RuntimeConfig {
  if (!value || typeof value !== 'object') {
    throw new Error('运行配置格式错误')
  }

  const candidate = value as Partial<RuntimeConfig>

  if (!isEnvironment(candidate.environment)) {
    throw new Error('运行环境无效')
  }

  return {
    environment: candidate.environment,
    apiBaseUrl: String(candidate.apiBaseUrl || ''),
    h5Version: String(candidate.h5Version || fallbackConfig.h5Version),
    bridgeProtocol: String(candidate.bridgeProtocol || fallbackConfig.bridgeProtocol),
    enableMockLogin: candidate.enableMockLogin === true,
    enableMockScanner: candidate.enableMockScanner === true,
    enableVConsole: candidate.enableVConsole === true,
    offlineSessionHours:
      typeof candidate.offlineSessionHours === 'number' && candidate.offlineSessionHours > 0
        ? candidate.offlineSessionHours
        : fallbackConfig.offlineSessionHours,
  }
}

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}runtime-config.json`, {
      cache: 'no-store',
    })

    if (!response.ok) {
      throw new Error(`运行配置加载失败：${response.status}`)
    }

    runtimeConfig = parseRuntimeConfig(await response.json())
  } catch (error) {
    if (import.meta.env.PROD) {
      throw error
    }

    runtimeConfig = fallbackConfig
  }

  return runtimeConfig
}

export function getRuntimeConfig(): RuntimeConfig {
  return runtimeConfig
}
