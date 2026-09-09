// Reusable local-only regression for the three-block analytics UI.
// Requires the synthetic fixture in outputs/crm-analytics-local-demo, Playwright and Chrome.
// Never uses a personal browser profile or permits requests to a non-loopback host.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const api = process.env.DEMO_URL || 'http://127.0.0.1:5280';
const web = process.env.DEMO_WEB_URL || 'http://127.0.0.1:5281';
for (const url of [api, web]) {
    if (!['127.0.0.1', 'localhost'].includes(new URL(url).hostname)) throw new Error('Local fixture only');
}

async function run(suite = 'all') {
    const browser = await chromium.launch({
        executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe',
        headless: true
    });
    try {
        const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, timezoneId: 'Asia/Yekaterinburg' });
        await context.route('**/*', route =>
            ['127.0.0.1', 'localhost'].includes(new URL(route.request().url()).hostname) ? route.continue() : route.abort());
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(api + '/demo/open?office=4&from=2026-09-05');
        await page.goto(web + '/Account/Login');
        if (await page.getByRole('textbox', { name: 'Email', exact: true }).isVisible()) {
            await page.getByRole('textbox', { name: 'Email', exact: true }).fill('demo-admin@orbita.local');
            await page.getByRole('textbox', { name: 'Пароль', exact: true }).fill('LocalDemo2026!');
            await page.getByRole('button', { name: 'Войти', exact: true }).click();
            await page.waitForURL(url => url.pathname !== '/Account/Login');
        }
        const open = async (manager = '', from = '2026-09-05', to = from) => {
            await page.goto(web + '/Crm/Analytics?' + new URLSearchParams({ from, to, tz: '-300', managerUserId: manager }));
            await page.locator('[data-report-block="work"]').waitFor();
            assert.equal(await page.locator('[data-report-block]').count(), 3);
        };
        const close = async () => {
            for (const button of await page.getByRole('button', { name: 'Свернуть', exact: true }).all()) await button.click();
        };
        const count = async key => Number(await page.locator('[data-metric="' + key + '"]').textContent());
        const settle = async () => page.evaluate(async () => {
            await document.fonts.ready;
            await Promise.all(document.getAnimations().filter(a => a.playState === 'running'
                && Number.isFinite(a.effect?.getComputedTiming().endTime)).map(a => a.finished.catch(() => {})));
        });
        await open();

        if (suite === 'all' || suite === 'evidence') {
            for (const metric of ['sales.received', 'sales.contacts', 'sales.questionnaires', 'sales.tickets', 'sales.successes', 'sales.refusals']) {
                const expected = await count(metric);
                const url = page.url();
                await page.locator('.sales-kpi').filter({ has: page.locator('[data-metric="' + metric + '"]') }).click();
                const host = page.locator('[data-report-block="work"] [data-evidence-host]');
                await host.locator('.sales-evidence-summary').waitFor();
                assert.equal(Number(await host.locator('.sales-evidence-summary strong').textContent()), expected);
                assert.equal(await host.locator('tbody tr').count(), expected);
                assert.equal(page.url(), url, 'Drilldown stays on the same page');
                await close();
            }
            await page.getByRole('button', { name: 'Лид(Важный) 0 переходов в этап', exact: true }).click();
            assert.equal(await page.locator('[data-report-block="stages"] .sales-evidence-summary strong').textContent(), '0');
            await close();
            await page.getByRole('button', { name: 'Установили контакт 40% 4 из 10 карточек', exact: true }).click();
            await page.locator('[data-report-block="cohort"] .sales-evidence-summary').waitFor();
            assert.equal(await page.locator('[data-report-block="cohort"] .sales-evidence-table tbody tr').count(), 4);
            await close();
            await page.locator('[data-report-block="work"] .sales-kpi').first().click();
            await page.locator('.sales-evidence-table a').first().click();
            await page.waitForURL('**/Crm/Card/**');
            await open();
            await page.route('**/Crm/AnalyticsEvidence?**', route => route.fulfill({ status: 503, body: 'Unavailable' }));
            await page.locator('[data-report-block="work"] .sales-kpi').first().click();
            await page.locator('[data-evidence-host] [role="alert"]').waitFor();
            assert.match(await page.locator('[data-evidence-host]:visible').innerText(), /не нулевой результат/);
            await page.unroute('**/Crm/AnalyticsEvidence?**');
            await close();
        }

        if (suite === 'all' || suite === 'management') {
            await page.getByRole('combobox', { name: 'Фильтр по менеджеру' }).selectOption('demo-m43');
            await page.getByRole('button', { name: 'Применить', exact: true }).click();
            await page.waitForURL('**managerUserId=demo-m43**');
            assert.equal(await count('sales.received'), 0);
            assert.equal(await count('sales.contacts'), 2);
            assert.equal(await count('sales.refusals'), 2);
            assert.match(await page.locator('[data-report-block="cohort"]').innerText(), /процент не рассчитывается/);
            await open('demo-m44');
            assert.equal(await count('sales.successes'), 1, 'Missing shift must not suppress work');
            await open();
            await page.locator('.sales-manager-table tr').filter({ hasText: 'Лаб · Анна' })
                .getByRole('button', { name: '8', exact: true }).click();
            await page.locator('[data-report-block="stages"] .sales-evidence-summary').waitFor();
            assert.match(await page.locator('[data-report-block="stages"] [data-evidence-host]').innerText(), /КЕЙС 23/);
            await close();
            await open('demo-m41', '2026-08-20');
            assert.equal(await count('sales.successes'), 1, 'Late success is on its event date');
            assert.equal(await count('sales.received'), 0);
            await open();
            await page.getByRole('combobox', { name: 'Фильтр по менеджеру' }).selectOption('demo-m44');
            await page.getByRole('button', { name: 'Применить', exact: true }).click();
            await page.waitForURL('**managerUserId=demo-m44**');
            await Promise.all([page.waitForNavigation(),
                page.getByRole('combobox', { name: 'Выбор офиса' }).selectOption({ label: 'ДЕМО 1 · Офис' })]);
            await page.locator('[data-report-block="work"]').waitFor();
            assert.equal(await page.getByRole('combobox', { name: 'Фильтр по менеджеру' }).inputValue(), '');
            assert.equal(new URL(page.url()).searchParams.get('from'), '2026-09-05');
            await Promise.all([page.waitForNavigation(),
                page.getByRole('combobox', { name: 'Выбор офиса' }).selectOption({ label: 'ДЕМО 4 · Разбор кейсов' })]);
            await page.locator('[data-report-block="work"]').waitFor();
        }

        if (suite === 'all' || suite === 'layout') {
            for (const width of [1920, 1536, 1280, 768, 390]) {
                await page.setViewportSize({ width, height: 900 });
                await settle();
                const geometry = () => page.evaluate(() => ({
                    page: document.documentElement.scrollWidth, viewport: document.documentElement.clientWidth,
                    blocks: [...document.querySelectorAll('[data-report-block]')].map(e => ({
                        x: e.getBoundingClientRect().x, width: e.getBoundingClientRect().width
                    }))
                }));
                const before = await geometry();
                assert.ok(before.page <= before.viewport, 'No page overflow at ' + width);
                await page.locator('[data-report-block="work"] .sales-kpi').nth(1).click();
                await page.locator('[data-report-block="work"] .sales-evidence-summary').waitFor();
                const after = await geometry();
                assert.deepEqual(after.blocks, before.blocks, 'No width jump at ' + width);
                assert.ok(after.page <= after.viewport, 'No overflow with evidence at ' + width);
                await close();
            }
        }
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ status: 'PASS', suite, pageErrors: errors }));
    } finally { await browser.close(); }
}
module.exports = run;
if (require.main === module) run().catch(error => { console.error(error); process.exitCode = 1; });
