import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { expect, expectAccessible, test } from './helpers';

test('native HL7 uploads for both versions are processed by the worker and unsupported messages pause visibly', async ({ asUser }) => {
  test.skip(process.env.MERIT_E2E_DATABASE !== 'MERIT_IT', 'Uses isolated synthetic intake records only in MERIT_IT.');
  test.setTimeout(90000);
  const page = await asUser('supervisor');
  const identity = randomUUID().replace(/-/g, '');
  for (const [version, count] of [['231', 1], ['251', 2]] as const) {
    await page.goto('/intake');
    await expect(page.locator('#file')).toHaveAttribute('accept', /\.hl7/);
    const option = page.locator('#intakeProfileId option').filter({ hasText: 'Chesapeake Reference Laboratory HL7' }).first();
    await page.locator('#intakeProfileId').selectOption((await option.getAttribute('value'))!);
    const content = readFileSync(resolve(__dirname, `../../../samples/chesapeake_hl7_${version}.hl7`), 'utf8')
      .replace('SampleAster^Adaline', `E2ENative${identity}^Adaline${identity}`)
      .replaceAll(`DEMO-HL7-${version}`, `E2E-${version}-${identity}`);
    const name = `native-${identity}-${version}.hl7`;
    await page.locator('#file').setInputFiles({ name, mimeType: 'text/plain', buffer: Buffer.from(content) });
    await page.getByRole('button', { name: 'Upload', exact: true }).click();
    await expect(page.getByText('File received.', { exact: false }).first()).toBeVisible();
    const card = page.locator('#batches .usa-card__container').filter({ has: page.getByRole('heading', { name, exact: true }) });
    await expect(card).toContainText(`loaded ${count}`, { timeout: 30000 });
    await expect(card).toContainText('held 0');
  }
  await page.goto('/intake');
  const option = page.locator('#intakeProfileId option').filter({ hasText: 'Chesapeake Reference Laboratory HL7' }).first();
  await page.locator('#intakeProfileId').selectOption((await option.getAttribute('value'))!);
  const badName = `unsupported-${identity}.hl7`;
  const content = readFileSync(resolve(__dirname, '../../../samples/chesapeake_hl7_251.hl7'), 'utf8')
    .replace('|P|2.5.1', '|P|2.4').replace('DEMO-251', `UNSUPPORTED-${identity}`);
  await page.locator('#file').setInputFiles({ name: badName, mimeType: 'text/plain', buffer: Buffer.from(content) });
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  const card = page.locator('#batches .usa-card__container').filter({ has: page.getByRole('heading', { name: badName, exact: true }) });
  await expect(card).toContainText('Paused', { timeout: 30000 });
  await expect(card).toContainText('MSH-12 must be version 2.3.1 or 2.5.1');
  await expect(card).toContainText('loaded 0');
  await expectAccessible(page, 'native HL7 intake and paused-batch reason');
});
