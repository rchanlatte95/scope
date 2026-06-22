// =============================================================================
// SettingsView (FE-2b) — Tabbed Settings page.
//
// Tabs: Accessibility (real), Workspace (honest stub).
// Tab selection is driven by the URL hash ?tab= param.
// Default tab: accessibility.
//
// Accessibility tab wires to FE-0a lib/prefs.ts:
//   - reads current prefs from localStorage on mount
//   - writes on each control change (writePref)
//   - applies to document root immediately (applyPrefs)
//   - on reload, main.tsx restores from localStorage (persistence covered)
//
// Tab semantics: ARIA tablist/tab/tabpanel pattern with keyboard arrow nav.
// No raw hex. --scope-color-* tokens only.
// =============================================================================

import { useEffect, useRef, useState } from 'react';
import { PageHeader } from '../components/PageHeader';
import { currentRoute, navigate } from '../lib/router';
import { readPrefs, writePref, applyPrefs, type Prefs, type AssumedCostModel } from '../lib/prefs';
import { friendlyModelName } from '../lib/modelName';
import styles from './SettingsView.module.css';

// ---------------------------------------------------------------------------
// Tab definitions
// ---------------------------------------------------------------------------

type TabId = 'accessibility' | 'workspace';

interface TabDef {
  id: TabId;
  label: string;
}

const TABS: TabDef[] = [
  { id: 'accessibility', label: 'Accessibility' },
  { id: 'workspace',     label: 'Workspace' },
];

function resolveTab(raw: string | null | undefined): TabId {
  if (raw === 'accessibility' || raw === 'workspace') return raw;
  return 'accessibility';
}

// ---------------------------------------------------------------------------
// Accessibility tab content
// ---------------------------------------------------------------------------

function AccessibilityTab({ prefs, onChange }: {
  prefs: Prefs;
  onChange: <K extends keyof Prefs>(key: K, value: Prefs[K]) => void;
}) {
  return (
    <div className={styles.tabContent}>
      <p className={styles.tabDescription}>
        Display preferences apply immediately and persist across reloads.
        They do not affect server or agent behavior.
      </p>

      {/* Theme */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Theme</legend>
        <p className={styles.fieldHelp} id="theme-help">
          System follows your OS preference; Light or Dark override it.
        </p>
        <div className={styles.radioGroup} role="group" aria-describedby="theme-help">
          {(['system', 'light', 'dark'] as const).map((value) => (
            <label key={value} className={styles.radioLabel}>
              <input
                type="radio"
                name="theme"
                value={value}
                checked={prefs.theme === value}
                onChange={() => { onChange('theme', value); }}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>
                {value === 'system' ? 'System' : value === 'light' ? 'Light' : 'Dark'}
              </span>
            </label>
          ))}
        </div>
      </fieldset>

      {/* Font scale */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Text size</legend>
        <p className={styles.fieldHelp} id="font-scale-help">
          Scales all UI text. 100% is the default; 130% aids readability on large screens.
        </p>
        <div className={styles.radioGroup} role="group" aria-describedby="font-scale-help">
          {([100, 115, 130] as const).map((value) => (
            <label key={value} className={styles.radioLabel}>
              <input
                type="radio"
                name="fontScale"
                value={String(value)}
                checked={prefs.fontScale === value}
                onChange={() => { onChange('fontScale', value); }}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>{value}%</span>
            </label>
          ))}
        </div>
      </fieldset>

      {/* Readable font */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Readable font</legend>
        <p className={styles.fieldHelp} id="readable-font-help">
          Choose a high-legibility typeface. Applies immediately and persists.
        </p>
        <div className={styles.radioGroup} role="group" aria-describedby="readable-font-help">
          {([
            { value: 'off', label: 'Default' },
            { value: 'atkinson', label: 'Atkinson Hyperlegible' },
            { value: 'opendyslexic', label: 'OpenDyslexic' },
          ] as const).map(({ value, label }) => (
            <label key={value} className={styles.radioLabel}>
              <input
                type="radio"
                name="readableFont"
                value={value}
                checked={prefs.readableFont === value}
                onChange={() => { onChange('readableFont', value); }}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>{label}</span>
            </label>
          ))}
        </div>
      </fieldset>

      {/* Visual cues */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Visual cues</legend>
        <p className={styles.fieldHelp} id="visual-cues-help">
          Strong adds additional shape and pattern indicators to status and trace markers.
        </p>
        <div className={styles.radioGroup} role="group" aria-describedby="visual-cues-help">
          {(['standard', 'strong'] as const).map((value) => (
            <label key={value} className={styles.radioLabel}>
              <input
                type="radio"
                name="visualCues"
                value={value}
                checked={prefs.visualCues === value}
                onChange={() => { onChange('visualCues', value); }}
                className={styles.radioInput}
              />
              <span className={styles.radioText}>{value === 'standard' ? 'Standard' : 'Strong'}</span>
            </label>
          ))}
        </div>
      </fieldset>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Assumed cost model options — the four model tiers the user can peg estimates to.
// Used for the scope_estimated fallback when no model is known.
// ---------------------------------------------------------------------------

const ASSUMED_COST_MODEL_OPTIONS: { value: AssumedCostModel; label: string }[] = [
  { value: 'claude-sonnet-4-6', label: friendlyModelName('claude-sonnet-4-6') },
  { value: 'claude-opus-4-8',   label: friendlyModelName('claude-opus-4-8') },
  { value: 'claude-haiku-4-5',  label: friendlyModelName('claude-haiku-4-5') },
  { value: 'claude-fable-5',    label: friendlyModelName('claude-fable-5') },
];

// ---------------------------------------------------------------------------
// Timezone options — curated list of common IANA zones.
// The sentinel 'system' means "use the browser/OS timezone".
// ---------------------------------------------------------------------------

interface TzOption {
  value: string;
  label: string;
}

const TIMEZONE_OPTIONS: TzOption[] = [
  { value: 'system', label: 'System default' },
  { value: 'UTC', label: 'UTC' },
  { value: 'America/New_York', label: 'Eastern (New York)' },
  { value: 'America/Chicago', label: 'Central (Chicago)' },
  { value: 'America/Denver', label: 'Mountain (Denver)' },
  { value: 'America/Los_Angeles', label: 'Pacific (Los Angeles)' },
  { value: 'America/Anchorage', label: 'Alaska (Anchorage)' },
  { value: 'Pacific/Honolulu', label: 'Hawaii (Honolulu)' },
  { value: 'America/Sao_Paulo', label: 'Brasilia (Sao Paulo)' },
  { value: 'Europe/London', label: 'London' },
  { value: 'Europe/Paris', label: 'Central European (Paris)' },
  { value: 'Europe/Helsinki', label: 'Eastern European (Helsinki)' },
  { value: 'Europe/Moscow', label: 'Moscow' },
  { value: 'Asia/Dubai', label: 'Gulf (Dubai)' },
  { value: 'Asia/Kolkata', label: 'India (Kolkata)' },
  { value: 'Asia/Bangkok', label: 'Indochina (Bangkok)' },
  { value: 'Asia/Shanghai', label: 'China (Shanghai)' },
  { value: 'Asia/Tokyo', label: 'Japan (Tokyo)' },
  { value: 'Australia/Sydney', label: 'Australia Eastern (Sydney)' },
  { value: 'Pacific/Auckland', label: 'New Zealand (Auckland)' },
];

// ---------------------------------------------------------------------------
// Workspace tab — timezone control (real) + operational info (coming soon)
// ---------------------------------------------------------------------------

function WorkspaceTab({ prefs, onChange }: {
  prefs: Prefs;
  onChange: <K extends keyof Prefs>(key: K, value: Prefs[K]) => void;
}) {
  return (
    <div className={styles.tabContent}>
      <p className={styles.tabDescription}>
        Workspace display preferences apply immediately and persist across reloads.
        They do not affect server or agent behavior.
      </p>

      {/* Timezone */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Timezone</legend>
        <p className={styles.fieldHelp} id="timezone-help">
          Controls how absolute timestamps are displayed across the dashboard.
          System default uses your browser or OS timezone.
        </p>
        <div className={styles.selectGroup}>
          <label htmlFor="timezone-select" className={styles.selectLabel}>
            Display timezone
          </label>
          <select
            id="timezone-select"
            className={styles.select}
            value={prefs.timezone}
            aria-describedby="timezone-help"
            onChange={(e) => { onChange('timezone', e.target.value); }}
          >
            {TIMEZONE_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      </fieldset>

      {/* Cost estimation model peg */}
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Cost estimation</legend>
        <p className={styles.fieldHelp} id="assumed-cost-model-help">
          When no model is known for a task, cost is estimated using the selected tier.
          Changing this re-fetches the accounting rollup immediately.
        </p>
        <div className={styles.selectGroup}>
          <label htmlFor="assumed-cost-model-select" className={styles.selectLabel}>
            Assumed model tier
          </label>
          <select
            id="assumed-cost-model-select"
            className={styles.select}
            value={prefs.assumedCostModel}
            aria-describedby="assumed-cost-model-help"
            onChange={(e) => { onChange('assumedCostModel', e.target.value as AssumedCostModel); }}
          >
            {ASSUMED_COST_MODEL_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      </fieldset>
    </div>
  );
}

// ---------------------------------------------------------------------------
// SettingsView — default export (import contract preserved from FE-2a)
// ---------------------------------------------------------------------------

export default function SettingsView() {
  // Read the active tab from the URL ?tab= param on every render.
  // App.tsx does not pass tab as a prop, so we read currentRoute() directly.
  // On hashchange, the parent App re-renders and we re-read here.
  const route = currentRoute();
  const activeTab = resolveTab(route.tab);

  // Prefs state — loaded from localStorage on mount; kept in sync on change.
  const [prefs, setPrefs] = useState<Prefs>(() => readPrefs());

  // If the tab changes (via URL) while mounted, we don't need to re-read prefs.
  // But on first mount sync prefs from localStorage (already done in useState init).

  // Tab list ref for keyboard arrow navigation.
  const tabListRef = useRef<HTMLDivElement>(null);

  // Navigate to a tab by updating the URL hash (which triggers a hashchange -> re-render).
  function selectTab(id: TabId) {
    navigate({ name: 'settings', tab: id });
  }

  // Apply prefs to document root whenever prefs change.
  useEffect(() => {
    applyPrefs(prefs, document.documentElement);
  }, [prefs]);

  // Pref change handler: write to localStorage and update state.
  function handlePrefChange<K extends keyof Prefs>(key: K, value: Prefs[K]) {
    writePref(key, value);
    setPrefs((prev) => ({ ...prev, [key]: value }));
  }

  // Keyboard arrow navigation on the tablist.
  function handleTabKeyDown(e: React.KeyboardEvent<HTMLButtonElement>, index: number) {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      const dir = e.key === 'ArrowRight' ? 1 : -1;
      const next = (index + dir + TABS.length) % TABS.length;
      // TABS indices are safe: `next` is computed modulo TABS.length (always in-bounds).
      const nextTab = TABS[next];
      if (nextTab !== undefined) selectTab(nextTab.id);
      // Move focus to the next tab button.
      const buttons = tabListRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]');
      if (buttons) {
        buttons[next]?.focus();
      }
    }
    if (e.key === 'Home') {
      e.preventDefault();
      const firstTab = TABS[0];
      if (firstTab !== undefined) selectTab(firstTab.id);
      tabListRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[0]?.focus();
    }
    if (e.key === 'End') {
      e.preventDefault();
      const lastTab = TABS[TABS.length - 1];
      if (lastTab !== undefined) selectTab(lastTab.id);
      tabListRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[TABS.length - 1]?.focus();
    }
  }

  return (
    <div className={styles.view}>
      <PageHeader title="Settings" />

      {/* Tab bar */}
      <div
        ref={tabListRef}
        role="tablist"
        aria-label="Settings sections"
        className={styles.tabList}
      >
        {TABS.map((tab, index) => {
          const isSelected = tab.id === activeTab;
          return (
            <button
              key={tab.id}
              role="tab"
              id={`settings-tab-${tab.id}`}
              aria-selected={isSelected}
              aria-controls={`settings-panel-${tab.id}`}
              tabIndex={isSelected ? 0 : -1}
              className={`${styles.tab ?? ''} ${isSelected ? (styles.tabActive ?? '') : ''}`}
              onClick={() => { selectTab(tab.id); }}
              onKeyDown={(e) => { handleTabKeyDown(e, index); }}
            >
              {tab.label}
            </button>
          );
        })}
      </div>

      {/* Tab panels */}
      {TABS.map((tab) => {
        const isSelected = tab.id === activeTab;
        return (
          <div
            key={tab.id}
            role="tabpanel"
            id={`settings-panel-${tab.id}`}
            aria-labelledby={`settings-tab-${tab.id}`}
            hidden={!isSelected}
            tabIndex={0}
            className={styles.tabPanel}
          >
            {tab.id === 'accessibility' && isSelected && (
              <AccessibilityTab prefs={prefs} onChange={handlePrefChange} />
            )}
            {tab.id === 'workspace' && isSelected && (
              <WorkspaceTab prefs={prefs} onChange={handlePrefChange} />
            )}
          </div>
        );
      })}
    </div>
  );
}
