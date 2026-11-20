import assert from 'node:assert/strict'
import { linkSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { apply } from '../../src/LittleLoop.Chat/Bot/deepseek-private-guard.mjs'

test('the external Harness guard confines all tool calls to eligible files of its private session', () => {
  const original = process.cwd()
  const base = mkdtempSync(join(tmpdir(), 'littleloop-acp-'))
  const workspace = join(base, 'workspace')
  mkdirSync(workspace)
  process.chdir(workspace)
  try {
    const sibling = join(base, 'sibling.txt')
    writeFileSync(sibling, 'synthetic outside file')
    writeFileSync('notes.txt', 'synthetic workspace file')
    linkSync(sibling, 'linked.txt')
    for (const enabled of [false, true]) {
      let guard, ready, exposed
      process.env.LITTLELOOP_ACP_FILES = enabled ? '1' : '0'
      process.env.LITTLELOOP_ACP_WORKSPACE = workspace
      apply({tools: {guard(fn) { guard = fn }}, on(event, fn) { assert.equal(event, 'agent/created'); ready = fn }})
      const agent = {session: {id: 'fixture'}, ctx: {tools: {restrict(value) { exposed = value.allow }}}}
      const call = (name, args, session = agent) => guard({name, arguments: args, agent: session})
      assert.ok(call('read', {file_path: 'notes.txt'}))
      ready({agent})
      assert.deepEqual(exposed, enabled ? ['read', 'write', 'edit'] : [])
      assert.equal(!call('read', {file_path: 'notes.txt'}), enabled)
      assert.equal(!call('write', {file_path: 'new.csv', content: 'a,b'}), enabled)
      assert.equal(!call('edit', {file_path: 'notes.txt', new_string: 'ok'}), enabled)
      for (const filename of [sibling, '../sibling.txt', 'linked.txt', 'AGENTS.md', 'NUL.txt', 'notes.txt:ads', 'sub/doc.md'])
        assert.ok(call('read', {file_path: filename}))
      for (const name of ['bash', 'pwsh', 'grep', 'glob', 'fetch', 'run_code', 'apply_patch'])
        assert.ok(call(name, {}))
      assert.ok(call('read', {file_path: 'notes.txt', sandbox_permissions: 'require_escalated'}))
      assert.ok(call('read', {file_path: 'notes.txt'}, {session: {id: 'another-session'}}))
    }
  } finally {
    process.chdir(original)
    delete process.env.LITTLELOOP_ACP_FILES
    delete process.env.LITTLELOOP_ACP_WORKSPACE
    rmSync(base, {recursive: true, force: true})
  }
})
