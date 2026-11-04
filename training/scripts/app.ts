// Starts the MERIT web app on the training database for recording, in the foreground (Ctrl+C stops it). The
// connection string is the one in src/Merit.Web/appsettings.Development.json with the database swapped; that file is
// read, not changed, and the password is never printed. Usage: npm run app
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { DATABASE, REPO } from '../lib/config.ts';
import { assertTrainingTarget } from '../lib/privacy.ts';

assertTrainingTarget();
const source = join(REPO, 'sourceCode');
const settings = JSON.parse(readFileSync(join(source, 'src/Merit.Web/appsettings.Development.json'), 'utf8'));
const base: string = process.env.MERIT_TRAINING_CONNECTION ?? settings.ConnectionStrings.Merit;
const connection = /Database=[^;]+/i.test(base) ? base.replace(/Database=[^;]+/i, `Database=${DATABASE}`) : `${base};Database=${DATABASE}`;
console.log(`Starting MERIT on ${DATABASE} at https://localhost:7243 ...`);
const child = spawn('dotnet', ['run', '--project', 'src/Merit.Web', '--launch-profile', 'Merit.Web', '--no-launch-browser'], {
  cwd: source, stdio: 'inherit',
  // Recording signs in repeatedly; lift the per-minute sign-in limit as the e2e tests do.
  env: { ...process.env, ConnectionStrings__Merit: connection, Merit__SignInAttemptsPerMinute: '10000' },
});
child.on('exit', code => process.exit(code ?? 0));
