import { describe, expect, it } from 'vitest';
import * as browser from '../../src/browser.js';

describe('browser entry (Chromium)', () => {
  it('loads in a real browser', () => {
    expect(typeof browser.NexusClient).toBe('function');
    expect(typeof browser.ProxyBase).toBe('function');
    expect(typeof browser.ClientNexusBase).toBe('function');
  });

  it('supports the webSocket transport only', () => {
    expect(Object.keys(browser.NexusClient.transports)).toEqual(['webSocket']);
  });

  it('maps cancellation to AbortError', () => {
    const error = browser.cancellationError();
    expect(error).toBeInstanceOf(DOMException);
    expect(browser.isCancellationError(error)).toBe(true);
  });
});
