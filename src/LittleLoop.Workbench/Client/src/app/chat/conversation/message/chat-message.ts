import { Component, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatBotCard } from '../bot-card/chat-bot-card';
import { ChatMessage, DiscussionSummary } from '../../shared/chat-model';
import { ChatComments } from '../../channels/chat-comments';
import { ChatStore } from '../../shared/chat-store';

@Component({
  selector: 'chat-message',
  imports: [FormsModule, ChatBotCard, ChatComments],
  templateUrl: './chat-message.html',
  styleUrl: './chat-message.css',
})
export class ChatMessageView {
  readonly store = inject(ChatStore);
  readonly message = input.required<ChatMessage>();
  readonly discussion = input<DiscussionSummary>();
  readonly previous = input<ChatMessage | null>(null);

  readonly channel = computed(() => this.store.currentRoom()?.type === 'channel' ? this.store.currentRoom() : undefined);
  readonly mine = computed(() => !this.channel() && this.message().sender === this.store.user());
  canChange(flag: number) {
    const room = this.store.currentRoom();
    return !this.message().card && (room?.type === 'channel'
      ? ((room.channelRights ?? 0) & flag) !== 0 : this.mine());
  }

  canPin() {
    const room = this.store.currentRoom();
    return room?.type === 'channel' ? ((room.channelRights ?? 0) & 8) !== 0 : !!room?.canPost;
  }

  readonly continued = computed(() => {
    const previous = this.previous();
    return !!previous && (this.channel() || previous.sender === this.message().sender) &&
      this.store.day(previous.sentAt) === this.store.day(this.message().sentAt);
  });
  readonly senderName = computed(() => this.channel()?.title ?? this.message().sender);
}
