import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-sidebar',
  imports: [FormsModule],
  templateUrl: './chat-sidebar.html',
  styleUrl: './chat-sidebar.css',
})
export class ChatSidebar {
  readonly store = inject(ChatStore);
}
