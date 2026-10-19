import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import {
  DEFAULT_SETTINGS_SECTION,
  SETTINGS_SECTIONS,
  selectNavigation,
  toggleSettings,
} from '../src/settings-navigation.mjs';

const labels = [
  'Media Management',
  'Profiles',
  'Quality',
  'Custom Formats',
  'Indexers',
  'Download Clients',
  'Import Lists',
  'Connect',
  'Metadata',
  'Tags',
  'General',
  'UI',
];

test('settings submenu matches the requested Arr-inspired label order', () => {
  assert.deepEqual(SETTINGS_SECTIONS.map(section => section.label), labels);
  assert.equal(DEFAULT_SETTINGS_SECTION, 'Media Management');
});

test('opening Settings selects Media Management and toggles its accordion state', () => {
  assert.deepEqual(toggleSettings({ nav: 'Music', settingsExpanded: false }), {
    nav: 'Media Management', settingsExpanded: true,
  });
  assert.deepEqual(toggleSettings({ nav: 'Indexers', settingsExpanded: true }), {
    nav: 'Indexers', settingsExpanded: false,
  });
});

test('settings selection keeps the accordion open while root navigation closes it', () => {
  assert.deepEqual(selectNavigation('Indexers'), { nav: 'Indexers', settingsExpanded: true });
  assert.deepEqual(selectNavigation('System'), { nav: 'System', settingsExpanded: false });
});

test('every existing functional section has a route definition', () => {
  const functionalIds = SETTINGS_SECTIONS.filter(section => section.status === 'active').map(section => section.id);
  assert.deepEqual(functionalIds, ['Media Management', 'Indexers', 'Download Clients', 'Metadata', 'General', 'UI']);
});

test('Download Clients retains Manage Indexers through the shared settings transition', async () => {
  const source = await readFile(new URL('../src/main.tsx', import.meta.url), 'utf8');

  assert.match(source, /nav==='Download Clients'[\s\S]*?<button[^>]*onClick=\{\(\)=>choose\('Indexers'\)\}[^>]*>Manage Indexers<\/button>/);
});

test('settings accordion uses a labelled native-button section rather than ARIA menu roles', async () => {
  const source = await readFile(new URL('../src/main.tsx', import.meta.url), 'utf8');

  assert.match(source, /<section id="settings-submenu" className="settings-submenu" aria-label="Settings sections">/);
  assert.match(source, /aria-expanded=\{expanded\} aria-controls="settings-submenu"/);
  assert.doesNotMatch(source, /role="(?:menu|menuitem)"/);
});
