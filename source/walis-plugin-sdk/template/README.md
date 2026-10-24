# Walis plugin

```bash
npm install
npx walis-plugin render --input target=stg-01   # see the generated shell
npm test                                        # run the tests
npx walis-plugin build                          # bundle into dist/
WALIS_API_KEY=... npx walis-plugin install      # upload to Walis
```

`plugin.ts` exports a manifest and a `render` function. `render` returns a shell snippet that Walis
runs as one step of a build.

Always wrap parameter values with `ctx.shell.quote(...)` — the escaping rule matches the Walis host,
so what you test locally is what actually runs.
