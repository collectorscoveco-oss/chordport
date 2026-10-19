import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import test from 'node:test';
import { findExternalResourceUrls } from './verify-local-assets.mjs';

const frontendRoot = path.resolve(import.meta.dirname, '..');
const execFileAsync = promisify(execFile);

async function collectRegularFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async entry => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return collectRegularFiles(target);
    return entry.isFile() ? [target] : [];
  }));
  return nested.flat();
}

test('production frontend source has no external HTTP(S) resource URLs', async () => {
  const sourceDirectory = path.join(frontendRoot, 'src');
  const assets = await collectRegularFiles(sourceDirectory);
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

test('local asset validator rejects remote loads in Vite-shippable SVG source assets', async () => {
  const fixture = path.join(frontendRoot, 'src', '__local-assets-regression-fixture__.svg');
  const url = 'https://cdn.example.invalid/remote-image.png';
  await writeFile(fixture, `<svg xmlns="http://www.w3.org/2000/svg"><image href="${url}" /></svg>\n`);

  try {
    await assert.rejects(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs'], { cwd: frontendRoot }),
      error => error.stderr.includes('src/__local-assets-regression-fixture__.svg') && error.stderr.includes(url),
    );
  } finally {
    await rm(fixture, { force: true });
  }
});

test('local asset validator rejects remote SVG loads emitted in production build assets', async () => {
  const fixture = path.join(frontendRoot, 'dist', 'assets', '__local-assets-regression-fixture__.svg');
  const url = 'https://cdn.example.invalid/built-remote-image.png';
  await mkdir(path.dirname(fixture), { recursive: true });
  await writeFile(fixture, `<svg xmlns="http://www.w3.org/2000/svg"><image href="${url}" /></svg>\n`);

  try {
    await assert.rejects(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs', '--require-build'], { cwd: frontendRoot }),
      error => error.stderr.includes('dist/assets/__local-assets-regression-fixture__.svg') && error.stderr.includes(url),
    );
  } finally {
    await rm(fixture, { force: true });
  }
});

test('local asset validator traverses fixture directories in production build assets', async () => {
  const fixture = path.join(frontendRoot, 'dist', 'fixtures', 'remote.svg');
  const url = 'https://cdn.example.invalid/built-fixture-remote-image.png';
  await mkdir(path.dirname(fixture), { recursive: true });
  await writeFile(fixture, `<svg xmlns="http://www.w3.org/2000/svg"><image href="${url}" /></svg>\n`);

  try {
    await assert.rejects(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs', '--require-build'], { cwd: frontendRoot }),
      error => error.stderr.includes('dist/fixtures/remote.svg') && error.stderr.includes(url),
    );
  } finally {
    await rm(path.dirname(fixture), { force: true, recursive: true });
  }
});

test('local asset validator rejects remote SVG feImage and use references in production build assets', async () => {
  const fixture = path.join(frontendRoot, 'dist', 'assets', '__local-assets-svg-reference-regression-fixture__.svg');
  const feImageUrl = 'https://cdn.example.invalid/built-filter-image.png';
  const useUrl = 'https://cdn.example.invalid/built-use-reference.svg#icon';
  await mkdir(path.dirname(fixture), { recursive: true });
  await writeFile(fixture, `<svg xmlns="http://www.w3.org/2000/svg"><filter id="filter"><feImage href="${feImageUrl}" /></filter><use href="${useUrl}" /></svg>\n`);

  try {
    await assert.rejects(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs', '--require-build'], { cwd: frontendRoot }),
      error => error.stderr.includes('dist/assets/__local-assets-svg-reference-regression-fixture__.svg')
        && error.stderr.includes(feImageUrl)
        && error.stderr.includes(useUrl),
    );
  } finally {
    await rm(fixture, { force: true });
  }
});

test('local asset validator does not traverse dependency or test fixture directories', async () => {
  const ignoredDirectories = [
    path.join(frontendRoot, 'src', 'node_modules', '__local-assets-regression-fixture__'),
    path.join(frontendRoot, 'src', '__fixtures__', '__local-assets-regression-fixture__'),
  ];
  const url = 'https://cdn.example.invalid/ignored-remote-image.png';

  await Promise.all(ignoredDirectories.map(async directory => {
    await mkdir(directory, { recursive: true });
    await writeFile(path.join(directory, 'remote.svg'), `<svg><image href="${url}" /></svg>\n`);
  }));

  try {
    await assert.doesNotReject(
      execFileAsync(process.execPath, ['scripts/verify-local-assets.mjs'], { cwd: frontendRoot }),
    );
  } finally {
    await Promise.all(ignoredDirectories.map(directory => rm(directory, { force: true, recursive: true })));
  }
});
