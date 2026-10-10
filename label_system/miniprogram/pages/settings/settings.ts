type EnvironmentKey = 'development' | 'test' | 'production'

interface SavedH5Config {
  environment: EnvironmentKey
  url: string
  developmentUrl: string
  testUrl: string
  productionUrl: string
}

const STORAGE_KEY = 'label_system_h5_config'

const DEFAULT_URLS: Record<EnvironmentKey, string> = {
  development: 'http://172.16.10.45:5173/',
  test: 'http://172.16.10.45:5174/',
  production: 'http://172.16.10.45:5175/',
}

function isEnvironment(value: unknown): value is EnvironmentKey {
  return value === 'development' || value === 'test' || value === 'production'
}

function normalizeUrl(value: string): string {
  const url = value.trim()

  if (!/^https?:\/\//i.test(url)) {
    return ''
  }

  return url.endsWith('/') ? url : `${url}/`
}

Page({
  data: {
    environment: 'development' as EnvironmentKey,
    developmentUrl: DEFAULT_URLS.development,
    testUrl: DEFAULT_URLS.test,
    productionUrl: DEFAULT_URLS.production,
    errorMessage: '',
  },

  onLoad() {
    const saved = wx.getStorageSync(STORAGE_KEY) as Partial<SavedH5Config> | undefined

    if (!saved || !isEnvironment(saved.environment)) {
      return
    }

    this.setData({
      environment: saved.environment,
      developmentUrl: saved.developmentUrl || DEFAULT_URLS.development,
      testUrl: saved.testUrl || DEFAULT_URLS.test,
      productionUrl: saved.productionUrl || DEFAULT_URLS.production,
    })
  },

  handleEnvironmentChange(event: WechatMiniprogram.RadioGroupChange) {
    const environment = event.detail.value

    if (isEnvironment(environment)) {
      this.setData({ environment, errorMessage: '' })
    }
  },

  handleDevelopmentInput(event: WechatMiniprogram.Input) {
    this.setData({ developmentUrl: event.detail.value, errorMessage: '' })
  },

  handleTestInput(event: WechatMiniprogram.Input) {
    this.setData({ testUrl: event.detail.value, errorMessage: '' })
  },

  handleProductionInput(event: WechatMiniprogram.Input) {
    this.setData({ productionUrl: event.detail.value, errorMessage: '' })
  },

  handleOpen() {
    const environment = this.data.environment
    const url = normalizeUrl(this.data[`${environment}Url`])

    if (!url) {
      this.setData({ errorMessage: '请输入有效地址' })
      return
    }

    const saved: SavedH5Config = {
      environment,
      url,
      developmentUrl: normalizeUrl(this.data.developmentUrl) || this.data.developmentUrl.trim(),
      testUrl: normalizeUrl(this.data.testUrl) || this.data.testUrl.trim(),
      productionUrl: normalizeUrl(this.data.productionUrl) || this.data.productionUrl.trim(),
    }

    wx.setStorageSync(STORAGE_KEY, saved)
    wx.navigateTo({
      url: `/pages/h5/h5?url=${encodeURIComponent(url)}`,
    })
  },
})
