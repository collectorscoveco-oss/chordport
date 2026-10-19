import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { readdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { findExternalResourceUrls } from './verify-local-assets.mjs';

const frontendRoot = path.resolve(import.meta.dirname, '..');
const textAssetPattern = /\.(?:css|html|js|jsx|mjs|ts|tsx)$/i;
const execFileAsync = promisify(execFile);

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

test('findExternalResourceUrls finds HTTP(S) literals in shipped source syntax', () => {
  const urls = [
    'https://cdn.example.invalid/image.png',
    'https://cdn.example.invalid/image-2x.png',
    'https://cdn.example.invalid/widget.js',
    'https://cdn.example.invalid/font.woff2',
    'https://cdn.example.invalid/legacy.js',
  ];
  const content = `
    const image = <img src={"${urls[0]}"} srcSet={"${urls[1]} 2x"} />;
    import '${urls[2]}';
    .font { src: url('${urls[3]}'); }
    <script src="${urls[4]}"></script>
  `;

  assert.deepEqual(findExternalResourceUrls(content), urls);
});

test('local asset validator reports a source filename and remote URL', async () => {
  const fixture = path.join(frontendRoot, 'src', '__local-assets-regression-fixture__.tsx');
  const url = 'https://cdn.example.invalid/fixture.png';
  await writeFile(fixture, `export const fixture = <img src={"${url}"} />;\n`);

  try {
    await assert.rejects(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs'], { cwd: frontendRoot }),
      error => error.stderr.includes('src/__local-assets-regression-fixture__.tsx') && error.stderr.includes(url),
    );
  } finally {
    await rm(fixture, { force: true });
  }
});
