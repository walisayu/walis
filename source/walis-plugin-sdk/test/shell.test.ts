import assert from 'node:assert/strict'
import { test } from 'node:test'
import { quote } from '../src/shell.js'

test('一般值用單引號包起來', () => {
  assert.equal(quote('stg-01'), "'stg-01'")
})

test('單引號的跳脫方式跟 host 一致', () => {
  // The C# side is value.Replace("'", "'\\''") and both must be identical,
  // otherwise the script tested locally differs from the one that actually runs.
  assert.equal(quote("it's"), "'it'\\''s'")
  assert.equal(quote("'"), "''\\'''")
})

test('空值與特殊字元', () => {
  assert.equal(quote(''), "''")
  assert.equal(quote('a b; rm -rf /'), "'a b; rm -rf /'")
  assert.equal(quote('$(whoami)'), "'$(whoami)'")
})
