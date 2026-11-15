import { Component, ElementRef, HostListener, afterNextRender, inject, input, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from '../shared/chat-store';

type SpaceKind = 'channel' | 'topic';

@Component({
  selector: 'chat-create-space',
  imports: [FormsModule],
  templateUrl: './chat-create-space.html',
  styleUrl: './chat-create-space.css',
})
export class ChatCreateSpace {
  readonly kind = input.required<SpaceKind>();
  readonly store = inject(ChatStore);
  readonly dialog = viewChild<ElementRef<HTMLElement>>('dialog');
  readonly titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');
  title = '';
  private readonly trigger = typeof document === 'undefined' ? null : document.activeElement as HTMLElement | null;

  constructor() {
    afterNextRender(() => this.titleInput()?.nativeElement.focus());
  }

  heading() { return this.store.t(this.kind() === 'channel' ? '新建频道' : '新建话题'); }
  fieldLabel() { return this.store.t(this.kind() === 'channel' ? '频道名称' : '话题名称'); }
  placeholder() { return this.store.t(this.kind() === 'channel' ? '例如：产品公告' : '例如：发布讨论'); }
  submitLabel() { return this.store.t(this.kind() === 'channel' ? '创建频道' : '创建话题'); }
  metaNote() { return this.store.t(this.kind() === 'channel' ? '名称将显示在会话列表和频道目录中。' : '名称将显示在群话题列表中。'); }
  icon() { return this.kind() === 'channel' ? '◎' : '◇'; }

  description() {
    if (this.kind() === 'channel') return this.store.t('订阅者接收广播，只有发布者可以发消息。');
    const room = this.store.currentRoom()?.title;
    return room
      ? this.store.t('在 {room} 中创建话题。', { room })
      : this.store.t('话题用于在群内展开一条独立讨论。');
  }

  canSubmit() { return this.title.trim().length > 0 && !this.store.busy(); }

  close() {
    if (this.store.busy()) return;
    this.store.closeCreateDialogs();
    this.store.error.set('');
    const target = this.trigger?.isConnected && this.trigger.offsetParent !== null
      ? this.trigger
      : document.querySelector<HTMLElement>('.menu-button');
    target?.focus();
  }

  async submit() {
    if (!this.canSubmit()) return;
    const title = this.title;
    if (this.kind() === 'channel') await this.store.createChannel(title);
    else await this.store.createTopic(title);
  }

  @HostListener('document:keydown.escape')
  escape() { this.close(); }

  @HostListener('document:keydown', ['$event'])
  trapFocus(event: KeyboardEvent) {
    if (event.key !== 'Tab') return;
    const root = this.dialog()?.nativeElement;
    if (!root) return;
    const focusable = [...root.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), textarea:not([disabled]), select:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
    )].filter(element => element.offsetParent !== null);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
}
