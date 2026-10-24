# @walisayu/plugin-sdk

Build [Walis](https://walis.io) plugins with types, local testing, bundling and one-command install.

A plugin does exactly one thing: **turn the parameters a user filled in into a shell snippet**.
Walis runs that snippet as one step of a build.

## Quick start

```bash
npx @walisayu/plugin-sdk init my-plugin
cd my-plugin && npm install

npx walis-plugin render --input target=stg-01   # see what it produces
npm test                                        # run the tests
npx walis-plugin build                          # bundle into dist/
WALIS_API_KEY=... npx walis-plugin install      # upload to Walis
```

## Writing a plugin

```ts
import { definePlugin } from '@walisayu/plugin-sdk'

export default definePlugin({
  manifest: {
    id: 'acme.deploy',
    name: 'Deploy to staging',
    parameters: [{ name: 'target', label: 'Host', required: true }]
  },

  render(inputs, ctx) {
    return `scripts/deploy.sh ${ctx.shell.quote(inputs.target)}`
  }
})
```

Always wrap values with `ctx.shell.quote(...)`. It is byte-for-byte the same rule Walis uses on the
server (both sides have tests pinning it), so what you see locally is what actually runs.

## Testing

```ts
import { renderPlugin } from '@walisayu/plugin-sdk'
import plugin from './plugin.js'

const { script, logs, fetches } = renderPlugin(plugin, { target: 'stg-01' })
```

`renderPlugin` reproduces the host's behaviour: parameter defaults, the empty-string semantics of
`boolean`, and "a domain not declared in the manifest cannot be reached". `fetches` lets you assert
that a plugin does not call APIs it has no business calling.

Provide a stub to test network calls:

```ts
renderPlugin(plugin, inputs, {
  variables: { BRANCH: 'main' },
  fetch: (url) => ({ status: 200, ok: true, body: '{"key":"ABC-1"}' })
})
```

## What the runtime does not have

Plugins run in Jint, a pure C# JavaScript interpreter. **There is no npm at runtime and no module
system**:

- no `require`, `import`, `fs`, `process`, `fetch`, `setTimeout`
- the host API is synchronous — `render` must return a string directly (no `async` / `await` / `Promise`)
- limits: 10 s timeout, 8 MB memory, 500,000 statements, recursion depth 64

`walis-plugin build` scans the bundle and warns about all of the above, so you find out at build time
rather than halfway through a production build. Dependencies are bundled in by esbuild, so small pure
JavaScript packages are fine — as long as they stay clear of the list above.

## Capabilities

A capability that is not declared in the manifest simply does not exist:

```ts
manifest: {
  permissions: {
    http: ['https://acme.atlassian.net'],
    secrets: ['JIRA_TOKEN']
  }
}
```

Secrets are **usable but not readable** — a plugin can ask the host to put a value into a header, but
never receives the value itself:

```ts
ctx.http.fetch('https://acme.atlassian.net/rest/api/3/issue', {
  method: 'POST',
  body: JSON.stringify({ fields: { summary: inputs.title } }),
  withSecret: { name: 'JIRA_TOKEN', header: 'Authorization' }
})
```

## CLI

| Command | What it does |
| --- | --- |
| `walis-plugin init <dir>` | Scaffold a starter project |
| `walis-plugin build [entry]` | Bundle into `dist/plugin.js` and `dist/manifest.json` |
| `walis-plugin render [entry] --input k=v` | Run locally and print the generated shell |
| `walis-plugin install [entry] --url --key` | Bundle and upload (refuses if there are warnings) |

`--url` falls back to `WALIS_API_URL`, `--key` to `WALIS_API_KEY`.
Installing requires an **admin** key — a plugin is code that will be executed.

## License

MIT
