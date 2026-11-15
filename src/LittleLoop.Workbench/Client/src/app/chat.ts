import { Component, HostListener, inject } from '@angular/core';
import { ChatAuth } from './chat-auth';
import { ChatCreateSpace } from './chat-create-space';
import { ChatDrawer } from './chat-drawer';
import { ChatFriends } from './chat-friends';
import { ChatInfo } from './chat-info';
import { ChatSettings } from './chat-settings';
import { ChatSidebar } from './chat-sidebar';
import { ChatStore } from './chat-store';
import { ChatThread } from './chat-thread';

@Component({
  selector: 'app-root',
  imports: [ChatAuth, ChatCreateSpace, ChatDrawer, ChatFriends, ChatInfo, ChatSettings, ChatSidebar, ChatThread],
  templateUrl: './chat.html',
  styleUrl: './chat.css',
})
export class Chat {
  readonly store = inject(ChatStore);

  @HostListener('window:resize')
  resize() { this.store.closeMessageMenus(); }

  @HostListener('document:click', ['$event'])
  click(event: Event) { this.store.dismissMessageMenu(event); }

  @HostListener('document:keydown.escape')
  escape() { this.store.escape(); }
}
