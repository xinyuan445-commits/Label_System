import { getRuntimeConfig } from './runtime-config'

const SESSION_KEY_PREFIX = 'label-system.session'

export interface AuthSession {
  username: string
  signedInAt: number
  expiresAt: number
}

export interface SignInResult {
  success: boolean
  message?: string
  session?: AuthSession
}

function getSessionKey(): string {
  return `${SESSION_KEY_PREFIX}.${getRuntimeConfig().environment}`
}

export function readSession(): AuthSession | null {
  const raw = window.localStorage.getItem(getSessionKey())

  if (!raw) {
    return null
  }

  try {
    const session = JSON.parse(raw) as AuthSession

    if (!session.username || !session.expiresAt || session.expiresAt <= Date.now()) {
      clearSession()
      return null
    }

    return session
  } catch {
    clearSession()
    return null
  }
}

export function hasValidSession(): boolean {
  return readSession() !== null
}

export async function signIn(username: string, password: string): Promise<SignInResult> {
  const config = getRuntimeConfig()

  if (config.environment === 'development' && config.enableMockLogin) {
    if (username !== 'admin' || password !== 'admin') {
      return { success: false, message: '账号或密码错误' }
    }

    const signedInAt = Date.now()
    const session: AuthSession = {
      username,
      signedInAt,
      expiresAt: signedInAt + config.offlineSessionHours * 60 * 60 * 1000,
    }

    window.localStorage.setItem(getSessionKey(), JSON.stringify(session))
    return { success: true, session }
  }

  return { success: false, message: '登录服务未配置' }
}

export function clearSession(): void {
  window.localStorage.removeItem(getSessionKey())
}
