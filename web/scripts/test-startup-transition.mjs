import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

const overlay = await readFile(new URL('../../android-bridge/assets/startup-overlay.html', import.meta.url), 'utf8')
const script = overlay.match(/<script>([\s\S]*?)<\/script>/)[1]

function shell() {
  let now = 0, nextId = 0
  const timers = new Map(), listeners = new Map(), classes = new Set()
  const frame = { src: 'http://h5.example:5175/', contentWindow: {} }
  const cover = { hidden: false, classList: { add: value => classes.add(value) } }
  const window = {
    addEventListener(type, handler) {
      const items = listeners.get(type) ?? []; items.push(handler); listeners.set(type, items)
    },
    dispatchEvent(event) { for (const fn of listeners.get(event.type) ?? []) fn(event) },
  }
  vm.runInNewContext(script, {
    window, document: { getElementById: id => id === 'app' ? frame : cover }, URL,
    Event: class { constructor(type) { this.type = type } },
    setTimeout(fn, delay) { const id = ++nextId; timers.set(id, { fn, at: now + delay }); return id },
    clearTimeout(id) { timers.delete(id) },
  })
  function advance(ms) {
    const end = now + ms
    while (true) {
      const item = [...timers].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0]
      if (!item) break
      now = item[1].at; timers.delete(item[0]); item[1].fn()
    }
    now = end
  }
  function ready(overrides = {}) {
    window.dispatchEvent({ type: 'message', source: frame.contentWindow, origin: 'http://h5.example:5175',
      data: { source: 'label-system-h5', type: 'app-ready' }, ...overrides })
  }
  return { window, frame, cover, classes, advance, ready }
}

test('页面提前就绪立即淡出，不等满 720ms；重复 ready 不重新播放', () => {
  const s = shell(); s.ready()
  assert.equal(s.classes.has('leaving'), true)
  s.advance(150); assert.equal(s.cover.hidden, true)
  s.ready(); s.advance(10000); assert.equal(s.cover.hidden, true)
})

test('忽略其他窗口、其他来源和无效消息', () => {
  const s = shell()
  s.ready({ source: {} }); s.ready({ origin: 'http://untrusted.example' })
  s.ready({ data: null }); s.ready({ data: { source: 'wrong', type: 'app-ready' } })
  s.ready({ data: { source: 'label-system-h5', type: 'config-request' } })
  s.advance(1000); assert.equal(s.classes.has('leaving'), false)
  s.ready(); s.advance(150); assert.equal(s.cover.hidden, true)
})

test('地址改变后，只接受当前 iframe 来源', () => {
  const s = shell(); s.frame.src = 'http://other-h5.example:5175/'
  s.ready(); assert.equal(s.classes.has('leaving'), false)
  s.ready({ origin: 'http://other-h5.example:5175' }); s.advance(150)
  assert.equal(s.cover.hidden, true)
})

test('连接失败优先，遮罩不阻塞离线操作；不依赖 transitionend', () => {
  const s = shell(); s.window.dispatchEvent({ type: 'label-startup-offline' }); s.advance(150)
  assert.equal(s.cover.hidden, true)
  assert.match(overlay, /pointer-events:none/)
  assert.match(overlay, /prefers-reduced-motion:reduce/)
})

test('没有 ready 时有超时出口，已就绪不会误报超时', () => {
  const s = shell(); let timedOut = 0
  s.window.addEventListener('label-startup-timeout', () => timedOut++)
  s.advance(10000); assert.equal(timedOut, 1)
  s.advance(150); assert.equal(s.cover.hidden, true)
  const ready = shell(); let falseTimeout = 0
  ready.window.addEventListener('label-startup-timeout', () => falseTimeout++)
  ready.ready(); ready.advance(11000); assert.equal(falseTimeout, 0)
})

const source = await readFile(new URL('../src/bridge/startup-ready.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText
function h5(embedded) {
  const frames = [], messages = [], exports = {}
  const window = { requestAnimationFrame: fn => frames.push(fn) }
  window.parent = embedded ? { postMessage: (...args) => messages.push(args) } : window
  vm.runInNewContext(compiled, { window, exports })
  return { frames, messages, notify: exports.notifyStartupReady }
}

test('H5 等待两帧后通知，仅向精确本机桥地址发送', () => {
  const h = h5(true); h.notify(); assert.equal(h.messages.length, 0)
  h.frames.shift()(); assert.equal(h.messages.length, 0)
  h.frames.shift()(); assert.equal(h.messages.length, 1)
  assert.equal(h.messages[0][1], 'http://127.0.0.1:18765')
  assert.equal(h.messages[0][0].type, 'app-ready')
})

test('直接打开网页不发送桥消息或启动动画', () => {
  const h = h5(false); h.notify()
  assert.equal(h.frames.length, 0); assert.equal(h.messages.length, 0)
})
