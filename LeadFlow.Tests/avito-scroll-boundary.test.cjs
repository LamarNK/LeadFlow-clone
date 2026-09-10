// Run with: node --test LeadFlow.Tests/avito-scroll-boundary.test.cjs
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { join } = require('node:path');

// Execute the production DOM parsing/boundary code, including resolveMessengerUrl.
const source = readFileSync(join(__dirname, '../LeadFlow.Core/Services/Avito/AvitoCandidatesPageScripts.cs'), 'utf8');
const start = source.indexOf('public static string BuildScrollStepScript');
const body = source.slice(source.indexOf('            const normalizeUrl =', start),
    source.indexOf('            return JSON.stringify({', start));
const probe = new Function('items', 'previousItemCount', 'window', `
    const normalizeCardText = value => String(value ?? '').trim();
    const parseVacancyAndCityFromRoot = () => ({vacancy: '', city: ''});
    const buildCardFingerprint = name => name;
    const readItemPhone = () => '';
    const normalizePhoneKeyForSourceId = value => value;
    ${body}
    return { fullRescan, newItems, boundary: window.__leadflowScrollBoundary };
`);
const card = name => ({
    querySelector: selector => selector === 'h3, h4' ? { textContent: name } : null,
    querySelectorAll: () => []
});

test('initial empty list does not throw', () => {
    const result = probe([], 0, {});
    assert.deepEqual(result.newItems, []);
    assert.equal(result.boundary.count, 0);
});

test('list shrinking to zero requires full rescan', () => {
    const window = {};
    probe([card('A'), card('B')], 0, window);
    const result = probe([], 2, window);
    assert.equal(result.fullRescan, true);
    assert.deepEqual(result.newItems, []);
});

test('list shrinking with unchanged first card requires full rescan', () => {
    const window = {};
    probe([card('A'), card('B')], 0, window);
    const result = probe([card('A')], 2, window);
    assert.equal(result.fullRescan, true);
    assert.deepEqual(result.newItems.map(item => item.index), [0]);
});

test('normal append returns only new cards', () => {
    const window = {};
    probe([card('A')], 0, window);
    const result = probe([card('A'), card('B')], 1, window);
    assert.equal(result.fullRescan, false);
    assert.deepEqual(result.newItems.map(item => item.index), [1]);
});

test('replaced boundary requires full rescan', () => {
    const window = {};
    probe([card('A')], 0, window);
    const result = probe([card('B')], 1, window);
    assert.equal(result.fullRescan, true);
    assert.deepEqual(result.newItems.map(item => item.index), [0]);
});
