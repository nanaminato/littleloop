import { Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from '../shared/chat-store';

@Component({
  selector: 'chat-friends',
  imports: [FormsModule],
  templateUrl: './chat-friends.html',
  styleUrl: './chat-friends.css',
})
export class ChatFriends {
  readonly store = inject(ChatStore);
  readonly requests = computed(() => this.store.botAccess()?.filter(entry => entry.state === 'pending') ?? []);
  readonly access = computed(() => this.store.botAccess()?.filter(entry => entry.allowed ||
    entry.state === 'automatic' || entry.state === 'manual') ?? []);
}
