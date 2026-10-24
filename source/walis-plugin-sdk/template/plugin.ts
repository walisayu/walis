import { definePlugin } from '@walisayu/plugin-sdk'

export default definePlugin({
  manifest: {
    id: 'acme.deploy',
    name: 'Deploy to staging',
    description: 'Runs the team deploy script.',
    version: '1.0.0',
    category: 'deploy',
    icon: 'cil-cloud-upload',
    parameters: [
      { name: 'target', label: 'Host', required: true, placeholder: 'stg-01' },
      { name: 'restart', label: 'Restart after deploy', type: 'boolean', default: 'false' }
    ]
  },

  render(inputs, ctx) {
    // Always wrap values with ctx.shell.quote — never build the command by hand.
    const lines = [`scripts/deploy.sh ${ctx.shell.quote(inputs.target)}`]

    if (inputs.restart) {
      lines.push(`scripts/restart.sh ${ctx.shell.quote(inputs.target)}`)
    }

    return lines.join('\n')
  }
})
