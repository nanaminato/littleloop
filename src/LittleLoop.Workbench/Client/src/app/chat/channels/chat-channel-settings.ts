import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatStore } from '../shared/chat-store';

const rights = [
  [1, '发布帖子'], [2, '编辑帖子'], [4, '删除帖子'], [8, '置顶消息'],
  [16, '修改频道资料'], [32, '管理订阅者'], [64, '管理讨论'],
] as const;

interface AuditEntry { id: number; actor: string; action: string; detail: string; createdAt: string }
interface ChannelAdmin { name: string; rights: number }

@Component({
  selector: 'chat-channel-settings',
  imports: [FormsModule],
  template: `
    <section>
      @if (canEditProfile()) {
        <form (ngSubmit)="saveProfile()">
          <label>{{ store.t('频道名称') }}
            <input name="title" [(ngModel)]="title" maxlength="100" required>
          </label>
          <label><input name="signPosts" type="checkbox" [(ngModel)]="signPosts"> {{ store.t('显示作者署名') }}</label>
          <button type="submit" [disabled]="busy() || !title.trim()">{{ store.t('保存修改') }}</button>
        </form>
      }
      <h3>{{ store.t('频道管理员') }}</h3>
      @for (admin of admins(); track admin.name) {
        <p>
          {{ admin.name }} · {{ labels(admin.rights) }}
          @if (owner()) {
            <button type="button" class="quiet" (click)="edit(admin)">{{ store.t('编辑') }}</button>
          }
        </p>
      }
      @if (owner()) {
        <form (ngSubmit)="saveAdmin(mask)">
          <input name="admin" [(ngModel)]="username" required maxlength="32"
            [placeholder]="store.t('成员用户名')" [attr.aria-label]="store.t('成员用户名')">
          @for (right of rights; track right[0]) {
            <label><input type="checkbox" [checked]="maskHas(right[0])" (change)="toggle(right[0])">
              {{ store.t(right[1]) }}
            </label>
          }
          <button type="submit" [disabled]="busy() || !username.trim() || !mask">{{ store.t('保存修改') }}</button>
          @if (editing()) {
            <button type="button" class="quiet" [disabled]="busy()" (click)="saveAdmin(0)">{{ store.t('撤销管理员') }}</button>
          }
        </form>
      }
      @if (owner()) {
        <form (ngSubmit)="confirmTransfer()">
          <label>{{ store.t('新频道所有者的用户名') }}
            <input name="newOwner" [(ngModel)]="newOwner" maxlength="32" required>
          </label>
          @if (transferTarget(); as target) {
            <p>{{ store.t('转让后，你将成为普通订阅者。') }} @{{ target }}</p>
            <button type="button" [disabled]="busy()" (click)="transferOwnership(target)">{{ store.t('确认转让') }}</button>
            <button type="button" class="quiet" (click)="transferTarget.set(null)">{{ store.t('取消') }}</button>
          } @else {
            <button type="submit" [disabled]="busy() || !newOwner.trim()">{{ store.t('转让频道') }}</button>
          }
        </form>
      }
      @if (canRemoveSubscriber()) {
        <form (ngSubmit)="removeSubscriber()">
          <label>{{ store.t('订阅者用户名') }}
            <input name="subscriber" [(ngModel)]="subscriber" maxlength="32" required>
          </label>
          <button type="submit" [disabled]="busy() || !subscriber.trim()">{{ store.t('移除订阅者') }}</button>
        </form>
      }
      <h3>{{ store.t('讨论群') }}</h3>
      @if (group(); as linked) {
        @if (store.rooms().some(isLinked)) {
          <button type="button" class="quiet" (click)="store.select(linked)">{{ store.roomTitle(linked) }}</button>
        } @else {
          <button type="button" class="quiet" [disabled]="busy() || joinRequested()" (click)="requestJoin()">
            {{ store.t(joinRequested() ? '已提交入群申请' : '申请加入讨论群') }}
          </button>
        }
      }
      @if (canDiscuss()) {
        <form (ngSubmit)="saveGroup()">
          <label>
            {{ store.t('关联讨论群') }}
            <select name="group" [(ngModel)]="groupChoice">
              <option value="">{{ store.t('无') }}</option>
              @for (room of store.rooms(); track room.id) {
                @if (room.type === 'group' && room.canManage) { <option [value]="room.id">{{ room.title }}</option> }
              }
            </select>
          </label>
          <p>{{ store.t('关联后，新帖子及其附件会同步给讨论群成员。') }}</p>
          <button type="submit" [disabled]="busy() || groupChoice === (group() ?? '')">{{ store.t('保存修改') }}</button>
        </form>
      }
      @if (owner()) {
        <button type="button" class="quiet" [disabled]="busy()" (click)="loadAudit()">{{ store.t('管理记录') }}</button>
        @if (audit(); as entries) {
          @for (entry of entries; track entry.id) {
            <p><strong>{{ entry.actor }}</strong> · {{ store.t(auditLabel(entry.action)) }} · {{ entry.detail }}
              <small>{{ store.dateLabel(entry.createdAt) }} {{ store.dateLabel(entry.createdAt, true) }}</small></p>
          } @empty { <p>{{ store.t('暂无管理记录') }}</p> }
          @if (moreAudit()) {
            <button type="button" class="quiet" [disabled]="busy()" (click)="loadAudit(entries.at(-1)?.id)">{{ store.t('加载更早消息') }}</button>
          }
        }
      }
      @if (owner()) {
        @if (deleting()) {
          <p>{{ store.t('删除频道后，订阅者将无法访问频道。讨论群保留，已有评论将关闭。') }}</p>
          <button type="button" [disabled]="busy()" (click)="deleteChannel()">{{ store.t('确认删除频道') }}</button>
          <button type="button" class="quiet" [disabled]="busy()" (click)="deleting.set(false)">{{ store.t('取消') }}</button>
        } @else {
          <button type="button" class="quiet" [disabled]="busy()" (click)="deleting.set(true)">{{ store.t('删除频道') }}</button>
        }
      }
      @if (error()) { <p role="alert">{{ store.t(error()) }}</p> }
    </section>
  `,
  styles: [`section,form { display:grid; gap:.45rem; } h3,p { margin:0; } label { display:block; }`],
})
export class ChatChannelSettings {
  readonly store = inject(ChatStore);
  readonly channel = input.required<string>();
  readonly owner = input(false);
  readonly rights = rights;
  readonly joinRequested = signal(false);

  async requestJoin() {
    const channel = this.channel();
    if (this.busy() || this.joinRequested()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await fetch('/api/chat/channels/' + channel + '/discussion/join', {
        method: 'POST', headers: { 'X-LittleLoop-Chat': '1' },
      });
      if (!response.ok) throw new Error();
      if (this.channel() === channel) this.joinRequested.set(true);
    } catch {
      if (this.channel() === channel) this.error.set('无法申请加入讨论群。');
    } finally {
      this.busy.set(false);
    }
  }

  readonly audit = signal<AuditEntry[] | null>(null);
  readonly moreAudit = signal(false);

  auditLabel(action: string) {
    const labels: Record<string, string> = {
      ownership_transferred: '转让频道', profile_changed: '修改频道资料', subscriber_removed: '移除订阅者',
      administrator_granted: '授予管理员权限', administrator_revoked: '撤销管理员',
      post_published: '发布帖子', post_edited: '编辑帖子', post_deleted: '删除帖子',
      post_pinned: '置顶消息', post_unpinned: '取消置顶消息',
      comments_opened: '开启评论', comments_closed: '关闭评论',
      discussion_linked: '关联讨论群', discussion_unlinked: '解除关联',
    };
    return labels[action] ?? action;
  }

  async loadAudit(before?: number) {
    if (this.busy()) return;
    const channel = this.channel();
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await fetch('/api/chat/channels/' + channel + '/audit' + (before ? '?before=' + before : ''));
      if (!response.ok) throw new Error();
      const entries = await response.json() as AuditEntry[];
      if (this.channel() !== channel) return;
      this.audit.set(before ? [...(this.audit() ?? []), ...entries] : entries);
      this.moreAudit.set(entries.length === 50);
    } catch {
      if (this.channel() === channel) this.error.set('无法加载管理记录。');
    } finally {
      this.busy.set(false);
    }
  }

  readonly admins = signal<ChannelAdmin[]>([]);
  readonly group = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly editing = signal('');
  readonly isLinked = (room: { id: string }) => room.id === this.group();
  username = '';
  subscriber = '';
  newOwner = '';
  readonly transferTarget = signal<string | null>(null);
  readonly deleting = signal(false);
  title = '';
  signPosts = false;
  groupChoice = '';
  mask = 0;

  constructor() {
    effect(() => {
      const channel = this.channel();
      const room = this.store.currentRoom();
      if (room?.id === channel) { this.title = room.title; this.signPosts = !!room.signPosts; }
    });
    effect(() => {
      const channel = this.channel();
      untracked(() => {
        this.username = '';
        this.subscriber = '';
        this.newOwner = '';
        this.transferTarget.set(null);
        this.deleting.set(false);
        this.mask = 0;
        this.editing.set('');
        this.joinRequested.set(false);
        this.audit.set(null);
        this.moreAudit.set(false);
        this.admins.set([]);
        this.group.set(null);
        this.groupChoice = '';
        void this.refresh(channel);
      });
    });
  }

  canEditProfile() {
    return ((this.store.currentRoom()?.channelRights ?? 0) & 16) !== 0;
  }

  canRemoveSubscriber() {
    return ((this.store.currentRoom()?.channelRights ?? 0) & 32) !== 0;
  }

  async removeSubscriber() {
    const target = this.subscriber.trim();
    if (!target || this.busy()) return;
    const channel = this.channel();
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await fetch('/api/chat/channels/' + channel + '/subscribers/' + encodeURIComponent(target), {
        method: 'DELETE', headers: { 'X-LittleLoop-Chat': '1' },
      });
      if (!response.ok) throw new Error();
      if (this.channel() === channel) this.subscriber = '';
      await this.refresh(channel);
    } catch {
      if (this.channel() === channel) this.error.set('无法移除订阅者，请检查权限和成员资格。');
    } finally {
      this.busy.set(false);
    }
  }

  canDiscuss() {
    return this.owner() || this.admins().some(admin => admin.name === this.store.user() && (admin.rights & 64) !== 0);
  }

  labels(mask: number) {
    return rights.filter(([flag]) => mask & flag).map(([, label]) => this.store.t(label)).join(' · ');
  }

  maskHas(flag: number) { return (this.mask & flag) !== 0; }
  toggle(flag: number) { this.mask ^= flag; }

  edit(admin: ChannelAdmin) {
    this.editing.set(admin.name);
    this.username = admin.name;
    this.mask = admin.rights;
  }

  private async refresh(channel: string) {
    try {
      const base = '/api/chat/channels/' + channel;
      const [admins, link] = await Promise.all([fetch(base + '/admins'), fetch(base + '/discussion')]);
      if (!admins.ok || !link.ok) throw new Error();
      const [list, discussion] = await Promise.all([admins.json() as Promise<ChannelAdmin[]>, link.json() as Promise<{groupId: string | null}>]);
      if (this.channel() !== channel) return;
      this.admins.set(list);
      this.group.set(discussion.groupId);
      this.groupChoice = discussion.groupId ?? '';
    } catch {
      if (this.channel() === channel) this.error.set('无法加载频道设置。');
    }
  }

  async saveAdmin(mask: number) {
    const name = (mask === 0 ? this.editing() : this.username).trim();
    if (!name) return;
    await this.save('/admins/' + encodeURIComponent(name), { rights: mask });
  }

  confirmTransfer() {
    const target = this.newOwner.trim();
    if (target) this.transferTarget.set(target);
  }

  async transferOwnership(username: string) {
    await this.save('/ownership', { username });
    if (!this.error()) {
      this.transferTarget.set(null);
      this.newOwner = '';
    }
  }

  async deleteChannel() {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      await this.store.deleteChannel(this.channel());
    } catch {
      this.error.set('无法删除频道。');
    } finally {
      this.busy.set(false);
    }
  }

  async saveProfile() {
    await this.save('/profile', { title: this.title.trim(), signPosts: this.signPosts });
  }

  async saveGroup() {
    await this.save('/discussion', { groupId: this.groupChoice || null });
  }

  private async save(path: string, payload: object) {
    if (this.busy()) return;
    const channel = this.channel();
    this.busy.set(true);
    this.error.set('');
    try {
      const response = await fetch('/api/chat/channels/' + channel + path, {
        method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' }, body: JSON.stringify(payload),
      });
      if (!response.ok) throw new Error();
      this.editing.set('');
      await this.refresh(channel);
    } catch {
      if (this.channel() === channel) this.error.set('无法保存频道设置，请检查权限和成员资格。');
    } finally {
      this.busy.set(false);
    }
  }
}
