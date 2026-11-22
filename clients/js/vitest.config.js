import { defineConfig } from 'vitest/config';
import { playwright } from '@vitest/browser-playwright';

// Two projects (D28):
//   node     every *.test.js except *.browser.test.js (npm test)
//   browser  *.browser.test.js in Chromium via Playwright (npm run test:browser;
//            needs 'npx playwright install chromium' once)
export default defineConfig({
  test: {
    projects: [
      {
        test: {
          name: 'node',
          environment: 'node',
          include: ['packages/*/test/**/*.test.js'],
          exclude: ['**/node_modules/**', 'packages/*/test/**/*.browser.test.js'],
        },
      },
      {
        test: {
          name: 'browser',
          include: ['packages/*/test/**/*.browser.test.js'],
          browser: {
            enabled: true,
            headless: true,
            provider: playwright(),
            instances: [{ browser: 'chromium' }],
          },
        },
      },
    ],
  },
});
