import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { CDPSession, Page } from '@playwright/test';
import { VIDEO } from './config.ts';

/**
 * Records the page through the Chromium screencast: a JPEG for every frame the browser paints, stamped with its arrival
 * time. Frames arrive only when something changes, so the encoder (compose.ts) holds each frame until the next one and
 * resamples to a constant 30 fps. Unlike Playwright's built-in recorder (VP8 at a fixed, low bitrate), this keeps small
 * text sharp at 1080p, and frame times share the clock the director's timeline uses.
 */
export interface Frame { file: string; at: number }

export class Recorder {
  private session: CDPSession | null = null;
  private frames: Frame[] = [];
  private pending: Promise<void>[] = [];
  private stoppedAt = 0;
  readonly dir: string;

  constructor(dir: string) {
    this.dir = dir;
    mkdirSync(dir, { recursive: true });
  }

  async start(page: Page): Promise<void> {
    this.session = await page.context().newCDPSession(page);
    this.session.on('Page.screencastFrame', (event: { data: string; sessionId: number }) => {
      const file = join(this.dir, `${String(this.frames.length).padStart(6, '0')}.jpg`);
      // Arrival time, on the same clock as the director's step times (the frame's own timestamp is a different clock).
      this.frames.push({ file, at: Date.now() });
      writeFileSync(file, Buffer.from(event.data, 'base64'));
      this.pending.push(this.session!.send('Page.screencastFrameAck', { sessionId: event.sessionId }).then(() => undefined, () => undefined));
    });
    await this.session.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: VIDEO.width, maxHeight: VIDEO.height, everyNthFrame: 1 });
  }

  /** Wall-clock time of the first frame, the zero of the video. */
  get startedAt(): number {
    return this.frames[0]?.at ?? Date.now();
  }

  async stop(): Promise<{ frames: Frame[]; endAt: number }> {
    this.stoppedAt = Date.now();
    if (this.session) {
      await this.session.send('Page.stopScreencast').catch(() => undefined);
      await Promise.all(this.pending);
      await this.session.detach().catch(() => undefined);
    }
    return { frames: this.frames, endAt: this.stoppedAt };
  }
}
