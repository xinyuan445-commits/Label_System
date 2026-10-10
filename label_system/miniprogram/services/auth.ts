const SESSION_KEY = 'label-system-session'

const DEFAULT_USERNAME = 'admin'
const DEFAULT_PASSWORD = 'admin'

export interface AuthSession {
  username: string
  signedInAt: number
}

export interface SignInResult {
  success: boolean
  message?: string
}

export function getSession(): AuthSession | null {
  const session = wx.getStorageSync<AuthSession | null>(SESSION_KEY)

  if (!session || typeof session.username !== 'string') {
    return null
  }

  return session
}

export function signIn(username: string, password: string): SignInResult {
  if (username !== DEFAULT_USERNAME || password !== DEFAULT_PASSWORD) {
    return {
      success: false,
      message: '账号或密码错误',
    }
  }

  wx.setStorageSync<AuthSession>(SESSION_KEY, {
    username,
    signedInAt: Date.now(),
  })

  return { success: true }
}

export function signOut(): void {
  wx.removeStorageSync(SESSION_KEY)
}
