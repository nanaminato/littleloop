import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from '../../shared/chat-store';

interface PrivateApproval {
  id: string;
  nonce: string;
  tool: string;
  arguments: { path?: string; oldText?: string; newText?: string; command?: string };
}

@Component({
  selector: 'chat-sidebar',
  imports: [FormsModule],
  templateUrl: './chat-sidebar.html',
  styleUrl: './chat-sidebar.css',
})
export class ChatSidebar {
  readonly store = inject(ChatStore);
  readonly approvals = signal<PrivateApproval[] | null>(null);
  readonly approvalBusy = signal(false);
  readonly approvalError = signal(false);

  async refreshApprovals(item?: PrivateApproval, value?: 'accept' | 'decline') {
    this.approvalBusy.set(true);
    this.approvalError.set(false);
    const base = '/api/chat/bot/private-approvals';
    const headers = { 'X-LittleLoop-Chat': '1', 'Content-Type': 'application/json' };
    try {
      if (item) {
        const decision = await fetch(base + '/' + item.id + '/decision', {
          method: 'POST', headers, body: JSON.stringify({ nonce: item.nonce, value }),
        });
        if (!decision.ok) this.approvalError.set(true);
      }
      const response = await fetch(base, { headers });
      if (!response.ok) throw new Error('Approval request failed');
      this.approvals.set(await response.json());
    } catch {
      this.approvals.set(null);
      this.approvalError.set(true);
    } finally {
      this.approvalBusy.set(false);
    }
  }
}
