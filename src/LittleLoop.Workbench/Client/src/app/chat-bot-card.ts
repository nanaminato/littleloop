import { Component, input, OnDestroy, OnInit, signal } from '@angular/core';
import { translate } from './chat-language';

export interface SourceCitation { path: string; start: number; end: number; sha256: string; text: string }
export interface MessageCard { kind: 'citation' | 'question' | 'task'; source?: SourceCitation; taskId?: string }
interface Approval { nonce: string; method: string; parameters: unknown }
interface AgentExecution {
  approval?: Approval;
  changes: Record<string, string>;
  receipts: { id: string; exitCode: number; standardOutput: string; standardError: string }[];
  turns: { status: string; report: string; issues: string[] }[];
}
interface Work { id: string; status: string; error?: string; canApprove: boolean; provider: string; output?: string; execution?: AgentExecution }

@Component({
  selector: 'chat-bot-card',
  templateUrl: './chat-bot-card.html',
  styleUrl: './chat-bot-card.css',
})
export class ChatBotCard implements OnInit, OnDestroy {
  readonly card = input.required<MessageCard>();
  readonly english = input(false);
  readonly work = signal<Work | null>(null);
  readonly error = signal('');
  readonly busy = signal(false);
  private timer?: ReturnType<typeof setInterval>;
  private loading = false;

  ngOnInit() {
    if (!this.card().taskId) return;
    void this.refresh();
    this.timer = setInterval(() => void this.refresh(), 1600);
  }
  ngOnDestroy() { clearInterval(this.timer); }

  private async refresh() {
    if (this.loading) return;
    this.loading = true;
    try {
      const response = await fetch('/api/chat/bot/work/' + this.card().taskId);
      if (!response.ok) {
        if (response.status === 401 || response.status === 404) clearInterval(this.timer);
        return;
      }
      const work: Work = await response.json();
      this.work.set(work);
      if (!['queued', 'running'].includes(work.status)) clearInterval(this.timer);
    } catch {
      // The next request after reconnection will fetch the durable job.
    } finally { this.loading = false; }
  }

  async decide(nonce: string, value: 'accept' | 'decline') {
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await fetch('/api/chat/bot/work/' + this.card().taskId + '/decision', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
        body: JSON.stringify({ nonce, value }),
      });
      if (!response.ok) throw new Error(this.t(response.status === 409 ? '此审批已处理或已失效。' : '审批提交失败。'));
      this.work.update(work => work?.execution ? { ...work, execution: { ...work.execution, approval: undefined } } : work);
    } catch (error) { this.error.set(error instanceof Error ? error.message : String(error)); }
    finally { await this.refresh(); this.busy.set(false); }
  }

  pairs(changes: Record<string, string>) { return Object.entries(changes); }
  format(value: unknown) { return JSON.stringify(value, null, 2); }
  t(value: string) { return translate(value, this.english()); }
  label(status: string) {
    const names: Record<string, string> = { queued: '排队中', running: '执行中', completed: '已完成', refused: '核心拒绝',
      failed: '失败', disabled: '机器人已停用', 'needs-reconciliation': '待核对', 'needs-revision': '验收未通过', 'stale-source': '源码已变更' };
    return this.t(names[status] ?? status);
  }
}
