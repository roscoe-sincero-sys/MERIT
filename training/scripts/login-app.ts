// Isolated, local-only mail and app instances for the complete login series.
// Run this in one terminal; in another use MERIT_TRAINING_URL=https://localhost:7343.
import { spawn, type ChildProcess } from 'node:child_process';
import { existsSync, mkdirSync, openSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { DATABASE, PATHS, REPO } from '../lib/config.ts';
import { assertTrainingTarget } from '../lib/privacy.ts';

assertTrainingTarget();
const mailpit = process.env.MERIT_TRAINING_MAILPIT_EXE
  ?? join(process.env.LOCALAPPDATA ?? '', 'MeritDevTools', 'mailpit', 'mailpit.exe');
if (!existsSync(mailpit)) throw new Error('Install Mailpit with sourceCode/tools/mail/Install-Mailpit.ps1 first.');
mkdirSync(PATHS.work, { recursive: true });
const settings = JSON.parse(readFileSync(join(REPO, 'sourceCode/src/Merit.Web/appsettings.Development.json'), 'utf8'));
const base: string = process.env.MERIT_TRAINING_CONNECTION ?? settings.ConnectionStrings.Merit;
const connection = /Database=[^;]+/i.test(base) ? base.replace(/Database=[^;]+/i, `Database=${DATABASE}`) : `${base};Database=${DATABASE}`;
const children: ChildProcess[] = [];
function start(name: string, file: string, args: string[], env: NodeJS.ProcessEnv = process.env): void {
  const child = spawn(file, args, { cwd: join(REPO, 'sourceCode/src/Merit.Web'), windowsHide: true, env,
    stdio: ['ignore', openSync(join(PATHS.work, `${name}.log`), 'a'), openSync(join(PATHS.work, `${name}.err.log`), 'a')] });
  children.push(child);
  child.on('error', error => { console.error(`${name}: ${error.message}`); stop(1); });
  child.on('exit', code => { console.log(`${name} exited (${code}).`); stop(code ?? 1); });
}
let stopping = false;
function stop(code: number): void {
  if (stopping) return;
  stopping = true;
  for (const child of children) child.kill();
  process.exitCode = code;
}
process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));
start('login-mailpit', mailpit, ['--listen', '127.0.0.1:18025', '--smtp', '127.0.0.1:11025',
  '--disable-version-check', '--enable-chaos', '--max', '500']);
const common = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ConnectionStrings__Merit: connection,
  Merit__SignInAttemptsPerMinute: '10000', Merit__MfaAttemptsPerMinute: '10000', Merit__EmailCodeSendsPerMinute: '10000',
  EmailCodes__MaxCodesPerAccount: '100', EmailCodes__MaxCodesPerAddress: '1000',
  PasswordReset__CooldownSeconds: '0', PasswordReset__MaxLinksPerAccount: '100', PasswordReset__MaxLinksPerAddress: '1000' };
const app = process.env.MERIT_TRAINING_APP ?? join(REPO, 'sourceCode/src/Merit.Web/bin/Debug/net9.0/Merit.Web.dll');
if (!existsSync(app)) throw new Error('Build the application first: dotnet build sourceCode/src/Merit.Web.');
start('login-app', 'dotnet', [app, '--urls', 'https://localhost:7343'], {
  ...common, Email__Provider: 'Smtp', Email__Smtp__Host: '127.0.0.1', Email__Smtp__Port: '11025',
  Email__Smtp__Security: 'None', Email__Smtp__FromAddress: 'merit-training@localhost.test', Merit__PublicBaseUrl: 'https://localhost:7343',
});
// A separate configuration demonstrates unavailable email and network throttling without changing the main app.
start('login-no-email', 'dotnet', [app, '--urls', 'https://localhost:7344'], {
  ...common, Email__Provider: 'Disabled', Merit__SignInAttemptsPerMinute: '1',
});
console.log('Login training app: https://localhost:7343; no-email/rate-limit example: https://localhost:7344.');
console.log('Local-only captured mail: http://127.0.0.1:18025. Logs: training/.work/login-*.log. Ctrl+C stops these instances.');
