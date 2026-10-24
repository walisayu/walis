export type {
  Context,
  FetchOptions,
  HttpResponse,
  Inputs,
  Manifest,
  Parameter,
  ParameterOption,
  ParameterType,
  Permissions,
  PluginDefinition,
  RenderFunction
} from './types.js'

export { quote } from './shell.js'
export { renderPlugin, isAllowed } from './harness.js'
export type { HarnessOptions, RenderResult, FetchCall } from './harness.js'

import type { PluginDefinition } from './types.js'

/**
 * Declares a plugin. Purely a type helper — it does nothing at runtime.
 *
 * ```ts
 * export default definePlugin({
 *   manifest: { id: 'acme.deploy', name: 'Deploy', parameters: [{ name: 'target', label: 'Target' }] },
 *   render: (inputs, ctx) => `deploy ${ctx.shell.quote(inputs.target)}`
 * })
 * ```
 */
export function definePlugin(plugin: PluginDefinition): PluginDefinition {
  return plugin
}
