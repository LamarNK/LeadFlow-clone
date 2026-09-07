# CRM analytics release — 2026-09-07

## Scope

Two separate reports: dated team activity on all cards (including old leads), and
results for a selected receipt/first-assignment cohort. Includes exact evidence
drilldowns, explicit date-basis selection, historical actors and event offices,
legacy-data warnings, and independent office stage funnels. Existing access
policies and office stage configuration are unchanged.

Integrated with master `9a63cbf`. The existing Redis analytics cache is retained:
the new payload namespace and cohort/options key prevent incompatible cached
results; evidence rebuilds its scoped source sets rather than relying on side
effects of a cached dashboard request.

## Rechecked after integration

- 168 targeted tests passed (analytics, card commands, distribution, import,
  roles/access profiles, controller and cache). This includes three new cache
  integration regressions and Npgsql migration/model metadata comparison.
- Local synthetic dataset: 385 cards / 1737 events; 186 independent aggregate
  checks and 136 exact evidence checks passed.
- Browser management and evidence scenarios passed, including unchanged 403
  for an ordinary manager. All 32 layout switches passed without horizontal
  movement or unintended scroll changes.
- API, Web and local demo compiled. Demo code/data remain outside this repository
  and are not part of the production deployment.
- Restore reports pre-existing dependency advisories for Microsoft.OpenApi 2.0.0
  and SQLitePCLRaw.lib.e_sqlite3 2.1.11. Dependency upgrades are not included here.

## Required deployment gate

These checks do not replace validation on a copy of the real PostgreSQL database.
No production migration or live-data verification was performed during this
release preparation. Do not describe a Git push as a completed deployment.

The API runs pending migrations at startup. This release adds:

- `20260905160000_AddCrmAnalyticsAttributionFields`
- `20260906140000_AddCrmHistoryContext`

They add columns/indexes and backfill existing card/history rows. They do not
delete cards, but backfills and index creation can block concurrent work. Before
starting the updated API:

1. Verify the production host identity and actual container/compose layout.
2. Verify a fresh, restorable backup and rehearse all pending migrations on an
   isolated copy. Disable workers, notifications, imports and telephony on that
   copy. Measure migration time and locks with production-sized history.
3. Compare real offices/managers for fresh unassigned leads, delayed first
   assignment, old-card success, transfers, reopened/reclosed cards, imported
   receipt dates and direct creation at an alternate entry stage. Check totals
   against drilldown IDs and source history, not just the chart labels.
4. Agree a suitable deployment window; preserve the previous API/Web image IDs.
   Update only the API/Web services needed for this release. Do not restart
   PostgreSQL, Asterisk, telephony gateway or worker services as a side effect.
5. Check migration completion, API/Web health, error logs, filter/evidence
   behavior with the production cache, and the same working office samples.

Do not run destructive Down migrations to roll back the UI/API: they drop the
new historical attribution fields. Prefer a reviewed application-image rollback
while retaining additive schema, or a separately planned database recovery.

Legacy history cannot recover facts that were never stored. Inferred office
context is explicitly marked; unknown historical recipients/stages are not
invented. Concurrent close-command atomicity is a separate unverified limitation.
