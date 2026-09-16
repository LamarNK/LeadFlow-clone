const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const web = path.join(__dirname, '../../Orbita.Web/wwwroot/js');
const script = fs.readFileSync(path.join(web, 'crm-recordings.js'), 'utf8');

function player() {
    const error = { hidden: true };
    return { error, pauses: 0, matches: () => true, pause() { this.pauses++; },
        parentElement: { querySelector: () => error } };
}
test('delegated media events cover newly swapped pages and register only once', () => {
    const handlers = {};
    let players = [];
    const context = vm.createContext({ window: {}, document: {
        addEventListener(name, fn, capture) { assert.equal(capture, true); assert.equal(handlers[name], undefined); handlers[name] = fn; },
        querySelectorAll() { return players; }
    } });
    vm.runInContext(script, context);
    vm.runInContext(script, context);
    players = [player(), player()];
    handlers.play({ target: players[0] });
    assert.equal(players[0].pauses, 0); assert.equal(players[1].pauses, 1);
    handlers.error({ target: players[0] }); assert.equal(players[0].error.hidden, false);
    handlers.loadedmetadata({ target: players[0] }); assert.equal(players[0].error.hidden, true);
    handlers.play({ target: { matches: () => false } }); assert.equal(players[0].pauses, 0);
});

const nav = fs.readFileSync(path.join(web, 'orbita/navigation.js'), 'utf8');
const functions = nav.slice(nav.indexOf('runtime.getNavKey ='), nav.indexOf('var currentLoadingOverlay'));
for (const [location, expected] of [
    ['/Crm/Recordings?from=2026-09-15&page=2', '/Crm/Recordings'],
    ['/Crm/MissedCalls', '/Crm/MissedCalls'],
    ['/Crm/Card/fixture', '/Crm'],
    ['/Crm/Analytics', '/Crm/Analytics'],
    ['/Dashboard', '/']
]) {
    test('only the correct menu item is active for ' + location, () => {
        const links = ['/', '/Crm', '/Crm/MissedCalls', '/Crm/Recordings', '/Crm/Analytics'].map(href => {
            const classes = new Set(['active']);
            return { href, classes, getAttribute: () => href, classList: {
                add: v => classes.add(v), remove: v => classes.delete(v)
            } };
        });
        const context = vm.createContext({ runtime: {}, document: { querySelectorAll: () => links } });
        vm.runInContext(functions, context);
        context.runtime.updateActiveNav(location);
        assert.deepEqual(links.filter(l => l.classes.has('active')).map(l => l.href), [expected]);
    });
}
