import { EventEmitter } from "events";

export type Priority = "low" | "normal" | "high";

export interface Job<T = unknown> {
  id: string;
  priority: Priority;
  runAt: Date;
  attempts: number;
  payload: T;
}

export interface SchedulerOptions {
  concurrency: number;
  maxAttempts: number;
  retryDelayMs: (attempt: number) => number;
  now?: () => Date;
}

const PRIORITY_WEIGHT: Record<Priority, number> = { low: 0, normal: 1, high: 2 };

export class JobScheduler<T> extends EventEmitter {
  private readonly queue: Job<T>[] = [];
  private readonly running = new Map<string, Promise<void>>();
  private timer: ReturnType<typeof setTimeout> | undefined;
  private stopped = false;

  constructor(
    private readonly handler: (job: Job<T>) => Promise<void>,
    private readonly options: SchedulerOptions,
  ) {
    super();
    if (options.concurrency < 1) {
      throw new RangeError(`concurrency must be at least 1, got ${options.concurrency}`);
    }
  }

  get pending(): number {
    return this.queue.length;
  }

  get active(): number {
    return this.running.size;
  }

  schedule(id: string, payload: T, priority: Priority = "normal", delayMs = 0): Job<T> {
    const now = this.now();
    const job: Job<T> = {
      id,
      priority,
      payload,
      attempts: 0,
      runAt: new Date(now.getTime() + delayMs),
    };
    this.insert(job);
    this.emit("scheduled", job);
    this.pump();
    return job;
  }

  cancel(id: string): boolean {
    const index = this.queue.findIndex((job) => job.id === id);
    if (index < 0) {
      return false;
    }
    const [removed] = this.queue.splice(index, 1);
    this.emit("cancelled", removed);
    return true;
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    await Promise.allSettled([...this.running.values()]);
  }

  private insert(job: Job<T>): void {
    // Keep the queue ordered by run time, then by priority (highest first), so the head is always next.
    let low = 0;
    let high = this.queue.length;
    while (low < high) {
      const mid = (low + high) >>> 1;
      if (this.compare(this.queue[mid], job) <= 0) {
        low = mid + 1;
      } else {
        high = mid;
      }
    }
    this.queue.splice(low, 0, job);
  }

  private compare(a: Job<T>, b: Job<T>): number {
    const byTime = a.runAt.getTime() - b.runAt.getTime();
    if (byTime !== 0) {
      return byTime;
    }
    return PRIORITY_WEIGHT[b.priority] - PRIORITY_WEIGHT[a.priority];
  }

  private pump(): void {
    if (this.stopped) {
      return;
    }
    const now = this.now().getTime();
    while (this.running.size < this.options.concurrency && this.queue.length > 0) {
      const next = this.queue[0];
      if (next.runAt.getTime() > now) {
        this.arm(next.runAt.getTime() - now);
        return;
      }
      this.queue.shift();
      this.start(next);
    }
  }

  private arm(delayMs: number): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
    }
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.pump();
    }, Math.max(0, delayMs));
  }

  private start(job: Job<T>): void {
    job.attempts += 1;
    const run = this.handler(job)
      .then(() => {
        this.emit("completed", job);
      })
      .catch((error: unknown) => {
        if (job.attempts >= this.options.maxAttempts) {
          this.emit("failed", job, error);
          return;
        }
        const delay = this.options.retryDelayMs(job.attempts);
        job.runAt = new Date(this.now().getTime() + delay);
        this.insert(job);
        this.emit("retrying", job, delay);
      })
      .finally(() => {
        this.running.delete(job.id);
        this.pump();
      });
    this.running.set(job.id, run);
  }

  private now(): Date {
    return this.options.now ? this.options.now() : new Date();
  }
}

export function exponentialBackoff(baseMs: number, capMs: number): (attempt: number) => number {
  return (attempt) => Math.min(capMs, baseMs * 2 ** (attempt - 1));
}
