// Compatibility entry point for the rewritten three-block page.
require('./crm-analytics-three-blocks.cjs')('management').catch(error => {
    console.error(error);
    process.exitCode = 1;
});
