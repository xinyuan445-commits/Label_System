import { getSession, signIn } from '../../services/auth'

Page({
  data: {
    username: 'admin',
    password: 'admin',
    errorMessage: '',
    submitting: false,
  },

  onLoad() {
    if (getSession()) {
      wx.reLaunch({ url: '/pages/index/index' })
    }
  },

  onUsernameInput(event: WechatMiniprogram.Input) {
    this.setData({
      username: event.detail.value,
      errorMessage: '',
    })
  },

  onPasswordInput(event: WechatMiniprogram.Input) {
    this.setData({
      password: event.detail.value,
      errorMessage: '',
    })
  },

  handleLogin() {
    if (this.data.submitting) {
      return
    }

    const username = this.data.username.trim()
    const password = this.data.password

    if (!username || !password) {
      this.setData({ errorMessage: '请输入账号和密码' })
      return
    }

    this.setData({ submitting: true, errorMessage: '' })
    const result = signIn(username, password)

    if (!result.success) {
      this.setData({
        submitting: false,
        errorMessage: result.message || '登录失败',
      })
      return
    }

    wx.reLaunch({ url: '/pages/index/index' })
  },
})
