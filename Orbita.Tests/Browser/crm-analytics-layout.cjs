// Run against the isolated local analytics fixture (not production).
// NODE_PATH must contain Playwright; no browser downloads or personal Chrome profile.
// Set ASSERT_LAYOUT=0 to report a baseline without enforcing geometry.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const browserPath = process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const strict = process.env.ASSERT_LAYOUT !== '0';
const api = process.env.DEMO_URL || 'http://127.0.0.1:5280';
if (!['127.0.0.1', 'localhost'].includes(new URL(api).hostname)) throw new Error('Local fixture only');

(async () => {
    const browser = await chromium.launch({ executablePath: browserPath, headless: true });
    const observations = [];
    try {
        const sizes = [[1920, 750], [1920, 1080], [1920, 1440], [1536, 864], [1280, 750], [390, 750], [1920, 1080, true], [1536, 864, true]];
        for (const [width, height, collapsed = false] of sizes) {
            const context = await browser.newContext({ viewport: { width, height }, timezoneId: 'Asia/Yekaterinburg' });
            await context.route('**/*', route => ['127.0.0.1', 'localhost'].includes(new URL(route.request().url()).hostname) ? route.continue() : route.abort());
            if (collapsed) await context.addInitScript(() => localStorage.setItem('orbita-sidebar-collapsed', '1'));
            const page = await context.newPage();
            await page.goto(api + '/demo/open?office=1&from=2026-09-01&to=2026-09-05');
            await page.waitForURL('**/Crm/Analytics?**');
            await page.locator('.crm-analytics-reports a').nth(1).click();
            await page.waitForURL('**report=leads**');
            await page.locator('.crm-analytics-kpis').waitFor();
            const settle = () => page.evaluate(async () => {
                await document.fonts.ready;
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            });
            const measure = () => page.evaluate(() => {
                const box = selector => { const r = document.querySelector(selector).getBoundingClientRect(); return { x:r.x, y:r.y, width:r.width, height:r.height }; };
                return { main:box('.crm-analytics'), filters:box('.crm-analytics-filter-card'), tabs:box('.crm-analytics-lead-views'), scrollY, scrollX,
                    maxScroll:Math.max(0, document.documentElement.scrollHeight - innerHeight), viewport:document.documentElement.clientWidth,
                    pageWidth:document.documentElement.scrollWidth };
            });
            await settle();
            // Put the switcher near the top without asking the browser to scroll on click.
            await page.evaluate(() => {
                const tabs = document.querySelector('.crm-analytics-lead-views');
                scrollTo(0, Math.max(0, tabs.getBoundingClientRect().top + scrollY - 160));
            });
            await settle();
            let previous = await measure();
            for (const mode of ['progress', 'snapshot', 'progress', 'snapshot']) {
                await page.locator('.crm-analytics-lead-views a').nth(mode === 'progress' ? 1 : 0).click();
                await page.waitForURL('**leadView=' + mode + '**');
                await page.locator(mode === 'progress' ? '.crm-analytics-decomposition' : '.crm-analytics-kpis').waitFor();
                await settle();
                const current = await measure();
                const change = { viewport:`${width}x${height}`, collapsed, mode, dx:current.main.x-previous.main.x, dw:current.main.width-previous.main.width,
                    tabsDy:current.tabs.y-previous.tabs.y, scrollBefore:previous.scrollY, scrollAfter:current.scrollY, maxScroll:current.maxScroll };
                observations.push(change);
                if (strict) {
                    for (const key of ['main', 'filters', 'tabs']) {
                        assert.ok(Math.abs(current[key].x-previous[key].x) <= 1, `${width}/${mode}: ${key} moved horizontally`);
                        assert.ok(Math.abs(current[key].width-previous[key].width) <= 1, `${width}/${mode}: ${key} changed width`);
                    }
                    const expectedScroll = Math.min(previous.scrollY, current.maxScroll);
                    assert.ok(Math.abs(current.scrollY-expectedScroll) <= 1, `${width}/${mode}: scroll jumped ${previous.scrollY} -> ${current.scrollY}`);
                    assert.ok(Math.abs((current.tabs.y+current.scrollY)-(previous.tabs.y+previous.scrollY)) <= 1, `${width}/${mode}: tab document position changed`);
                    assert.equal(current.scrollX, 0);
                    assert.ok(current.pageWidth <= current.viewport, `${width}/${mode}: horizontal page overflow`);
                }
                previous = current;
            }
            await context.close();
        }
        console.log(JSON.stringify({status: strict ? 'PASS' : 'BASELINE', observations}, null, 2));
    } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
