import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'

// Execute the actual generated wrapper JavaScript, not a reimplementation.
const java = await readFile(new URL('../../android-bridge/src/com/xinyuan/labelbridge/PdaBridgeServer.java', import.meta.url), 'utf8')
const section = java.slice(java.indexOf('+ "const app=document'), java.indexOf('+ "</script></body></html>";'))
const script = [...section.matchAll(/"(?:\\.|[^"\\])*"/g)].map(match => JSON.parse(match[0])).join('')
  .replace('let config=,checking=', 'let config={environment:"development",developmentUrl:"http://h5.example:5175/"},checking=')

function shell(remote = async () => { throw new Error('offline') }) {
  const listeners = new Map(), nodes = new Map(), sent = []
  const classes = new Set()
  const get = id => {
    if (!nodes.has(id)) nodes.set(id, { value: '', textContent: '', src: 'http://h5.example:5175/',
      contentWindow: { postMessage(message, origin) { sent.push({ message, origin }) } }, addEventListener() {}, focus() {},
      classList: { add: key => classes.add(key), remove: key => classes.delete(key), contains: key => classes.has(key) } })
    return nodes.get(id)
  }
  let requests = 0
  const context = vm.createContext({
    document: { getElementById: get },
    window: { addEventListener(type, fn) { listeners.set(type, fn) }, dispatchEvent() {} },
    history: { pushState() {}, back() {} }, location: { href: 'http://127.0.0.1:18765/' },
    URL, URLSearchParams, Event: class { constructor(type) { this.type = type } },
    setTimeout() { return 1 }, setInterval() { return 1 },
    fetch: async url => {
      if (url.startsWith('/')) return { ok: true, json: async () => [] }
      requests++; return remote(url)
    },
  })
  vm.runInContext(script, context)
  return {
    get requests() { return requests },
    sent,
    covered: () => classes.has('visible'),
    check: force => vm.runInContext(`checkTarget(${force})`, context),
    async collection(enabled, overrides = {}) {
      await listeners.get('message')({ source: get('app').contentWindow, origin: 'http://h5.example:5175',
        data: { source: 'label-system-h5', type: 'offline-collection', payload: { enabled } }, ...overrides })
    },
  }
}

test('已准备采集页面断网不触发远程重载或遮罩', async () => {
  const state = shell()
  await state.collection(true)
  await state.check(false)
  assert.equal(state.requests, 0)
  assert.equal(state.covered(), false)
  assert.equal(state.sent.at(-1).message.type, 'offline-collection-ready')
  assert.equal(state.sent.at(-1).origin, 'http://h5.example:5175')
})

test('先前未结束的探测失败也不能盖住刚恢复的本机任务', async () => {
  let rejectRemote
  const state = shell(() => new Promise((_, reject) => { rejectRemote = reject }))
  const checking = state.check(false)
  await state.collection(true)
  rejectRemote(new Error('offline'))
  await checking
  assert.equal(state.covered(), false)
})

test('离开采集后保留原来的启动故障提示', async () => {
  const state = shell()
  await state.collection(true)
  await state.collection(false)
  await state.check(false)
  assert.equal(state.requests, 1)
  assert.equal(state.covered(), true)
})

test('伪造来源或窗口的消息不能关闭健康探测', async () => {
  for (const override of [{ origin: 'http://other.example' }, { source: {} }]) {
    const state = shell()
    await state.collection(true, override)
    await state.check(false)
    assert.equal(state.requests, 1)
    assert.equal(state.covered(), true)
  }
})
