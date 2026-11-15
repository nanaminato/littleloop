import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-friends',
  imports: [FormsModule],
  templateUrl: './chat-friends.html',
  styleUrl: './chat-friends.css',
})
export class ChatFriends {
  readonly store = inject(ChatStore);
}
