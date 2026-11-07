// Convenient entry point; build.ts still owns all rendering, fingerprints and output checks.
process.env.MERIT_TRAINING_URL ??= 'https://localhost:7343';
if (!process.argv.includes('--feature')) process.argv.push('--feature', 'signIn');
if (!process.argv.includes('--burn-in')) process.argv.push('--burn-in');
await import('./build.ts');
