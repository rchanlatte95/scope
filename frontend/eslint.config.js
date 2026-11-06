// eslint.config.js — Scope frontend ESLint flat config (T-2026-06-14-14)
//
// strictTypeChecked + stylisticTypeChecked via typescript-eslint, plus
// react-hooks + react-refresh. Type-aware via projectService:true.
// --max-warnings 0 enforced in the lint script (zero-warning policy).
//
// Applies to ALL files, with no per-directory relaxation.

import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';

// eslint-disable-next-line @typescript-eslint/no-deprecated
export default tseslint.config(
  // ------------------------------------------------------------------
  // Ignore generated / build output directories.
  // ------------------------------------------------------------------
  {
    ignores: [
      'dist/**',
      'node_modules/**',
    ],
  },

  // ------------------------------------------------------------------
  // Base: typescript-eslint strict + stylistic (type-aware).
  // Applies to all TS/TSX files.
  // ------------------------------------------------------------------
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,

  // ------------------------------------------------------------------
  // Language options + parser options for type-aware linting.
  // projectService discovers tsconfigs automatically.
  // allowDefaultProject covers this config file, which is outside tsconfig.json.
  // ------------------------------------------------------------------
  {
    languageOptions: {
      parserOptions: {
        projectService: {
          allowDefaultProject: [
            'eslint.config.js',
          ],
          defaultProject: './tsconfig.node.json',
        },
        tsconfigRootDir: import.meta.dirname,
      },
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
  },

  // ------------------------------------------------------------------
  // React-hooks + react-refresh rules for TSX files.
  // ------------------------------------------------------------------
  {
    files: ['**/*.tsx'],
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': [
        'warn',
        { allowConstantExport: true },
      ],
    },
  },

  // ------------------------------------------------------------------
  // Emphasis rules: these are the correctness-critical rules explicitly
  // called out in the task. Most are already on via strictTypeChecked;
  // listed here explicitly so the intent is self-documenting.
  // ------------------------------------------------------------------
  {
    rules: {
      // Floating promises → correctness bug (async fire-and-forget).
      '@typescript-eslint/no-floating-promises': 'error',
      // Misused promises (e.g. if (asyncFn()) {}) → correctness bug.
      '@typescript-eslint/no-misused-promises': 'error',
      // Unnecessary condition checks that are always truthy/falsy.
      '@typescript-eslint/no-unnecessary-condition': 'error',
      // Strict boolean expressions prevent accidental 0/"" truthiness.
      '@typescript-eslint/strict-boolean-expressions': 'error',
      // Non-null assertions bypass the type system.
      '@typescript-eslint/no-non-null-assertion': 'error',
      // Explicit any defeats the purpose of TypeScript.
      '@typescript-eslint/no-explicit-any': 'error',
      // Switch exhaustiveness — all union arms must be handled.
      '@typescript-eslint/switch-exhaustiveness-check': 'error',
    },
  },
);
