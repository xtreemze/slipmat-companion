import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const html = await readFile(new URL('../../Configuration/configPage.html', import.meta.url), 'utf8');
const source = html.match(/^            async function recoverConflictMigration\(job\) \{[\s\S]*?^            }/m)?.[0];
assert.ok(source);
const job = { id: 'job/id', libraryName: 'Music', remoteName: 'tele2', remotePath: 'Slipmat', manifestDigestSha256: 'a'.repeat(64) };

function fixture(accept, fail = false) {
    const requests = [];
    const status = { textContent: '' };
    const context = vm.createContext({
        window: { confirm: () => accept }, document: { querySelector: () => status },
        ApiClient: { getUrl: path => path, ajax: async options => { requests.push(options); if (fail) throw new Error('private-provider-token'); } },
        refreshMigrations: async () => {}, migrationErrorCode: async () => null
    });
    vm.runInContext(source, context);
    return { context, requests, status };
}

test('cancelled sole-writer and disposition confirmation sends no recovery request', async () => {
    const f = fixture(false);
    await f.context.recoverConflictMigration(job);
    assert.equal(f.requests.length, 0);
});

test('confirmed repair binds only the selected job and old baseline, never caller proofs or paths', async () => {
    const f = fixture(true);
    await f.context.recoverConflictMigration(job);
    assert.equal(f.requests.length, 1);
    assert.equal(f.requests[0].url, 'Plugins/AudioGateway/cloud/migrations/job%2Fid/recover-conflicts');
    assert.equal(f.requests[0].type, 'POST');
    assert.deepEqual(JSON.parse(f.requests[0].data), { expectedManifestDigestSha256: job.manifestDigestSha256, writerPolicy: 'companion-only' });
});

test('request failure does not expose raw provider error text', async () => {
    const f = fixture(true, true);
    await f.context.recoverConflictMigration(job);
    assert.match(f.status.textContent, /not accepted/);
    assert.doesNotMatch(f.status.textContent, /private-provider-token/);
});

test('listing progress does not invent a total or percentage', () => {
    const formatter = html.match(/^            function formatConflictPreflightProgress\(sample\) \{[\s\S]*?^            }/m)?.[0];
    assert.ok(formatter);
    const context = vm.createContext({ formatCloudSize: value => String(value) + ' B' });
    vm.runInContext(formatter, context);
    const text = context.formatConflictPreflightProgress({ phase: 'listing-cloud', files: 4181,
        totalFiles: 0, totalIsKnown: false, bytesRead: 0, observedAt: 'now' });
    assert.match(text, /4181 files listed; total pending/);
    assert.doesNotMatch(text, /4181 \/ 0|%/);
    assert.match(context.formatConflictPreflightProgress({ phase: 'hashing-local', files: 1,
        totalFiles: 2, bytesRead: 20, observedAt: 'now' }), /1 \/ 2 files/);
});
