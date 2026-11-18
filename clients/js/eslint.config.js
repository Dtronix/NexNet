import js from '@eslint/js';
import globals from 'globals';

export default [
  { ignores: ['**/node_modules/**', '**/dist/**'] },
  js.configs.recommended,
  {
    files: ['**/*.js'],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: 'module',
      // Runtime sources get browser globals only; Node globals are limited to the Node entry, Node transports,
      // tests and config files (below).
      globals: { ...globals.browser },
    },
    rules: {
      // Contract skeletons and JSDoc'd interfaces name parameters they do not use yet.
      'no-unused-vars': ['error', { args: 'none' }],
      eqeqeq: ['error', 'always'],
      'prefer-const': 'error',
      'no-var': 'error',
    },
  },
  {
    files: [
      'packages/*/src/node.js',
      'packages/*/src/contract/node.js',
      'packages/*/src/**/node/**/*.js',
      'packages/*/test/**/*.js',
      '*.config.js',
    ],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
  },
];
