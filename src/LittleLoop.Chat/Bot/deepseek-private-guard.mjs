import { lstatSync, realpathSync } from 'node:fs'
import { basename, dirname, isAbsolute, join, resolve } from 'node:path'

export const name = 'littleloop-private-workspace'
export const inject = ['tools']

// The provider runs its own native file tools. The guard makes the decision at
// dispatch, including for tool calls not surfaced in the model's tool list.
export function apply(ctx) {
  const workspace = realpathSync.native(process.cwd())
  if (workspace !== realpathSync.native(process.env.LITTLELOOP_ACP_WORKSPACE))
    throw new Error('The private ACP process has an unexpected workspace.')
  const nonce = process.env.LITTLELOOP_ACP_CHALLENGE
  const allowed = process.env.LITTLELOOP_ACP_FILES === '1' ? ['read', 'write', 'edit'] : []
  const sessions = new Set()
  ctx.tools.guard(call => {
    if (!sessions.has(call.agent?.session?.id) || !allowed.includes(call.name)) return 'Private workspace tools only.'
    const args = call.arguments
    if (!args || typeof args.file_path !== 'string' || 'sandbox_permissions' in args || 'justification' in args)
      return 'Invalid private workspace operation.'
    const leaf = basename(args.file_path)
    // A single ordinary document name avoids traversal, alternate streams, device
    // names, linkable directory components and hidden loader configuration files.
    if (!/^[\p{L}\p{N}_ -]{1,96}\.(?:txt|md|csv)$/iu.test(leaf) ||
        /^(?:con|prn|aux|nul|(?:com|lpt)[0-9¹²³]) *\./iu.test(leaf) ||
        /^agents\.md$/iu.test(leaf)) return 'Use a plain text, Markdown or CSV file in this chat workspace.'
    const full = join(workspace, leaf)
    if (args.file_path !== leaf && (!isAbsolute(args.file_path) || args.file_path !== full))
      return 'The path is outside this chat workspace.'
    try {
      if (resolve(dirname(full)) !== workspace || realpathSync.native(workspace) !== workspace)
        return 'Invalid workspace path.'
      const item = lstatSync(full)
      if (!item.isFile() || item.isSymbolicLink() || item.nlink !== 1 || item.size > 131072 ||
          realpathSync.native(full) !== full) return 'Only small regular workspace files are available.'
    } catch (error) {
      if (error.code !== 'ENOENT' || call.name !== 'write') return 'The workspace file is unavailable.'
    }
    const replacement = call.name === 'write' ? args.content : args.new_string
    if (typeof replacement === 'string' && Buffer.byteLength(replacement, 'utf8') > 131072)
      return 'The workspace text exceeds 128 KiB.'
  })
  ctx.on('agent/created', ({agent}) => {
    agent.ctx.tools.restrict({ allow: allowed })
    sessions.add(agent.session.id)
    process.stdout.write(JSON.stringify({jsonrpc: '2.0', method: 'littleloop/private-ready',
      params: {nonce, sessionId: agent.session.id, workspace, tools: allowed}}) + '\n')
  })
}
