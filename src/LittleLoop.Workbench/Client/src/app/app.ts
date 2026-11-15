import { Component, OnDestroy, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

interface Citation { path: string; start: number; end: number; sha256: string; text: string }
interface Message { id: string; role: string; text: string; source?: Citation; replyTo?: string }
interface Job { id: string; messageId: string; goal: string; status: string; error?: string }
interface Conversation { messages: Message[]; tasks: Job[] }
interface Execution {
  status: string; threadId?: string;
  approval?: { nonce: string; method: string; parameters: unknown };
  changes: Record<string, string>;
  receipts: { id: string; exitCode: number; standardOutput: string; standardError: string }[];
  turns: { status: string; report: string; issues: string[] }[];
}

@Component({
  selector: 'app-root',
  imports: [FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnDestroy {
  readonly state = signal<Conversation>({ messages: [], tasks: [] });
  readonly paths = signal<string[]>([]);
  readonly execution = signal<Execution | null>(null);
  readonly error = signal('');
  readonly busy = signal(false);
  readonly selected = signal('');
  readonly preview = signal<Citation | null>(null);
  readonly connected = signal(false);

  token = sessionStorage.getItem('workbench-token') ?? '';
  path = '';
  start = 1;
  end = 20;
  question = '';
  goal = '';
  anchor?: Message;
  private readonly timer = setInterval(() => {
    if (this.connected()) void this.refresh().catch(error => this.error.set(error.message));
  }, 1500);

  constructor() {
    if (this.token) void this.connect();
  }

  ngOnDestroy() { clearInterval(this.timer); }

  async connect() {
    await this.action(async () => {
      this.paths.set(await this.request<string[]>('/api/sources'));
      sessionStorage.setItem('workbench-token', this.token);
      this.connected.set(true);
      await this.refresh();
    });
  }

  async refresh() {
    this.state.set(await this.request<Conversation>('/api/conversation'));
    const id = this.selected();
    if (id) {
      const details = await this.request<{ execution: Execution | null }>('/api/tasks/' + id);
      if (this.selected() === id) this.execution.set(details.execution);
    }
  }

  async select(id: string) {
    this.selected.set(id);
    this.execution.set(null);
    await this.refresh().catch(error => this.error.set(error.message));
  }

  async inspect() {
    await this.action(async () => {
      const query = new URLSearchParams({ path: this.path, start: String(this.start), end: String(this.end) });
      this.preview.set(await this.request<Citation>('/api/source?' + query));
    });
  }

  async ask() {
    await this.action(async () => {
      await this.request('/api/ask', { text: this.question, path: this.path, start: this.start, end: this.end });
      this.question = '';
    });
  }

  async schedule() {
    const anchor = this.anchor;
    if (!anchor) return;
    await this.action(async () => {
      const job = await this.request<Job>('/api/tasks', { messageId: anchor.id, goal: this.goal });
      this.goal = '';
      this.anchor = undefined;
      await this.select(job.id);
    });
  }

  async decide(nonce: string, value: 'accept' | 'decline') {
    await this.action(() => this.request('/api/tasks/' + this.selected() + '/decision', { nonce, value }));
  }

  async action(run: () => Promise<unknown>) {
    this.busy.set(true);
    this.error.set('');
    try { await run(); }
    catch (error) { this.error.set(error instanceof Error ? error.message : String(error)); }
    finally {
      if (this.connected()) await this.refresh().catch(error => this.error.set(error.message));
      this.busy.set(false);
    }
  }

  entries(value: Record<string, string>) { return Object.entries(value); }
  format(value: unknown) { return JSON.stringify(value, null, 2); }
  status(value: string) {
    const labels: Record<string, string> = {
      queued: '排队中', running: '执行中', completed: '已完成', refused: '核心拒绝',
      'needs-revision': '验收未通过', 'needs-reconciliation': '待核对', 'stale-source': '引用已过期',
    };
    return labels[value] ?? value;
  }

  private async request<T>(url: string, body?: object): Promise<T> {
    const headers: Record<string, string> = { Authorization: 'Bearer ' + this.token };
    if (body) headers['Content-Type'] = 'application/json';
    const response = await fetch(url, { method: body ? 'POST' : 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    if (!response.ok) {
      const raw = await response.text();
      let detail = raw;
      if (response.headers.get('content-type')?.includes('json') && raw) {
        const parsed = JSON.parse(raw);
        detail = typeof parsed === 'string' ? parsed : parsed.detail ?? parsed.title;
      }
      throw new Error(response.status === 401 ? '访问令牌无效。' : detail || '请求失败 (' + response.status + ')');
    }
    return response.status === 204 ? undefined as T : response.json();
  }
}
