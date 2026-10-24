/** Parameter types, mirroring the field kinds in the Walis admin UI. */
export type ParameterType = 'text' | 'multiline' | 'number' | 'boolean' | 'select' | 'path'

export interface ParameterOption {
  value: string
  label: string
}

export interface Parameter {
  /** Read in code as inputs[name]; letters, digits and underscores only. */
  name: string
  label: string
  type?: ParameterType
  required?: boolean
  default?: string
  placeholder?: string
  help?: string
  /** Options for `select` parameters. */
  options?: ParameterOption[]
}

export interface Permissions {
  /** Domains the plugin may reach, e.g. https://example.com. Anything not listed is refused. */
  http?: string[]
  /** Secrets the plugin may use. The value is never handed over — only injected into a header. */
  secrets?: string[]
  kv?: boolean
}

export interface Manifest {
  /** Lowercase identifier with at least one dot, e.g. acme.deploy. */
  id: string
  name: string
  description?: string
  version?: string
  author?: string
  /** Host API version; a plugin is not loaded when it does not match what Walis provides. */
  apiVersion?: number
  kind?: 'action'
  category?: string
  icon?: string
  parameters?: Parameter[]
  permissions?: Permissions
}

export interface HttpResponse {
  status: number
  ok: boolean
  body: string
}

export interface FetchOptions {
  method?: string
  headers?: Record<string, string>
  body?: string
  /**
   * Ask the host to put a named secret into a header.
   * The plugin never sees the value — deliberately, so a leaked plugin cannot ship tokens elsewhere.
   */
  withSecret?: { name: string; header: string }
}

/**
 * Everything a plugin can reach.
 * No require, import, fs, process, fetch or setTimeout, and no npm packages at runtime —
 * the interpreter is Jint (pure C#), so you get the standard library plus the API below.
 */
export interface Context {
  /** Writes to the server log; useful while debugging. */
  log(message: string): void

  shell: {
    /** Wraps a value as a safe shell string. Always use it when building commands. */
    quote(value: string): string
  }

  /** Environment variables of the current build (read-only). */
  variables: Readonly<Record<string, string>>

  http: {
    /** Synchronous. Only domains declared in the manifest can be reached. */
    fetch(url: string, options?: FetchOptions): HttpResponse
  }
}

export type Inputs = Readonly<Record<string, string>>

/** Returns a shell snippet that Walis runs as one step of a build. */
export type RenderFunction = (inputs: Inputs, ctx: Context) => string

export interface PluginDefinition {
  manifest: Manifest
  render: RenderFunction
}
