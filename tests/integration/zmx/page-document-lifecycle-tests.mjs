// Test-only Node runner; exercises the exact JavaScript embedded in the Mac patch.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const patch = readFileSync(new URL('./agterm-page-revision.patch', import.meta.url), 'utf8');
const match = patch.match(/^\+\s+let handler = """\n([\s\S]*?)^\+\s+"""/m);
assert.ok(match, 'revision-aware native handler is present in the reproducible patch');
const source = match[1].split('\n').map(line => line.startsWith('+') ? line.slice(1) : line).join('\n');

function fixture() {
    const events = new Map(), timers = [], commands = [], bindings = [];
    const window = { __agtermDocumentToken: null, webkit: { messageHandlers: { agterm: {
        postMessage(body) {
            if (body.documentReady) { bindings.push(body); return Promise.resolve(true); }
            commands.push(body);
            return Promise.resolve('accepted');
        }
    } } } };
    const handler = new Function('window', 'addEventListener', 'setTimeout', source + '\nreturn handler;')(
        window, (name, callback) => {
            if (!events.has(name)) events.set(name, []);
            events.get(name).push(callback);
        }, callback => timers.push(callback));
    return { window, commands, bindings, handler,
        event(name) { for (const callback of events.get(name) ?? []) callback(); },
        tick() { const pending = timers.splice(0); for (const callback of pending) callback(); }
    };
}

for (const navigation of ['pagehide', 'popstate']) {
    const f = fixture();
    const old = f.handler.postMessage({cmd: 'session.overlay.submit', args: {value: 'old'}});
    const rejected = assert.rejects(old, /page document retired/);
    f.event(navigation);
    if (navigation === 'pagehide') f.event('pageshow');
    f.window.__agtermDocumentToken = 'replacement';
    f.tick();
    await rejected;
    assert.equal(f.commands.length, 0, 'waiting old submit never borrows replacement identity');
    assert.equal(await f.handler.postMessage({cmd: 'session.status'}), 'accepted');
    assert.equal(f.commands[0].documentToken, 'replacement');
    assert.deepEqual(f.bindings, [{documentReady: true}], 'readiness contains no command or token');
}

const restored = fixture();
restored.event('pageshow');
const current = restored.handler.postMessage({cmd: 'session.status'});
assert.equal(restored.commands.length, 0, 'restored document waits for native binding');
restored.window.__agtermDocumentToken = 'restored';
restored.tick();
assert.equal(await current, 'accepted');
assert.equal(restored.commands[0].documentToken, 'restored');
console.log('PASS cached and same-document lifecycle: old waiters retired, new calls bound, readiness carries no authority');
