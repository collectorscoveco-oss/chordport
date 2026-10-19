import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { findExternalResourceUrls } from './verify-local-assets.mjs';

const frontendRoot = path.resolve(import.meta.dirname, '..');
const textAssetPattern = /\.(?:css|html|js|jsx|mjs|ts|tsx)$/i;

async function collectTextAssets(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async entry => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return collectTextAssets(target);
    return textAssetPattern.test(entry.name) ? [target] : [];
  }));
  return nested.flat();
}

test('production frontend source has no external HTTP(S) resource URLs', async () => {
  const sourceDirectory = path.join(frontendRoot, 'src');
  const assets = await collectTextAssets(sourceDirectory);
  const findings = [];

  for (const asset of assets) {
    const urls = findExternalResourceUrls(await readFile(asset, 'utf8'));
    findings.push(...urls.map(url => `${path.relative(frontendRoot, asset)}: ${url}`));
  }

  assert.deepEqual(findings, []);
});
