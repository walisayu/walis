#!/usr/bin/env node
// Local check for an action pack. No dependencies: `node scripts/validate.mjs [dir]`.
// Implements every rule tagged [check] in docs/action-development.md. The format rules match the
// Walis server (ActionManifest.TryParse / ActionPackLoader), so what passes here loads there.
import { readdirSync, readFileSync, statSync, existsSync } from 'node:fs'
import { join, relative, basename, extname } from 'node:path'

const root = process.argv[2] ?? process.cwd()
const categories = ['general', 'file', 'git', 'node', 'dotnet', 'python', 'java', 'go', 'rust', 'ruby', 'php', 'container', 'kubernetes', 'database', 'cloud', 'deploy', 'check', 'test', 'security', 'notify', 'archive']
const types = ['text', 'multiline', 'number', 'boolean', 'select', 'path']
const reservedPrefixes = ['walis', 'shell', 'file', 'git', 'node', 'dotnet', 'python', 'java', 'go', 'rust', 'ruby', 'php', 'docker', 'k8s', 'db', 'cloud', 'deploy', 'check', 'test', 'security', 'notify', 'archive']
const rawAllowedNames = ['command', 'script', 'arguments']
const rawAllowedTypes = ['number', 'boolean', 'select']
const idPattern = /^[a-z0-9]+(\.[a-z0-9-]+)+$/
const parameterNamePattern = /^[a-z][a-zA-Z0-9]*$/
const versionPattern = /^\d+\.\d+\.\d+$/
const placeholderPattern = /(\{\{\{?)(?:#(?:if|unless)\s+)?([A-Za-z_][A-Za-z0-9_]*)\}?\}\}/g

// Rejected outright.
const forbidden = [
  [/\b(curl|wget)\b[^\n|]*\|\s*(sudo\s+)?(sh|bash|zsh)\b/, 'download-and-execute (curl/wget | sh)'],
  [/(^|[\s;&|])sudo\s/, 'sudo'],
  [/\brm\s+-[a-zA-Z]*r[a-zA-Z]*\s+(\/|~|\$HOME)(\s|$|\/\*)/, 'rm -rf on / or the home directory'],
  [/(>>?|tee)\s*\/etc\//, 'writing to /etc'],
]
// Warned; the reviewer wants a reason.
const suspicious = [
  [/\beval\b/, 'eval'],
  [/\$HOME\b|(^|[\s"'=])~\//, 'home directory ($HOME, ~/)'],
  [/\bnohup\b/, 'nohup'],
  [/[^&]&\s*$/m, '& backgrounding'],
  [/\bchmod\s+(-R\s+)?777\b/, 'chmod 777'],
  [/\|\|\s*true\b/, '|| true swallows errors'],
]

const errors = []
const warnings = []
const fail = (file, message) => errors.push(`${file}: ${message}`)
const warn = (file, message) => warnings.push(`${file}: ${message}`)

const packPath = join(root, 'pack.json')
if (!existsSync(packPath)) {
  fail('pack.json', 'missing pack.json')
} else {
  try {
    const pack = JSON.parse(readFileSync(packPath, 'utf8'))
    if (!/^[a-z0-9][a-z0-9_-]{0,63}$/.test(pack.name ?? '')) fail('pack.json', 'name must be lowercase letters, digits, - or _')
    if (!versionPattern.test(pack.version ?? '')) fail('pack.json', 'version must be x.y.z')
  } catch (error) {
    fail('pack.json', `invalid JSON: ${error.message}`)
  }
}

function* walk(dir) {
  for (const entry of readdirSync(dir).sort()) {
    const full = join(dir, entry)
    const info = statSync(full)
    if (info.isDirectory()) yield* walk(full)
    else if (extname(entry) === '.json') yield full
  }
}

const actionsDir = join(root, 'actions')
if (!existsSync(actionsDir)) fail('actions/', 'missing actions/ directory')

const seen = new Map()
let count = 0
for (const file of existsSync(actionsDir) ? walk(actionsDir) : []) {
  count += 1
  const rel = relative(root, file)
  let action
  try {
    action = JSON.parse(readFileSync(file, 'utf8'))
  } catch (error) {
    fail(rel, `invalid JSON: ${error.message}`)
    continue
  }

  // 1. id and file
  const id = String(action.id ?? '')
  if (!idPattern.test(id) || id.length > 128) fail(rel, 'id must be <prefix>.<action>: lowercase, at least one dot, at most 128 characters')
  else if (reservedPrefixes.includes(id.split('.')[0])) fail(rel, `id prefix "${id.split('.')[0]}" is reserved for built-in actions; use your own prefix`)
  if (basename(file, '.json') !== id) fail(rel, `file name must equal the id (${id}.json)`)
  if (seen.has(id)) fail(rel, `id "${id}" duplicates ${seen.get(id)}`)
  seen.set(id, rel)

  // 2. display fields
  const name = typeof action.name === 'string' ? action.name.trim() : ''
  if (!name) fail(rel, 'name is required')
  else {
    if (name.length > 40) fail(rel, 'name is at most 40 characters')
    if (/[。.!！]$/.test(name)) fail(rel, 'name must not end with a period')
  }
  if (typeof action.description !== 'string' || !action.description.trim()) fail(rel, 'description is required: what it does and which tools it needs')
  if (!action.category) fail(rel, 'category is required')
  else if (!categories.includes(action.category)) fail(rel, `category "${action.category}" is not one of: ${categories.join(', ')}`)
  if (typeof action.icon !== 'string' || !action.icon.startsWith('cil-')) fail(rel, 'icon is required and must be a CoreUI icon starting with cil-')
  if (!versionPattern.test(action.version ?? '')) fail(rel, 'version is required, format x.y.z')
  if (typeof action.author !== 'string' || !action.author.trim()) fail(rel, 'author is required')

  // 3. parameters
  const parameters = Array.isArray(action.parameters) ? action.parameters : []
  if (parameters.length > 64) fail(rel, 'at most 64 parameters')
  else if (parameters.length > 8) warn(rel, `${parameters.length} parameters; consider splitting the action`)
  const byName = new Map()
  for (const parameter of parameters) {
    const pname = String(parameter?.name ?? '')
    if (!parameterNamePattern.test(pname)) fail(rel, `parameter name "${pname}" must be lowerCamelCase`)
    if (byName.has(pname)) fail(rel, `parameter "${pname}" is duplicated`)
    byName.set(pname, parameter)
    if (!parameter?.label) fail(rel, `parameter "${pname}" has no label`)
    const type = parameter?.type ?? 'text'
    if (!types.includes(type)) fail(rel, `parameter "${pname}" has unsupported type "${type}"`)
    if (type === 'boolean' && !['true', 'false'].includes(parameter.default)) fail(rel, `boolean parameter "${pname}" needs default "true" or "false"`)
    if (type === 'select') {
      const options = Array.isArray(parameter.options) ? parameter.options : []
      if (!options.length) fail(rel, `select parameter "${pname}" needs options`)
      else if (parameter.default != null && !options.some((option) => option?.value === parameter.default)) fail(rel, `select parameter "${pname}" has a default that is not one of its options`)
    }
    if (type === 'number' && parameter.default != null && parameter.default !== '' && Number.isNaN(Number(parameter.default))) fail(rel, `number parameter "${pname}" has a non-numeric default`)
    if (/(token|password|secret|apiKey|apikey|credential)/i.test(pname)) warn(rel, `parameter "${pname}" looks like a secret; secrets belong in the job's secret environment variables, not parameters`)
  }

  // 3b. outputs
  const outputs = Array.isArray(action.outputs) ? action.outputs : []
  if (outputs.length > 32) fail(rel, 'at most 32 outputs')
  const outputNames = new Set()
  for (const output of outputs) {
    const oname = String(output?.name ?? '')
    if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(oname)) fail(rel, `output name "${oname}" must be letters, digits and underscores`)
    if (outputNames.has(oname)) fail(rel, `output "${oname}" is duplicated`)
    outputNames.add(oname)
    if (typeof output?.description !== 'string' || !output.description.trim()) fail(rel, `output "${oname}" needs a description`)
  }

  // 4. template
  const template = typeof action.template === 'string' ? action.template : ''
  if (!template.trim()) fail(rel, 'template is required')
  if (template.length > 100000) fail(rel, 'template is at most 100,000 characters')
  else if (template.split('\n').length > 40) warn(rel, 'template is longer than 40 lines; consider calling a script in the repository instead')
  for (const match of template.matchAll(placeholderPattern)) {
    const [, open, pname] = match
    const parameter = byName.get(pname)
    if (!parameter) {
      fail(rel, `template uses undeclared parameter "${pname}"`)
      continue
    }
    if (open === '{{{' && !rawAllowedTypes.includes(parameter.type ?? 'text') && !rawAllowedNames.includes(pname)) {
      fail(rel, `raw "{{{${pname}}}}" is only allowed for number/boolean/select parameters or parameters named ${rawAllowedNames.join(', ')}; use {{${pname}}}`)
    }
  }
  if (/\$\{\{\s*steps\./.test(template)) fail(rel, 'template must not contain ${{ steps.x.y }}; that syntax is for users to type into parameter values')
  if (outputs.length && !template.includes('WALIS_OUTPUT_FILE')) warn(rel, 'outputs are declared but the template never writes $WALIS_OUTPUT_FILE')
  for (const [pattern, label] of forbidden) if (pattern.test(template)) fail(rel, `template contains forbidden pattern: ${label}`)
  for (const [pattern, label] of suspicious) if (pattern.test(template)) warn(rel, `template contains a pattern that needs review: ${label}`)
}

for (const line of warnings) console.warn(`warning ${line}`)
if (errors.length) {
  for (const line of errors) console.error(`error ${line}`)
  console.error(`\n${errors.length} error(s), ${warnings.length} warning(s), ${count} action(s).`)
  process.exit(1)
}
console.log(`OK: ${count} action(s) passed${warnings.length ? `; ${warnings.length} warning(s) need a reason in the PR` : ''}.`)
