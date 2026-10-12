import { getRuntimeConfig } from '@/services/runtime-config'

const SESSION_KEY_PREFIX = 'label-system.session'
let volatileSession: AuthSession | null = null

export interface AuthUser {
  id: string
  name: string
  roles: string[]
}

export interface AuthSession {
  token: string
  user: AuthUser
  signedInAt: number
  expiresAt: number
}

function getSessionKey(): string {
  return `${SESSION_KEY_PREFIX}.${getRuntimeConfig().environment}`
}

export function isAuthUser(value: unknown): value is AuthUser {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<AuthUser>
  return (
    typeof candidate.id === 'string' &&
    candidate.id.length > 0 &&
    typeof candidate.name === 'string' &&
    Array.isArray(candidate.roles) &&
    candidate.roles.every((role) => typeof role === 'string')
  )
}

export function readStoredAuthSession(): AuthSession | null {
  let raw: string | null = null
  try {
    raw = window.localStorage.getItem(getSessionKey())
  } catch {
    return readVolatileSession()
  }
  if (!raw) return readVolatileSession()

  try {
    const session = JSON.parse(raw) as Partial<AuthSession>
    if (
      typeof session.token !== 'string' ||
      !session.token ||
      !isAuthUser(session.user) ||
      typeof session.signedInAt !== 'number' ||
      typeof session.expiresAt !== 'number' ||
      session.expiresAt <= Date.now()
    ) {
      clearStoredAuthSession()
      return null
    }
    volatileSession = session as AuthSession
    return volatileSession
  } catch {
    clearStoredAuthSession()
    return readVolatileSession()
  }
}

export function writeStoredAuthSession(session: AuthSession): void {
  volatileSession = session
  try {
    window.localStorage.setItem(getSessionKey(), JSON.stringify(session))
  } catch {
    // 部分 PDA WebView 会限制持久存储；当前进程仍使用内存会话。
  }
}

export function clearStoredAuthSession(): void {
  volatileSession = null
  try {
    window.localStorage.removeItem(getSessionKey())
  } catch {
    // 存储不可用时清除内存会话即可。
  }
}

function readVolatileSession(): AuthSession | null {
  if (volatileSession && volatileSession.expiresAt > Date.now()) return volatileSession
  volatileSession = null
  return null
}
