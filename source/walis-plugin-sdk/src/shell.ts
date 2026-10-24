/**
 * Wraps a value as a safe shell string.
 *
 * This must match the host (`PluginHost.ShellQuote`) and the `{{name}}` rule of action templates
 * exactly, otherwise what you test locally is not what runs. The rule: wrap in single quotes and
 * replace every single quote inside with `'\\''` (close, escape one, reopen).
 */
export function quote(value: string): string {
  return `'${String(value ?? '').replaceAll("'", "'\\''")}'`
}
