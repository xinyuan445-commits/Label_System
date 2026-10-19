export {}

type EnvironmentKey = 'development' | 'test' | 'production'

interface SavedH5Config {
  environment?: EnvironmentKey
  url?: string
  developmentUrl?: string
  testUrl?: string
  productionUrl?: string
}

const STORAGE_KEY = 'label_system_h5_config'
const DEFAULT_URLS: Record<EnvironmentKey, string> = {
  development: 'http://172.16.10.45:5175/',
  test: 'http://172.16.10.45:5174/',
  production: 'http://172.16.10.45:5175/',
}

function isEnvironment(value: unknown): value is EnvironmentKey {
  return value === 'development' || value === 'test' || value === 'production'
}

function bootstrapUrl(legacyUrl?: string): string {
  const saved = (wx.getStorageSync(STORAGE_KEY) || {}) as SavedH5Config
  const environment = isEnvironment(saved.environment) ? saved.environment : 'development'
  const developmentUrl = saved.developmentUrl || legacyUrl || saved.url || DEFAULT_URLS.development
  const testUrl = saved.testUrl || DEFAULT_URLS.test
  const productionUrl = saved.productionUrl || DEFAULT_URLS.production
  const query = [
    ['environment', environment],
    ['developmentUrl', developmentUrl],
    ['testUrl', testUrl],
    ['productionUrl', productionUrl],
  ]
    .map(([key, value]) => `${key}=${encodeURIComponent(value)}`)
    .join('&')

  return `http://127.0.0.1:18765/?${query}`
}

Page({
  data: {
    url: '',
  },

  onLoad(options: Record<string, string | undefined>) {
    const encodedUrl = options.url
    const legacyUrl = encodedUrl ? decodeURIComponent(encodedUrl) : undefined

    this.setData({
      url: bootstrapUrl(legacyUrl),
    })
  },

  handleLoadError() {
    wx.showToast({
      title: '页面加载失败',
      icon: 'none',
    })
  },
})
