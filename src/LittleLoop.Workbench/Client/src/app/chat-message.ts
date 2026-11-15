import { Component, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatBotCard } from './chat-bot-card';
import { ChatMessage } from './chat-model';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-message',
  imports: [FormsModule, ChatBotCard],
  templateUrl: './chat-message.html',
  styleUrl: './chat-message.css',
})
export class ChatMessageView {
  readonly store = inject(ChatStore);
  readonly message = input.required<ChatMessage>();
  readonly previous = input<ChatMessage | null>(null);

  readonly mine = computed(() => this.message().sender === this.store.user());
  readonly continued = computed(() => {
    const previous = this.previous();
    return !!previous && previous.sender === this.message().sender &&
      this.store.day(previous.sentAt) === this.store.day(this.message().sentAt);
  });
  readonly senderName = computed(() => this.mine()
    ? this.store.displayedName()
    : this.message().sender);
}
