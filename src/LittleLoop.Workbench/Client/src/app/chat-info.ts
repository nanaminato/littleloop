import { Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Room } from './chat-model';
import { ChatStore } from './chat-store';

@Component({
  selector: 'chat-info',
  imports: [FormsModule],
  templateUrl: './chat-info.html',
  styleUrl: './chat-info.css',
})
export class ChatInfo {
  readonly store = inject(ChatStore);
  readonly room = input.required<Room>();
}
