import { Component, inject } from '@angular/core';
import { ChatStore } from '../../shared/chat-store';

@Component({
  selector: 'chat-drawer',
  templateUrl: './chat-drawer.html',
  styleUrl: './chat-drawer.css',
})
export class ChatDrawer {
  readonly store = inject(ChatStore);
}
