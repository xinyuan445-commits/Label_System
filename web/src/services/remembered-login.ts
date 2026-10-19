export interface RememberedLogin {
  username: string
  password: string
}

// 仅在用户主动选择后保存到当前浏览器/WebView；不写入漫游设置或日志。
export class RememberedLoginStore {
  private readonly key: string

  constructor(environment: string, apiBaseUrl: string, private readonly storage: Storage | null) {
    this.key = `label-system.remembered-login.${environment}.${encodeURIComponent(apiBaseUrl.replace(/\/+$/, ''))}`
  }

  read(): RememberedLogin | null {
    try {
      const raw = this.storage?.getItem(this.key)
      if (!raw) return null
      const value: unknown = JSON.parse(raw)
      if (!value || typeof value !== 'object') return null
      const candidate = value as Partial<RememberedLogin>
      if (typeof candidate.username !== 'string' || !candidate.username.trim() ||
          typeof candidate.password !== 'string' || !candidate.password) return null
      return { username: candidate.username.trim(), password: candidate.password }
    } catch {
      return null
    }
  }

  save(username: string, password: string): boolean {
    if (!this.storage || !username.trim() || !password) return false
    try {
      this.storage.setItem(this.key, JSON.stringify({ username: username.trim(), password }))
      return true
    } catch {
      return false
    }
  }

  clear(): boolean {
    if (!this.storage) return true
    try {
      this.storage.removeItem(this.key)
      return true
    } catch {
      return false
    }
  }
}
