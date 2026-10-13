import { ApiError, apiRequest } from '@/api/client'
import {
  clearStoredAuthSession,
  isAuthUser,
  writeLastLoginUsername,
  readStoredAuthSession,
  writeStoredAuthSession,
  type AuthSession,
  type AuthUser,
} from '@/services/auth-session'
import { clearUserFunctionAccess, saveUserFunctionAccess } from '@/services/feature-visibility'

export type { AuthSession, AuthUser } from '@/services/auth-session'

export interface SignInResult {
  success: boolean
  message?: string
  session?: AuthSession
}

export function readSession(): AuthSession | null {
  return readStoredAuthSession()
}

export function hasValidSession(): boolean {
  return readSession() !== null
}

async function refreshFunctionAccess(userId: string): Promise<void> {
  const functions = await apiRequest<Array<{ code: string }>>('/api/v1/functions')
  saveUserFunctionAccess(userId, functions.map((item) => item.code))
}

export async function signIn(username: string, password: string): Promise<SignInResult> {
  try {
    const result = await apiRequest<{
      token: string
      expiresAt: string
      expiresInSeconds: number
      user: AuthUser
    }>('/api/v1/auth/login', {
      auth: false,
      method: 'POST',
      body: JSON.stringify({ username, password }),
    })
    const signedInAt = Date.now()
    const serverExpiresAt = Date.parse(result.expiresAt)
    const expiresAt =
      Number.isFinite(result.expiresInSeconds) && result.expiresInSeconds > 0
        ? signedInAt + result.expiresInSeconds * 1000
        : serverExpiresAt
    if (
      !result.token ||
      !isAuthUser(result.user) ||
      !Number.isFinite(expiresAt) ||
      expiresAt <= signedInAt
    ) {
      return { success: false, message: '登录服务返回无效会话。' }
    }
    const session: AuthSession = {
      token: result.token,
      user: result.user,
      signedInAt,
      expiresAt,
    }
    writeStoredAuthSession(session)
    window.dispatchEvent(new Event('label-system:auth-changed'))
    try {
      await refreshFunctionAccess(session.user.id)
    } catch (error) {
      clearUserFunctionAccess(session.user.id)
      clearStoredAuthSession()
      return {
        success: false,
        message: error instanceof ApiError ? error.message : '读取功能权限失败。',
      }
    }
    writeLastLoginUsername(username)
    return { success: true, session }
  } catch (error) {
    return {
      success: false,
      message: error instanceof ApiError ? error.message : '登录失败。',
    }
  }
}

export function clearSession(): void {
  const session = readStoredAuthSession()
  if (session) clearUserFunctionAccess(session.user.id)
  clearStoredAuthSession()
}

export async function restoreSession(): Promise<AuthSession | null> {
  const session = readStoredAuthSession()
  if (!session) return null

  try {
    const user = await apiRequest<AuthUser>('/api/v1/auth/me')
    const restored = { ...session, user }
    writeStoredAuthSession(restored)
    try {
      await refreshFunctionAccess(user.id)
    } catch (error) {
      clearUserFunctionAccess(user.id)
      if (error instanceof ApiError && error.status === 401) {
        clearStoredAuthSession()
        return null
      }
    }
    return restored
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      clearStoredAuthSession()
      return null
    }
    return session
  }
}

export async function signOut(): Promise<void> {
  try {
    if (readStoredAuthSession()) {
      await apiRequest<{ loggedOut: boolean }>('/api/v1/auth/logout', { method: 'POST' })
    }
  } catch {
    // 退出始终清除本机会话，服务端会话由自身过期机制兜底。
  } finally {
    const session = readStoredAuthSession()
    if (session) clearUserFunctionAccess(session.user.id)
    clearStoredAuthSession()
    window.dispatchEvent(new Event('label-system:auth-changed'))
  }
}
