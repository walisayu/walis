import assert from 'node:assert/strict'
import { test } from 'node:test'
import { isAllowed, renderPlugin } from '../src/harness.js'
import type { PluginDefinition } from '../src/types.js'

const plugin: PluginDefinition = {
  manifest: {
    id: 'test.echo',
    name: '輸出',
    parameters: [
      { name: 'message', label: '訊息' },
      { name: 'loud', label: '大聲', type: 'boolean', default: 'false' },
      { name: 'prefix', label: '前綴', default: '>>' }
    ]
  },
  render: (inputs, ctx) => {
    ctx.log('開始產生')
    return `echo ${ctx.shell.quote(inputs.prefix + inputs.message)}${inputs.loud ? ' | tr a-z A-Z' : ''}`
  }
}

test('沒填的參數套用預設值', () => {
  const { script } = renderPlugin(plugin, { message: 'hi' })

  assert.equal(script, "echo '>>hi'")
})

test('boolean 沒勾就是空字串', () => {
  const off = renderPlugin(plugin, { message: 'hi' })
  const on = renderPlugin(plugin, { message: 'hi', loud: 'true' })

  assert.doesNotMatch(off.script, /tr a-z/)
  assert.match(on.script, /tr a-z/)
})

test('log 會被收集起來', () => {
  const { logs } = renderPlugin(plugin, { message: 'hi' })

  assert.deepEqual(logs, ['開始產生'])
})

test('沒宣告的網域連不出去', () => {
  const network: PluginDefinition = {
    manifest: { id: 'test.http', name: 'HTTP', permissions: { http: ['https://allowed.example'] } },
    render: (_inputs, ctx) => ctx.http.fetch('https://evil.example').body
  }

  assert.throws(
    () => renderPlugin(network, {}, { fetch: () => ({ status: 200, ok: true, body: 'x' }) }),
    /did not declare access/
  )
})

test('宣告過的網域會走替身並記錄下來', () => {
  const network: PluginDefinition = {
    manifest: { id: 'test.http', name: 'HTTP', permissions: { http: ['https://allowed.example'] } },
    render: (_inputs, ctx) => `echo ${ctx.shell.quote(ctx.http.fetch('https://allowed.example/x').body)}`
  }

  const result = renderPlugin(network, {}, {
    fetch: () => ({ status: 200, ok: true, body: '假的回應' })
  })

  assert.equal(result.script, "echo '假的回應'")
  assert.equal(result.fetches.length, 1)
  assert.equal(result.fetches[0].url, 'https://allowed.example/x')
})

test('網域比對只看協定與主機', () => {
  const allowed = ['https://api.example.com']

  assert.equal(isAllowed('https://api.example.com/v1/issues', allowed), true)
  assert.equal(isAllowed('http://api.example.com/v1', allowed), false)
  assert.equal(isAllowed('https://api.example.com.evil.test', allowed), false)
  assert.equal(isAllowed('不是網址', allowed), false)
})

test('環境變數是唯讀的快照', () => {
  const reader: PluginDefinition = {
    manifest: { id: 'test.vars', name: '變數' },
    render: (_inputs, ctx) => `echo ${ctx.shell.quote(ctx.variables.BRANCH ?? '')}`
  }

  const { script } = renderPlugin(reader, {}, { variables: { BRANCH: 'main' } })

  assert.equal(script, "echo 'main'")
})
