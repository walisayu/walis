import { build as esbuild } from 'esbuild'
import { createContext, runInNewContext } from 'node:vm'
import type { Manifest, PluginDefinition } from './types.js'

export interface BuildResult {
  manifest: Manifest
  /** The code for Walis: a single file that finally attaches render as a global. */
  code: string
  warnings: string[]
}

/**
 * The host has no module system and none of these globals, so using them blows up only at run time.
 * Catching them at bundle time beats failing in the middle of a real build.
 */
const FORBIDDEN: ReadonlyArray<{ pattern: RegExp; message: string }> = [
  { pattern: /\brequire\s*\(/, message: 'require(): the host has no module system — bundle your dependencies instead' },
  { pattern: /\bprocess\s*\./, message: 'process: the host is not Node' },
  { pattern: /\b(?:node:)?fs\b/, message: 'fs: plugins cannot touch the filesystem — emit shell that does it instead' },
  { pattern: /\bsetTimeout\s*\(/, message: 'setTimeout: the host has no event loop' },
  { pattern: /\bsetInterval\s*\(/, message: 'setInterval: the host has no event loop' },
  { pattern: /(?<!ctx\.http\.)\bfetch\s*\(/, message: 'fetch(): use ctx.http.fetch instead' },
  { pattern: /\bawait\b/, message: 'await: the host API is synchronous — render must return a string directly' },
  { pattern: /\bnew\s+Promise\b/, message: 'Promise: the host API is synchronous' },
  { pattern: /\bXMLHttpRequest\b/, message: 'XMLHttpRequest: use ctx.http.fetch instead' }
]

/** Bundles a single entry point, extracts the manifest and produces the code the host wants. */
export async function buildPlugin(entry: string): Promise<BuildResult> {
  const bundled = await esbuild({
    entryPoints: [entry],
    bundle: true,
    write: false,
    format: 'iife',
    globalName: '__walisPlugin',
    platform: 'neutral',

    // Jint supports up to ES2023; targeting ES2020 keeps the syntax a bit more conservative.
    target: 'es2020',
    logLevel: 'silent'
  })

  const iife = bundled.outputFiles[0].text
  const plugin = evaluate(iife)

  if (typeof plugin.render !== 'function') {
    throw new Error('No render function exported — use definePlugin and export it as default.')
  }

  if (!plugin.manifest?.id) {
    throw new Error('The manifest has no id.')
  }

  const code = `${iife}\nglobalThis.render = __walisPlugin.default.render;\n`

  return {
    manifest: normalize(plugin.manifest),
    code,
    warnings: inspect(code)
  }
}

/** Runs the bundle in an isolated context and pulls out the manifest and render. */
function evaluate(iife: string): PluginDefinition {
  const sandbox: Record<string, unknown> = {}
  const context = createContext(sandbox)

  runInNewContext(iife, context, { timeout: 5_000 })

  const exported = (sandbox.__walisPlugin as { default?: PluginDefinition } | undefined)?.default

  if (!exported) {
    throw new Error('The bundle has no default export.')
  }

  return exported
}

/** Fills in fields the host needs that authors often forget. */
function normalize(manifest: Manifest): Manifest {
  return {
    version: '1.0.0',
    apiVersion: 1,
    kind: 'action',
    category: 'general',
    icon: 'cil-code',
    parameters: [],
    ...manifest,
    permissions: {
      http: [],
      secrets: [],
      kv: false,
      ...(manifest.permissions ?? {})
    }
  }
}

export function inspect(code: string): string[] {
  const warnings: string[] = []

  for (const rule of FORBIDDEN) {
    if (rule.pattern.test(code)) {
      warnings.push(rule.message)
    }
  }

  if (code.length > 20_000) {
    warnings.push(`Code is ${code.length.toLocaleString()} characters, over the 20,000 limit Walis allows`)
  }

  return warnings
}
