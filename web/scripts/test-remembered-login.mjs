import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

// 编译真实存储模块，在隔离的内存 Storage 中测试，不读取真实账号或密码。
const source = await readFile(new URL('../src/services/remembered-login.ts', import.meta.url), 'utf8')
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
})
const { RememberedLoginStore } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`)

function memoryStorage() {
  const data = new Map()
  return {
    data,
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
    removeItem: (key) => data.delete(key),
  }
}

test('默认没有密码；保存后可恢复，保留密码中的空白', () => {
  const storage = memoryStorage()
  const store = new RememberedLoginStore('production', 'http://api.example:9890/', storage)
  assert.equal(store.read(), null)
  assert.equal(store.save(' tester ', ' test password '), true)
  assert.deepEqual(new RememberedLoginStore('production', 'http://api.example:9890', storage).read(), {
    username: 'tester', password: ' test password ',
  })
})

test('环境和 API 地址隔离；取消记住后清除当前范围', () => {
  const storage = memoryStorage()
  const prod = new RememberedLoginStore('production', 'http://api.example:9890', storage)
  prod.save('tester', 'test-value')
  assert.equal(new RememberedLoginStore('development', 'http://api.example:9890', storage).read(), null)
  assert.equal(new RememberedLoginStore('production', 'http://other.example:9890', storage).read(), null)
  assert.equal(prod.clear(), true)
  assert.equal(prod.read(), null)
  assert.equal(storage.data.size, 0)
})

test('空账号或密码不会覆盖已保存信息；成功保存新账号会替换', () => {
  const store = new RememberedLoginStore('test', 'http://api.example', memoryStorage())
  store.save('first', 'test-first')
  assert.equal(store.save(' ', 'test-invalid'), false)
  assert.equal(store.save('second', ''), false)
  assert.equal(store.read().username, 'first')
  store.save('second', 'test-second')
  assert.deepEqual(store.read(), { username: 'second', password: 'test-second' })
})

test('损坏数据及不可用的存储不会导致页面崩溃', () => {
  const storage = memoryStorage()
  const store = new RememberedLoginStore('test', 'http://api.example', storage)
  store.save('tester', 'test-value')
  const key = [...storage.data.keys()][0]
  for (const value of ['invalid', 'null', '[]', '{"username":"tester"}', '{"username":1,"password":2}']) {
    storage.setItem(key, value)
    assert.equal(store.read(), null)
  }
  const blocked = new RememberedLoginStore('test', 'http://api.example', {
    getItem() { throw new Error('blocked') },
    setItem() { throw new Error('blocked') },
    removeItem() { throw new Error('blocked') },
  })
  assert.equal(blocked.read(), null)
  assert.equal(blocked.save('tester', 'test-value'), false)
  assert.equal(blocked.clear(), false)
})
