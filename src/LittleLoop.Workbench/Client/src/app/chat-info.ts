import { Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Room } from './chat-model';
import { ChatChannelSettings } from './chat-channel-settings';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-info',
  imports: [FormsModule, ChatChannelSettings],
  templateUrl: './chat-info.html',
  styleUrl: './chat-info.css',
})
export class ChatInfo {
  readonly store = inject(ChatStore);
  readonly room = input.required<Room>();

  readonly joinRequests = signal<string[]>([]);
  readonly decidingJoin = signal<string | null>(null);
  readonly joinError = signal(false);

  constructor() {
    effect(onCleanup => {
      const room = this.room();
      this.joinRequests.set([]);
      this.joinError.set(false);
      if (room.type !== 'group' || !room.canManage) return;
      const scope = new AbortController();
      void this.refreshRequests(room.id, scope.signal);
      const timer = setInterval(() => void this.refreshRequests(room.id, scope.signal), 5000);
      onCleanup(() => { clearInterval(timer); scope.abort(); });
    });
  }

  private async refreshRequests(room: string, signal?: AbortSignal) {
    try {
      const response = await fetch('/api/chat/rooms/' + room + '/discussion-requests', { signal });
      if (!response.ok) return;
      const names = await response.json() as string[];
      if (!signal?.aborted && this.room().id === room) this.joinRequests.set(names);
    } catch { /* A later refresh can recover. */ }
  }

  async decideJoin(username: string, accepted: boolean) {
    if (this.decidingJoin()) return;
    const room = this.room().id;
    this.decidingJoin.set(username);
    this.joinError.set(false);
    try {
      const response = await fetch('/api/chat/rooms/' + room + '/members', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
        body: JSON.stringify({ username, operation: accepted ? 'approve_join' : 'decline_join' }),
      });
      if (!response.ok) throw new Error();
      await this.refreshRequests(room);
    } catch {
      if (this.room().id === room) this.joinError.set(true);
    } finally {
      this.decidingJoin.set(null);
    }
  }

  isChannelOwner() {
    return this.store.detailMembers().some(member => member.owner && member.name === this.store.user());
  }
}
