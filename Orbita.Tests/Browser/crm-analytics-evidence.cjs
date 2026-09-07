// Local-only UI + authorization regression, no personal browser profile.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const path = require('node:path');
(async()=>{
 const browser = await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 try {
  const ctx = await browser.newContext({viewport:{width:1600,height:1000},timezoneId:'Asia/Yekaterinburg'});
  await ctx.route('**/*', r=>new URL(r.request().url()).hostname==='127.0.0.1'?r.continue():r.abort());
  const p=await ctx.newPage(); const errors=[]; p.on('pageerror',e=>errors.push(e.message));
  await p.goto('http://127.0.0.1:5280/demo/open?office=4&from=2026-09-05&manager=demo-m43');
  await p.waitForURL('**/Crm/Analytics?**');
  const link=p.locator('.crm-analytics-period-activity__kpis a').nth(1);
  await link.click(); await p.waitForURL('**/Crm/AnalyticsEvidence?**');
  await p.locator('.crm-analytics-table tbody').waitFor();
  assert.equal(await p.locator('.crm-analytics-table tbody tr').count(),2);
  assert.ok((await p.locator('main').textContent()).includes('В показателе: 2'));
  assert.equal(await p.locator('a[href*="/Crm/Card/"]').count(),2);
  await p.screenshot({path:path.join(__dirname,'../../../outputs/crm-analytics-local-demo/runtime/evidence.png'),fullPage:true,animations:'disabled'});
  await p.locator('a[href*="/Crm/Card/"]').first().click();
  await p.waitForURL('**/Crm/Card/**'); await p.locator('[data-crm-card-id]').waitFor();
  await p.goto('http://127.0.0.1:5280/demo/open?office=4&from=2026-09-05');
  await p.waitForURL('**/Crm/Analytics?**');
  await p.locator('.crm-analytics-reports a').nth(1).click();
  await p.locator('.crm-analytics-kpis').waitFor();
  await p.locator('.crm-analytics-lead-views a').nth(1).click();
  await p.locator('.crm-analytics-funnels').waitFor();
  const important=p.locator('.crm-analytics-funnel-stage').filter({has:p.getByRole('heading',{name:'Лид(Важный)',exact:true})});
  assert.equal((await important.locator('.crm-analytics-funnel-stage__metrics strong').textContent()).trim(),'0');
  assert.ok((await important.textContent()).includes('Создано сразу здесь: 2'));
  assert.equal(await p.locator('.crm-analytics-funnel-connector').count(),0);
  assert.ok((await p.locator('.crm-analytics-decomposition').textContent()).includes('Контакт подтверждён'));
  await p.screenshot({path:path.join(__dirname,'../../../outputs/crm-analytics-local-demo/runtime/important-progress.png'),fullPage:true,animations:'disabled'});
  await important.locator('a').click();
  await p.waitForURL('**/Crm/AnalyticsEvidence?**');
  assert.ok((await p.locator('main').textContent()).includes('В показателе: 0'));
  assert.ok((await p.locator('main').textContent()).includes('Лид(Важный)'));
  const login=await ctx.request.post('http://127.0.0.1:5280/api/v1/auth/login',{data:{email:'demo-m11@orbita.local',password:'LocalDemo2026!'}});
  assert.equal(login.status(),200); const jwt=(await login.json()).token;
  const headers={Authorization:'Bearer '+jwt};
  for(const endpoint of ['analytics','analytics/evidence']) {
   const res=await ctx.request.get('http://127.0.0.1:5280/api/v1/crm/'+endpoint+'?fromUtc=2026-09-04T19:00:00Z&toUtc=2026-09-05T19:00:00Z&metric=activity.closed',{headers});
   assert.equal(res.status(),403,'Ordinary Manager still has no analytics access: '+endpoint);
  }
  assert.deepEqual(errors,[]);
  console.log(JSON.stringify({status:'PASS',checks:['exact closure rows','card navigation','direct important-stage entry is not movement','zero drilldown','ordinary-manager analytics and evidence remain 403'],pageErrors:errors}));
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
