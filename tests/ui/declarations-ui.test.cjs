const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const assert = require('node:assert/strict');

// A small DOM fixture exercises the actual renderer without Chrome, HTTP, or dependencies.
// HTML sinks fail immediately; source values must stay textContent throughout rendering.
class FixtureNode {
    constructor(tag) {
        this.tagName = tag;
        this.className = '';
        this.children = [];
        this.dataset = {};
        this.attributes = {};
        this._text = '';
    }
    set textContent(value) { this._text = String(value ?? ''); this.children = []; }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(' '); }
    set innerHTML(_) { throw new Error('innerHTML is forbidden in this fixture'); }
    set outerHTML(_) { throw new Error('outerHTML is forbidden in this fixture'); }
    append(...nodes) { this.children.push(...nodes); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener() {}
}

const scriptPath = path.join(__dirname, '../../MediaParser/wwwroot/js/search.js');
const source = fs.readFileSync(scriptPath, 'utf8')
    .replace('    initHistory();', '    globalThis.fixture = { declarationProvider, declarationDocumentUrl, createResultCard, formatDateOnly };')
    .replace('    initSearch();', '');
const context = { URL, Intl, document: { createElement: tag => new FixtureNode(tag) } };
vm.runInNewContext(source, context, { filename: scriptPath });
const ui = context.fixture;
const nodes = node => [node, ...node.children.flatMap(nodes)];
const byClass = (node, name) => nodes(node).filter(child => child.className.split(' ').includes(name));
const id = '00000000-0000-4000-8000-000000000001';
const providers = [
    { key: 'nazk', search: 'https://public.nazk.gov.ua/documents/list?q=TEST', document: `https://public.nazk.gov.ua/documents/${id}`, title: 'Публічні декларації НАЗК' },
    { key: 'youcontrol', search: 'https://youcontrol.com.ua/catalog/individuals/search/?q=TEST', document: `https://youcontrol.com.ua/catalog/individuals/declaration/${id}/`, title: 'Декларації YouControl' }
];
const summary = provider => ({
    id, fullName: '<img onerror="bad()">ТЕСТ', position: 'ПЕРША ПОСАДА', workplace: 'ПЕРША УСТАНОВА',
    declarationYear: null, declarationType: null, documentType: null,
    submittedAt: null, submittedDate: '2026-10-26', url: provider.document,
    phone: 'PRIVATE_PHONE', address: 'PRIVATE_ADDRESS', income: 'PRIVATE_INCOME', family: 'PRIVATE_FAMILY'
});
const result = (provider, overrides = {}) => ({
    siteId: 20, site: 'Довільна назва джерела', identifier: 'ТЕСТ', status: 'found', label: 'Знайдено декларації',
    profileUrl: provider.search, reason: 'Тестова причина', evidence: null, checkedAt: null,
    declarations: [summary(provider), { ...summary(provider), fullName: 'ІНШИЙ ЗАПИС', position: 'ДРУГА ПОСАДА' }],
    declarationTotal: 12, ...overrides
});

test('provider identity comes only from canonical HTTPS host and search route', () => {
    for (const provider of providers) {
        assert.equal(ui.declarationProvider(provider.search)?.key, provider.key);
        const card = ui.createResultCard(result(provider), null);
        assert.equal(byClass(card, 'declaration-results')[0].attributes['aria-label'], provider.title);
    }
    for (const url of [
        'http://youcontrol.com.ua/catalog/individuals/search/?q=x',
        'https://youcontrol.com.ua.evil.invalid/catalog/individuals/search/?q=x',
        'https://youcontrol.com.ua/other/?q=x',
        'https://youcontrol.com.ua:444/catalog/individuals/search/?q=x',
        'https://user@youcontrol.com.ua/catalog/individuals/search/?q=x',
        'https://youcontrol.com.ua/catalog/individuals/search/?q=x#x',
        'https://public.nazk.gov.ua/other'
    ]) assert.equal(ui.declarationProvider(url), null, url);
    const spoof = ui.createResultCard(result(providers[0], { site: 'YouControl — декларації', profileUrl: 'https://example.invalid/search' }), null);
    assert.equal(byClass(spoof, 'declaration-results').length, 0);
});

test('document links stay within their own provider and matching GUID route', () => {
    for (const provider of providers) {
        const config = ui.declarationProvider(provider.search);
        assert.equal(ui.declarationDocumentUrl(provider.document, id, config), provider.document);
        const other = providers.find(item => item.key !== provider.key);
        assert.equal(ui.declarationDocumentUrl(other.document, id, config), null);
        for (const url of [
            provider.document + '?redirect=x', provider.document + '#x',
            provider.document.replace('https:', 'http:'),
            provider.document.replace('https://', 'https://user@'),
            provider.document.replace(id, '00000000-0000-4000-8000-000000000002')
        ]) assert.equal(ui.declarationDocumentUrl(url, id, config), null, url);
    }
});

test('direct YouControl person cards use only strict HTTPS slug routes', () => {
    const person = 'https://youcontrol.com.ua/catalog/individuals/test-person/';
    assert.equal(ui.declarationProvider(person)?.key, 'youcontrol');
    const card = ui.createResultCard(result(providers[1], { profileUrl: person }), null);
    assert.equal(byClass(card, 'declaration-results')[0].attributes['aria-label'], 'Декларації YouControl');
    assert.equal(byClass(card, 'declaration-all')[0].href, person);
    assert.ok(byClass(card, 'declaration-all')[0].textContent.includes('Відкрити картку на YouControl'));
    assert.equal(byClass(card, 'person-overview').length, 1);
    for (const url of [
        providers[1].document,
        'https://youcontrol.com.ua/catalog/individuals/declaration/',
        'https://youcontrol.com.ua/catalog/individuals/search',
        'https://youcontrol.com.ua/catalog/individuals/oneword/',
        'https://youcontrol.com.ua/catalog/individuals/test_person/',
        'https://youcontrol.com.ua/catalog/individuals/Test-Person/',
        person + '?q=x', person + '#x',
        person.replace('https:', 'http:'),
        person.replace('https://', 'https://user@'),
        person.replace('youcontrol.com.ua/', 'youcontrol.com.ua:444/'),
        person.replace('youcontrol.com.ua/', 'youcontrol.com.ua.evil.invalid/')
    ]) assert.equal(ui.declarationProvider(url), null, url);
});

test('rich cards render safe public metadata and overview from only the first summary', () => {
    for (const provider of providers) {
        const card = ui.createResultCard(result(provider), null);
        const overview = byClass(card, 'person-overview')[0];
        assert.ok(overview.textContent.includes('ПЕРША ПОСАДА'));
        assert.ok(!overview.textContent.includes('ДРУГА ПОСАДА'));
        assert.ok(!/PRIVATE_(PHONE|ADDRESS|INCOME|FAMILY)/.test(card.textContent));
        assert.equal(nodes(card).filter(node => ['img', 'script'].includes(node.tagName)).length, 0);
        assert.ok(card.textContent.includes('<img onerror="bad()">'));
        assert.ok(card.textContent.includes('Збіг ПІБ не підтверджує особу'));
        assert.ok(card.textContent.includes('26.10.2026'));
        assert.ok(!card.textContent.includes('Invalid Date'));
        assert.ok(!byClass(card, 'declaration-meta').some(meta => meta.textContent.includes('Рік декларації')));
        assert.ok(byClass(card, 'declaration-count')[0].textContent.includes('2 із 12'));
    }
});

test('null inventory is unavailable, empty inventory stays unconfirmed, and display cap keeps actual total', () => {
    for (const provider of providers) {
        const unavailable = ui.createResultCard(result(provider, { declarations: null, declarationTotal: null, status: 'unconfirmed' }), null);
        assert.equal(byClass(unavailable, 'declaration-count').length, 0);
        assert.equal(byClass(unavailable, 'declaration-empty').length, 0);
        assert.equal(byClass(unavailable, 'declaration-unavailable').length, 1);
        const empty = ui.createResultCard(result(provider, { declarations: [], declarationTotal: 0, status: 'unconfirmed' }), null);
        assert.ok(byClass(empty, 'declaration-count')[0].textContent.includes('0 із 0'));
        assert.ok(empty.className.includes('unconfirmed'));
        const capped = ui.createResultCard(result(provider, { declarations: Array.from({ length: 25 }, () => summary(provider)), declarationTotal: 30 }), null);
        assert.equal(byClass(capped, 'declaration-item').length, 20);
        assert.ok(byClass(capped, 'declaration-count')[0].textContent.includes('20 із 30'));
    }
});

test('date-only values use calendar validation without time or timezone inference', () => {
    for (const [value, expected] of [
        ['2024-02-29', '29.02.2024'], ['2025-02-29', null], ['2026-10-26', '26.10.2026'],
        ['2026-13-01', null], [null, null], ['not-a-date', null], ['2026-10-26T00:00:00Z', null]
    ]) assert.equal(ui.formatDateOnly(value), expected);
});

test('eligible blocked YouControl card keeps both manual Chrome action and source link', () => {
    const card = ui.createResultCard(result(providers[1], { declarations: null, declarationTotal: null, status: 'blocked' }), () => {});
    assert.equal(byClass(card, 'manual-check-button').length, 1);
    assert.equal(byClass(card, 'declaration-all').length, 1);
    assert.equal(byClass(ui.createResultCard(result(providers[1], { status: 'blocked' }), null), 'manual-check-button').length, 0);
});
