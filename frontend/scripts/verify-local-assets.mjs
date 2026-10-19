import { access, readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const frontendRoot = path.resolve(import.meta.dirname, '..');
const sourceIgnoredDirectoryNames = new Set(['.git', 'dist', 'node_modules', 'fixtures', '__fixtures__']);
const externalBuildResourcePatterns = [
  /@import\s+(?:url\(\s*)?['"]?(https?:\/\/[^\s'"`<>)}]+)/gi,
  /url\(\s*['"]?(https?:\/\/[^\s'"`<>)}]+)/gi,
  /<(?:audio|embed|iframe|img|image|feImage|link|object|script|source|track|use|video)\b[^>]*?\b(?:data|href|poster|src)\s*=\s*['"](https?:\/\/[^\s'"`<>)}]+)/gi,
  /\b(?:fetch|import)\s*\(\s*['"](https?:\/\/[^\s'"`<>)}]+)/gi,
  /\bnew\s+(?:Shared)?Worker\s*\(\s*['"](https?:\/\/[^\s'"`<>)}]+)/gi,
];

export function findExternalResourceUrls(content) {
  return [...content.matchAll(/https?:\/\/[^\s'"`<>)}]+/gi)].map(match => match[0]);
}

function findExternalBuildResourceUrls(content) {
  return externalBuildResourcePatterns.flatMap(pattern => [...content.matchAll(pattern)].map(match => match[1]));
}

async function pathExists(target) {
  try {
    await access(target);
    return true;
  } catch {
    return false;
  }
}

async function collectRegularFiles(target, ignoredDirectoryNames = new Set()) {
  const entries = await readdir(target, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async entry => {
    const asset = path.join(target, entry.name);
    if (entry.isDirectory()) return ignoredDirectoryNames.has(entry.name) ? [] : collectRegularFiles(asset, ignoredDirectoryNames);
    return entry.isFile() ? [asset] : [];
  }));
  return nested.flat();
}

async function verifyLocalAssets(requireBuild) {
  const sourceAssets = [path.join(frontendRoot, 'index.html'), ...await collectRegularFiles(path.join(frontendRoot, 'src'), sourceIgnoredDirectoryNames)];
  const buildDirectory = path.join(frontendRoot, 'dist');

  if (requireBuild && !await pathExists(buildDirectory)) {
    throw new Error('Production build assets are required; run npm run build first.');
  }

  const findings = [];
  for (const asset of sourceAssets) {
    const urls = findExternalResourceUrls(await readFile(asset, 'utf8'));
    findings.push(...urls.map(url => `${path.relative(frontendRoot, asset)}: ${url}`));
  }

  const buildAssets = await pathExists(buildDirectory) ? await collectRegularFiles(buildDirectory) : [];
  for (const asset of buildAssets) {
    const urls = findExternalBuildResourceUrls(await readFile(asset, 'utf8'));
    findings.push(...urls.map(url => `${path.relative(frontendRoot, asset)}: ${url}`));
  }

  if (findings.length > 0) {
    throw new Error(`External HTTP(S) resource URLs are not allowed:\n${findings.join('\n')}`);
  }

  console.log(`Verified ${sourceAssets.length} frontend source and ${buildAssets.length} build assets contain no external HTTP(S) resource URLs.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await verifyLocalAssets(process.argv.includes('--require-build'));
}
