import assert from 'node:assert/strict'
import { test } from 'node:test'
import { renderPlugin } from '@walisayu/plugin-sdk'
import plugin from './plugin.js'

test('produces the deploy command', () => {
  const { script } = renderPlugin(plugin, { target: 'stg-01' })

  assert.equal(script, "scripts/deploy.sh 'stg-01'")
})

test('adds a restart line only when asked', () => {
  const { script } = renderPlugin(plugin, { target: 'stg-01', restart: 'true' })

  assert.match(script, /restart\.sh 'stg-01'/)
})

test('escapes single quotes in values', () => {
  const { script } = renderPlugin(plugin, { target: "it's" })

  // The escaping rule matches the Walis host exactly, so what you test is what runs.
  assert.equal(script, "scripts/deploy.sh 'it'\\''s'")
})
