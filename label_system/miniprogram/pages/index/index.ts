import { getSession, signOut } from '../../services/auth'

Page({
  data: {
    username: '',
  },

  onShow() {
    const session = getSession()

    if (!session) {
      wx.reLaunch({ url: '/pages/login/login' })
      return
    }

    this.setData({ username: session.username })
  },

  handleLogout() {
    signOut()
    wx.reLaunch({ url: '/pages/login/login' })
  },
})
