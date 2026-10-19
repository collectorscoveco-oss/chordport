export const DEFAULT_SETTINGS_SECTION = 'Media Management';

export const SETTINGS_SECTIONS = [
  { id: 'Media Management', label: 'Media Management', status: 'active' },
  { id: 'Profiles', label: 'Profiles', status: 'planned' },
  { id: 'Quality', label: 'Quality', status: 'planned' },
  { id: 'Custom Formats', label: 'Custom Formats', status: 'planned' },
  { id: 'Indexers', label: 'Indexers', status: 'active' },
  { id: 'Download Clients', label: 'Download Clients', status: 'active' },
  { id: 'Import Lists', label: 'Import Lists', status: 'planned' },
  { id: 'Connect', label: 'Connect', status: 'planned' },
  { id: 'Metadata', label: 'Metadata', status: 'active' },
  { id: 'Tags', label: 'Tags', status: 'planned' },
  { id: 'General', label: 'General', status: 'active' },
  { id: 'UI', label: 'UI', status: 'active' },
];

export const isSettingsSection = nav => SETTINGS_SECTIONS.some(section => section.id === nav);

export const toggleSettings = ({ nav, settingsExpanded }) => {
  if (settingsExpanded) return { nav, settingsExpanded: false };
  return { nav: isSettingsSection(nav) ? nav : DEFAULT_SETTINGS_SECTION, settingsExpanded: true };
};

export const selectNavigation = nav => ({ nav, settingsExpanded: isSettingsSection(nav) });
