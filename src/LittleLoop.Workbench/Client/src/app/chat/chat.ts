import { Component, HostListener, inject } from '@angular/core';
import { ChatAuth } from './auth/chat-auth';
import { ChatCreateSpace } from './spaces/chat-create-space';
import { ChatDrawer } from './navigation/drawer/chat-drawer';
import { ChatFriends } from './friends/chat-friends';
import { ChatInfo } from './conversation/details/chat-info';
import { ChatSettings } from './settings/chat-settings';
import { ChatSidebar } from './navigation/sidebar/chat-sidebar';
import { ChatStore } from './shared/chat-store';
import { ChatThread } from './conversation/thread/chat-thread';

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
