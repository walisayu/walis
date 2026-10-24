import { quote } from './shell.js'
import type { Context, FetchOptions, HttpResponse, Inputs, PluginDefinition } from './types.js'

export interface FetchCall {
  url: string
  options?: FetchOptions
}

export interface HarnessOptions {
  /** Environment variables visible to this run. */
  variables?: Record<string, string>

  /**
   * Stubs http.fetch. Without it any call throws — matching the real host,
   * where a domain not declared in the manifest simply cannot be reached.
   */
  fetch?: (url: string, options?: FetchOptions) => HttpResponse
}

export interface RenderResult {
  /** The generated shell. */
  script: string
  /** Messages the plugin passed to ctx.log. */
  logs: string[]
  /** Calls the plugin made, so tests can assert it does not reach APIs it should not. */
  fetches: FetchCall[]
}

/**
 * Runs a plugin locally and returns the script it produced.
 *
 * This is not a sandbox — it reproduces the shape of the host API so you can write tests.
 * The real limits and isolation live on the Walis side.
 */
export function renderPlugin(
  plugin: PluginDefinition,
  inputs: Inputs = {},
  options: HarnessOptions = {}
): RenderResult {
  const logs: string[] = []
  const fetches: FetchCall[] = []
  const allowed = plugin.manifest.permissions?.http ?? []

  const ctx: Context = {
    log: (message) => {
      logs.push(message)
    },
    shell: { quote },
    variables: Object.freeze({ ...(options.variables ?? {}) }),
    http: {
      fetch: (url, fetchOptions) => {
        if (!isAllowed(url, allowed)) {
          throw new Error(
            `This plugin did not declare access to ${url} — add the domain to permissions.http in the manifest.`
          )
        }

        fetches.push({ url, options: fetchOptions })

        if (!options.fetch) {
          throw new Error(`The plugin called ${url} but the test provided no fetch stub.`)
        }

        return options.fetch(url, fetchOptions)
      }
    }
  }

  const script = plugin.render(applyDefaults(plugin, inputs), ctx)

  return { script, logs, fetches }
}

/** Applies defaults to unset parameters, matching the host's ActionScriptCompiler.Resolve. */
function applyDefaults(plugin: PluginDefinition, inputs: Inputs): Inputs {
  const resolved: Record<string, string> = {}

  for (const parameter of plugin.manifest.parameters ?? []) {
    const provided = inputs[parameter.name]
    const value = provided === undefined || provided === '' ? parameter.default ?? '' : provided

    resolved[parameter.name] =
      parameter.type === 'boolean' ? (isTruthy(value) ? 'true' : '') : value
  }

  // Undeclared parameters are let through too, to make testing edge cases easy.
  return Object.freeze({ ...resolved, ...inputs })
}

function isTruthy(value: string | undefined): boolean {
  return value !== undefined && value !== '' && value !== 'false' && value !== '0'
}

/** Same as the host's PluginHost.IsAllowed: compares only the scheme and host and ignores the path. */
export function isAllowed(url: string, allowed: readonly string[]): boolean {
  let target: URL

  try {
    target = new URL(url)
  } catch {
    return false
  }

  return allowed.some((entry) => {
    try {
      const permitted = new URL(entry)
      return permitted.host === target.host && permitted.protocol === target.protocol
    } catch {
      return false
    }
  })
}
