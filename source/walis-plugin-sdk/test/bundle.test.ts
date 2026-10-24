import assert from 'node:assert/strict'
import { mkdtemp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { buildPlugin, inspect } from '../src/bundle.js'

async function writePlugin(source: string): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), 'walis-sdk-'))
  const entry = join(directory, 'plugin.ts')
  await writeFile(entry, source, 'utf8')
  return entry
}

test('打包後把 render 掛成全域', async () => {
  const entry = await writePlugin(`
    export default {
      manifest: { id: 'acme.demo', name: '示範' },
      render: (inputs, ctx) => 'echo ' + ctx.shell.quote(inputs.value ?? '')
    }
  `)

  const result = await buildPlugin(entry)

  // The host runs engine.Execute(code) and then reads the global render, so this line is required.
  assert.match(result.code, /globalThis\.render\s*=/)
  assert.equal(result.manifest.id, 'acme.demo')
  assert.equal(result.manifest.apiVersion, 1)
  assert.equal(result.manifest.kind, 'action')
  assert.deepEqual(result.manifest.permissions, { http: [], secrets: [], kv: false })
})

test('沒有 render 就報錯', async () => {
  const entry = await writePlugin(`export default { manifest: { id: 'acme.x', name: 'X' } }`)

  await assert.rejects(() => buildPlugin(entry), /render/)
})

test('抓出 host 沒有的 API', () => {
  assert.deepEqual(inspect('const fs = require("fs")').length > 0, true)
  assert.match(inspect('await something()').join(), /await/)
  assert.match(inspect('setTimeout(() => {}, 10)').join(), /setTimeout/)
  assert.match(inspect('fetch("https://x.test")').join(), /ctx\.http\.fetch/)

  // ctx.http.fetch is legitimate and must not be flagged.
  assert.deepEqual(inspect('ctx.http.fetch("https://x.test")'), [])
})

test('程式碼過長會警告', () => {
  assert.match(inspect(`const x = "${'a'.repeat(21_000)}"`).join(), /20,000/)
})
