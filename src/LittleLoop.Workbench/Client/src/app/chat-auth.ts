import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-auth',
  imports: [FormsModule],
  templateUrl: './chat-auth.html',
  styleUrl: './chat-auth.css',
})
export class ChatAuth {
  readonly store = inject(ChatStore);
}
