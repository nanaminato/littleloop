import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatMessage } from './chat-model';
import { ChatStore } from './chat-store';
import { ChatMessageView } from './chat-message';

@Component({
  selector: 'chat-thread',
  imports: [FormsModule, ChatMessageView],
  templateUrl: './chat-thread.html',
  styleUrl: './chat-thread.css',
})
export class ChatThread {
  readonly store = inject(ChatStore);

  previous(index: number): ChatMessage | null {
    return index > 0 ? this.store.messages()[index - 1] ?? null : null;
  }
}
