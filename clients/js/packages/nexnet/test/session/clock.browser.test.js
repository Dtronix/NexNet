import { describe, expect, it, vi } from 'vitest';
import { startIntervalTimer, systemClock } from '../../src/session/clock.js';

describe('browser keepalive timer', () => {
  it('runs in a dedicated Worker and stops cleanly', async () => {
    const created = vi.spyOn(URL, 'createObjectURL');
    let ticks = 0;
    const timer = startIntervalTimer(systemClock, 10, () => ticks++);
    try {
      expect(created).toHaveBeenCalledTimes(1); // the Worker path, not the setInterval fallback
      await new Promise((resolve) => setTimeout(resolve, 200));
      expect(ticks).toBeGreaterThan(2);
    } finally {
      timer.stop();
      created.mockRestore();
    }
    const after = ticks;
    await new Promise((resolve) => setTimeout(resolve, 60));
    expect(ticks).toBe(after);
  });
});
