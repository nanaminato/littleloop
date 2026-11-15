import { Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatMessage, DiscussionSummary } from '../../shared/chat-model';
import { ChatStore } from '../../shared/chat-store';
import { ChatMessageView } from '../message/chat-message';

@Component({
  selector: 'chat-thread',
  imports: [FormsModule, ChatMessageView],
  templateUrl: './chat-thread.html',
  styleUrl: './chat-thread.css',
})
export class ChatThread {
  readonly store = inject(ChatStore);

  readonly discussions = signal<Record<number, DiscussionSummary>>({});

  constructor() {
    effect(onCleanup => {
      const room = this.store.currentRoom();
      const posts = this.store.messages().filter(post => post.roomId === room?.id && !post.deleted)
        .slice(-100).map(post => post.sequence);
      if (room?.type !== 'channel' || !posts.length) {
        this.discussions.set({});
        return;
      }
      const scope = new AbortController();
      let running = false;
      const refresh = async () => {
        if (running || document.hidden) return;
        running = true;
        try {
          const response = await fetch(`/api/chat/channels/${room.id}/comments?posts=${posts.join(',')}`,
            { signal: scope.signal });
          if (!response.ok) return;
          const items = await response.json() as DiscussionSummary[];
          if (!scope.signal.aborted) this.discussions.set(Object.fromEntries(items.map(item => [item.post, item])));
        } catch { /* The next refresh can recover from a temporary network error. */ }
        finally { running = false; }
      };
      this.discussions.set({});
      void refresh();
      const timer = setInterval(() => void refresh(), 5000);
      onCleanup(() => { clearInterval(timer); scope.abort(); });
    });
  }

  previous(index: number): ChatMessage | null {
    return index > 0 ? this.store.messages()[index - 1] ?? null : null;
  }
}
