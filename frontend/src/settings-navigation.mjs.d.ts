export type SettingsSection = { id: string; label: string; status: 'active' | 'planned' };
export const DEFAULT_SETTINGS_SECTION: string;
export const SETTINGS_SECTIONS: SettingsSection[];
export function isSettingsSection(nav: string): boolean;
export function toggleSettings(state: { nav: string; settingsExpanded: boolean }): { nav: string; settingsExpanded: boolean };
export function selectNavigation(nav: string): { nav: string; settingsExpanded: boolean };
