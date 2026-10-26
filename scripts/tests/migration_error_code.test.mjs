import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const html = await readFile(new URL('../../Configuration/configPage.html', import.meta.url), 'utf8');
const source = html.match(/^            async function migrationErrorCode\(error\) \{[\s\S]*?^            }/m)?.[0];
assert.ok(source, 'The shipped page must contain the error projector under test');
const context = vm.createContext({ Response });
vm.runInContext(source, context);

test('shows the server code from the native Fetch Response from Jellyfin-compatible error handling', async () => {
  assert.equal(await context.migrationErrorCode(new Response(JSON.stringify({code:'migration-source-changed'}), {status:409})), 'migration-source-changed');
});
test('preserves supported jqXHR and wrapped response error forms', async () => {
  assert.equal(await context.migrationErrorCode({responseJSON:{code:'migration-profile-changed'}}), 'migration-profile-changed');
  assert.equal(await context.migrationErrorCode({response:new Response(JSON.stringify({code:'migration-recovery-conflict'}))}), 'migration-recovery-conflict');
});
test('does not project raw provider URLs, arbitrary text, or malformed bodies', async () => {
  assert.equal(await context.migrationErrorCode(new Response('private provider error')), null);
  assert.equal(await context.migrationErrorCode({responseJSON:{code:'https://private.invalid?token=secret'}}), null);
  assert.equal(await context.migrationErrorCode(new Error('token=private')), null);
});
