#!/usr/bin/env node
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildPlugin } from './bundle.js'
import { renderPlugin } from './harness.js'
import type { PluginDefinition } from './types.js'

const usage = `walis-plugin — development tools for Walis plugins

Usage:
  walis-plugin init <dir>              Scaffold a starter project
  walis-plugin build [entry]           Bundle for Walis (entry defaults to plugin.ts)
  walis-plugin render [entry] [flags]  Run locally and print the generated shell
  walis-plugin install [entry] [flags] Bundle and upload to Walis

Flags:
  --input name=value  Parameter for render; repeatable
  --var name=value    Environment variable for render; repeatable
  --out <dir>         Output directory for build (default dist)
  --url <url>         Walis address (default $WALIS_API_URL or http://127.0.0.1:11120)
  --key <key>         API key (default $WALIS_API_KEY)
  --trusted           Mark as trusted on install (may produce shell snippets)
`

async function main(): Promise<number> {
  const [command, ...rest] = process.argv.slice(2)

  switch (command) {
    case 'init':
      return await init(rest[0])
    case 'build':
      return await runBuild(rest)
    case 'render':
      return await render(rest)
    case 'install':
      return await install(rest)
    case undefined:
    case '--help':
    case '-h':
      console.log(usage)
      return 0
    default:
      console.error(`Unknown command: ${command}\n`)
      console.log(usage)
      return 1
  }
}

async function init(target?: string): Promise<number> {
  if (!target) {
    console.error('Please give a directory: walis-plugin init my-plugin')
    return 1
  }

  const destination = resolve(process.cwd(), target)
  const template = join(dirname(fileURLToPath(import.meta.url)), '..', 'template')

  await mkdir(destination, { recursive: true })
  await cp(template, destination, { recursive: true })

  console.log(`Created ${destination}`)
  console.log('Next:')
  console.log(`  cd ${target} && npm install`)
  console.log('  npx walis-plugin render --input target=stg-01')
  return 0
}

async function runBuild(args: string[]): Promise<number> {
  const { positional, options } = parse(args)
  const entry = resolve(process.cwd(), positional[0] ?? 'plugin.ts')
  const out = resolve(process.cwd(), options.out ?? 'dist')

  const result = await buildPlugin(entry)

  await mkdir(out, { recursive: true })
  await writeFile(join(out, 'plugin.js'), result.code, 'utf8')
  await writeFile(join(out, 'manifest.json'), JSON.stringify(result.manifest, null, 2), 'utf8')

  console.log(`Wrote ${join(out, 'plugin.js')} (${result.code.length.toLocaleString()} characters)`)
  console.log(`Wrote ${join(out, 'manifest.json')}`)

  return report(result.warnings)
}

async function render(args: string[]): Promise<number> {
  const { positional, options, inputs, variables } = parse(args)
  const entry = resolve(process.cwd(), positional[0] ?? 'plugin.ts')

  const built = await buildPlugin(entry)
  const plugin = await load(entry)
  const result = renderPlugin(plugin, inputs, {
    variables,
    fetch: () => {
      throw new Error('render never makes real network calls — write a test with a fetch stub instead.')
    }
  })

  for (const message of result.logs) {
    console.log(`[log] ${message}`)
  }

  console.log('--- generated shell ---')
  console.log(result.script)

  void options
  return report(built.warnings)
}

async function install(args: string[]): Promise<number> {
  const { positional, options } = parse(args)
  const entry = resolve(process.cwd(), positional[0] ?? 'plugin.ts')

  const url = (options.url ?? process.env.WALIS_API_URL ?? 'http://127.0.0.1:11120').replace(/\/$/, '')
  const key = options.key ?? process.env.WALIS_API_KEY

  if (!key) {
    console.error('Missing API key: pass --key or set WALIS_API_KEY.')
    return 1
  }

  const result = await buildPlugin(entry)

  if (result.warnings.length !== 0) {
    report(result.warnings)
    console.error('Compatibility warnings found — fix them before installing.')
    return 1
  }

  const response = await fetch(`${url}/api/plugins/`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${key}` },
    body: JSON.stringify({
      manifest: result.manifest,
      code: result.code,
      isTrusted: 'trusted' in options
    })
  })

  if (!response.ok) {
    console.error(`Install failed: ${response.status} ${await response.text()}`)
    return 1
  }

  console.log(`Installed ${result.manifest.id} to ${url}`)
  return 0
}

/** Uses esbuild to turn the entry point into an importable temporary module and retrieves the definePlugin result. */
async function load(entry: string): Promise<PluginDefinition> {
  const { build } = await import('esbuild')
  const bundled = await build({
    entryPoints: [entry],
    bundle: true,
    write: false,
    format: 'esm',
    platform: 'neutral',
    target: 'es2022',
    logLevel: 'silent'
  })

  const source = bundled.outputFiles[0].text
  const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`)

  return module.default as PluginDefinition
}

interface ParsedArgs {
  positional: string[]
  options: Record<string, string>
  inputs: Record<string, string>
  variables: Record<string, string>
}

function parse(args: string[]): ParsedArgs {
  const positional: string[] = []
  const options: Record<string, string> = {}
  const inputs: Record<string, string> = {}
  const variables: Record<string, string> = {}

  for (let index = 0; index < args.length; index++) {
    const argument = args[index]

    if (!argument.startsWith('--')) {
      positional.push(argument)
      continue
    }

    const name = argument.slice(2)

    if (name === 'trusted') {
      options.trusted = 'true'
      continue
    }

    const value = args[++index] ?? ''

    if (name === 'input' || name === 'var') {
      const separator = value.indexOf('=')
      const target = name === 'input' ? inputs : variables

      if (separator > 0) {
        target[value.slice(0, separator)] = value.slice(separator + 1)
      }

      continue
    }

    options[name] = value
  }

  return { positional, options, inputs, variables }
}

function report(warnings: string[]): number {
  for (const warning of warnings) {
    console.error(`⚠ ${warning}`)
  }

  return warnings.length === 0 ? 0 : 1
}

process.exitCode = await main()
