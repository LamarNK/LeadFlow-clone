// Compatibility entry point for the rewritten three-block page.
require('./crm-analytics-three-blocks.cjs')('layout').catch(error => {
    console.error(error);
    process.exitCode = 1;
});
