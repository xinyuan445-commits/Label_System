Page({
  data: {
    url: '',
  },

  onLoad(options: Record<string, string | undefined>) {
    const encodedUrl = options.url

    if (!encodedUrl) {
      wx.navigateBack()
      return
    }

    const url = decodeURIComponent(encodedUrl)

    if (!/^https?:\/\//i.test(url)) {
      wx.navigateBack()
      return
    }

    this.setData({ url })
  },

  handleLoadError() {
    wx.showToast({
      title: '页面加载失败',
      icon: 'none',
    })
  },
})
