import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Attachment, ChatMessage, DiscussionSummary } from './chat-model';
import { ChatStore } from './chat-store';

interface DiscussionThread {
  groupId: string;
  rootSequence: number;
  closed: boolean;
  canComment: boolean;
  following: boolean;
  count: number;
  readSequence: number;
  unread: number;
  messages: ChatMessage[];
}

@Component({
  selector: 'chat-comments',
  imports: [FormsModule],
  template: `
    <button class="quiet" type="button" [attr.aria-expanded]="expanded()" (click)="toggle()">
      {{ expanded() ? store.t('收起评论') : store.t('查看评论') }}
    </button>
    @if (!expanded() && summary(); as info) {
      <small class="comment-summary">{{ info.count }} {{ store.t('条评论') }}
        @if (info.unread > 0) { · {{ info.unread }} {{ store.t('未读') }} }
      </small>
    }
    @if (expanded()) {
      <section [attr.aria-label]="store.t('帖子评论')">
        @if (thread(); as discussion) {
          <strong>{{ store.t('帖子评论') }} · {{ discussion.count }}</strong>
          @if (!fromGroup()) {
          <button type="button" class="quiet" [disabled]="changingFollow()" [attr.aria-pressed]="discussion.following"
            (click)="setFollowing(!discussion.following)">
            {{ store.t(discussion.following ? '取消关注讨论' : '关注讨论') }}
          </button>
          }
          @if (hasOlder()) {
            <button type="button" class="quiet" [disabled]="loadingOlder()" (click)="loadOlder()">
              {{ store.t('加载更早消息') }}
            </button>
          }
          @for (comment of discussion.messages; track comment.sequence) {
            <article>
              <strong>{{ comment.sender }}</strong>
              @if (!comment.deleted && comment.replyTo && comment.replyTo !== discussion.rootSequence) {
                @if (quotedComment(discussion, comment.replyTo); as quoted) {
                  <blockquote>{{ quoted.sender }} · {{ quoted.deleted ? store.t('消息已删除') : (quoted.text || quoted.attachment?.name || store.t('消息')) }}</blockquote>
                } @else {
                  <blockquote>{{ store.t('回复') }} #{{ comment.replyTo }}</blockquote>
                }
              }
              <p>{{ comment.deleted ? store.t('消息已删除') : comment.text }}</p>
              @if (!comment.deleted && comment.attachment; as file) {
                <a [href]="store.attachmentUrl(comment) + '?download=true'">{{ file.name }}</a>
              }
              @if (discussion.canComment && !comment.deleted) {
                <button type="button" class="quiet" (click)="reply.set(comment)">{{ store.t('回复') }}</button>
              }
              @if (!comment.deleted && !comment.card && comment.sender === store.user()) {
              @if (editTarget() === comment.sequence) {
                <form (ngSubmit)="editComment(discussion, comment)">
                  <textarea name="editComment" [(ngModel)]="editedText" maxlength="4000"
                    [attr.aria-label]="store.t('编辑评论')"></textarea>
                  <button type="submit" class="primary" [disabled]="savingEdit() || (!editedText.trim() && !comment.attachment)">
                    {{ store.t('保存修改') }}
                  </button>
                  <button type="button" class="quiet" (click)="editTarget.set(null)">{{ store.t('取消') }}</button>
                </form>
              } @else {
                <button type="button" class="quiet" (click)="beginEdit(comment)">{{ store.t('编辑') }}</button>
              }
            }
            @if (!comment.deleted && canDelete(discussion, comment)) {
              @if (deleting() === comment.sequence) {
                <button type="button" class="quiet" (click)="deleteComment(discussion, comment)">{{ store.t('确认删除') }}</button>
                <button type="button" class="quiet" (click)="deleting.set(null)">{{ store.t('取消') }}</button>
              } @else {
                <button type="button" class="quiet" (click)="deleting.set(comment.sequence)">{{ store.t('删除') }}</button>
              }
            }
          </article>
          } @empty {
            <p>{{ store.t('还没有评论') }}</p>
          }
          @if (discussion.closed) {
            <p>{{ store.t('评论已关闭') }}</p>
          } @else if (!discussion.canComment) {
            <p>{{ store.t('你已被讨论群禁言。') }}</p>
          } @else {
            <form (ngSubmit)="send(discussion)">
              @if (reply(); as target) {
                <span>{{ store.t('回复') }} {{ target.sender }}</span>
                <button class="quiet" type="button" (click)="reply.set(null)">{{ store.t('取消回复') }}</button>
              }
              <textarea name="comment" [(ngModel)]="draft" maxlength="4000" [attr.aria-label]="store.t('发表评论')"></textarea>
              <label>{{ store.t('添加附件') }}
                <input type="file" (change)="selectFile($event)" [disabled]="sending()">
              </label>
              @if (file(); as chosen) {
                <span>{{ chosen.name }}</span>
                <button class="quiet" type="button" [disabled]="sending()" (click)="removeFile()">{{ store.t('移除附件') }}</button>
              }
              <button type="submit" class="primary" [disabled]="sending() || (!draft.trim() && !file())">{{ store.t('发送') }}</button>
            </form>
          }
        }
        @if (canManage() && !post().deleted) {
          <button type="button" class="quiet" [disabled]="changing()" (click)="setOpen(!thread() || !!thread()?.closed)">
            {{ store.t(thread() && !thread()?.closed ? '关闭评论' : '开启评论') }}
          </button>
        }
        @if (error()) { <p role="status">{{ store.t(error()) }}</p> }
      </section>
    }
  `,
  styles: [`
    :host { display: block; margin-top: .25rem; }
    section { display: grid; gap: .5rem; min-width: min(16rem, 55vw); border-top: 1px solid var(--line); padding-top: .5rem; }
    article { border-bottom: 1px solid var(--line); padding: .35rem 0; }
    p { margin: .25rem 0; white-space: pre-wrap; overflow-wrap: anywhere; }
    blockquote { margin: .25rem 0; padding-inline-start: .5rem; border-inline-start: 2px solid var(--line); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    form { display: grid; gap: .35rem; }
    textarea { width: 100%; }
  `],
})
export class ChatComments {
  readonly store = inject(ChatStore);
  readonly fromGroup = input(false);
  readonly post = input.required<ChatMessage>();
  readonly summary = input<DiscussionSummary>();
  readonly expanded = signal(false);
  readonly hasOlder = signal(false);
  readonly loadingOlder = signal(false);
  private visibleLimit = 50;

  private commentsUrl(before?: number) {
    const post = this.post();
    const path = this.fromGroup()
      ? `/api/chat/rooms/${post.roomId}/discussions/${post.sequence}/comments`
      : `/api/chat/channels/${post.roomId}/posts/${post.sequence}/comments`;
    return before === undefined ? path : `${path}?before=${before}`;
  }

  async loadOlder() {
    if (this.loadingOlder()) return;
    this.visibleLimit += 50;
    this.loadingOlder.set(true);
    try {
      await this.refresh();
    } finally {
      this.loadingOlder.set(false);
    }
  }
  readonly thread = signal<DiscussionThread | null>(null);
  readonly reply = signal<ChatMessage | null>(null);
  readonly editTarget = signal<number | null>(null);
  readonly savingEdit = signal(false);
  editedText = '';

  quotedComment(discussion: DiscussionThread, sequence: number) {
    return discussion.messages.find(message => message.sequence === sequence);
  }

  beginEdit(comment: ChatMessage) {
    this.editedText = comment.text;
    this.editTarget.set(comment.sequence);
  }

  async editComment(thread: DiscussionThread, comment: ChatMessage) {
    if (this.savingEdit()) return;
    this.savingEdit.set(true);
    this.error.set('');
    try {
      const response = await fetch(`/api/chat/rooms/${thread.groupId}/messages/${comment.sequence}/edit`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
        body: JSON.stringify({ text: this.editedText }),
      });
      if (!response.ok) throw new Error();
      if (this.editTarget() === comment.sequence) this.editTarget.set(null);
      await this.refresh();
    } catch {
      this.error.set('无法保存评论修改。');
    } finally {
      this.savingEdit.set(false);
    }
  }

  readonly deleting = signal<number | null>(null);

  canDelete(thread: DiscussionThread, comment: ChatMessage) {
    return !comment.card && (comment.sender === this.store.user() ||
      !!this.store.rooms().find(room => room.id === thread.groupId)?.canModerate);
  }

  async deleteComment(thread: DiscussionThread, comment: ChatMessage) {
    try {
      const response = await fetch(`/api/chat/rooms/${thread.groupId}/messages/${comment.sequence}/delete`, {
        method: 'POST', headers: { 'X-LittleLoop-Chat': '1' },
      });
      if (!response.ok) throw new Error();
      this.deleting.set(null);
      await this.refresh();
    } catch {
      this.error.set('无法删除评论。');
    }
  }

  readonly sending = signal(false);
  readonly changingFollow = signal(false);

  async setFollowing(following: boolean) {
    if (this.fromGroup() || this.changingFollow()) return;
    this.changingFollow.set(true);
    this.error.set('');
    try {
      const response = await fetch(this.commentsUrl() + '/follow', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
        body: JSON.stringify({ following }),
      });
      if (!response.ok) throw new Error();
      if (this.expanded()) await this.refresh();
    } catch {
      this.error.set('无法更新讨论关注状态。');
    } finally {
      this.changingFollow.set(false);
    }
  }

  readonly changing = signal(false);
  canManage() { return ((this.store.currentRoom()?.channelRights ?? 0) & 64) !== 0; }

  async setOpen(open: boolean) {
    if (this.changing()) return;
    this.changing.set(true);
    this.error.set('');
    const post = this.post();
    try {
      const response = await fetch(`/api/chat/channels/${post.roomId}/posts/${post.sequence}/discussion`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' }, body: JSON.stringify({ open }),
      });
      if (!response.ok) throw new Error();
      await this.refresh();
    } catch {
      this.error.set('无法更新评论设置。');
    } finally {
      this.changing.set(false);
    }
  }
  readonly error = signal('');
  readonly file = signal<File | null>(null);
  draft = '';

  private timer?: ReturnType<typeof setInterval>;
  private generation = 0;
  private pending?: { clientId: string; text: string; replyTo: number; attachmentId?: string };
  private uploadId?: string;
  private uploaded?: Attachment;

  selectFile(event: Event) {
    const element = event.target as HTMLInputElement;
    const file = element.files?.[0];
    element.value = '';
    if (!file) return;
    if (file.size < 1 || file.size > 5 * 1024 * 1024) {
      this.error.set('请选择不超过 5 MiB 的非空文件。');
      return;
    }
    this.error.set('');
    this.removeFile();
    this.file.set(file);
  }

  removeFile() {
    this.file.set(null);
    this.uploadId = undefined;
    this.uploaded = undefined;
  }

  private async upload(file: File) {
    if (this.uploaded) return this.uploaded;
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('uploadId', this.uploadId ??= crypto.randomUUID());
    const result = await fetch('/api/chat/attachments', {
      method: 'POST', headers: { 'X-LittleLoop-Chat': '1' }, body: form,
    });
    if (!result.ok) throw new Error();
    return this.uploaded = await result.json() as Attachment;
  }

  constructor() {
    effect(() => {
      const target = this.store.linkedComments();
      const post = this.post();
      if (target?.roomId === post.roomId && target.sequence === post.sequence) {
        this.store.linkedComments.set(null);
        if (!this.expanded()) this.toggle();
      }
    });
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  toggle() {
    this.expanded.update(value => !value);
    if (!this.expanded()) {
      this.stop();
      return;
    }
    void this.refresh();
    this.timer = setInterval(() => { if (!this.loadingOlder()) void this.refresh(); }, 3000);
  }

  private stop() {
    clearInterval(this.timer);
    this.generation++;
  }

  private async refresh() {
    const generation = ++this.generation;
    try {
      const response = await fetch(this.commentsUrl());
      if (generation !== this.generation) return;
      if (response.status === 404) {
        this.thread.set(null);
        this.error.set('此帖子没有可访问的讨论。');
        return;
      }
      if (!response.ok) throw new Error();
      const discussion = await response.json() as DiscussionThread;
      if (generation !== this.generation) return;
      let oldest = discussion.messages[0]?.sequence;
      let pageSize = discussion.messages.length;
      const messages = discussion.messages;
      while (pageSize === 50 && messages.length < this.visibleLimit && oldest !== undefined) {
        const previous = await fetch(this.commentsUrl(oldest));
        if (generation !== this.generation) return;
        if (!previous.ok) throw new Error();
        const older = (await previous.json() as DiscussionThread).messages;
        if (generation !== this.generation) return;
        pageSize = older.length;
        oldest = older[0]?.sequence;
        messages.unshift(...older);
      }
      this.hasOlder.set(pageSize === 50);
      this.thread.set(discussion);
      const sequence = discussion.messages.at(-1)?.sequence;
      if (sequence && discussion.unread > 0 && sequence > discussion.readSequence &&
          document.visibilityState === 'visible') {
        void fetch(this.commentsUrl() + '/read', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
          body: JSON.stringify({ sequence }),
        }).catch(() => undefined);
      }
      if (!this.sending() && ['无法加载评论。', '此帖子没有可访问的讨论。'].includes(this.error())) this.error.set('');
    } catch {
      if (generation === this.generation) this.error.set('无法加载评论。');
    }
  }

  async send(discussion: DiscussionThread) {
    const text = this.draft.trim();
    const replyTo = this.reply()?.sequence ?? discussion.rootSequence;
    const file = this.file();
    if ((!text && !file) || this.sending() || !discussion.canComment) return;
    this.sending.set(true);
    this.error.set('');
    try {
      const attachmentId = file ? (await this.upload(file)).id : undefined;
      if (this.pending?.text !== text || this.pending.replyTo !== replyTo || this.pending.attachmentId !== attachmentId)
        this.pending = { text, replyTo, attachmentId, clientId: crypto.randomUUID() };
      const response = await fetch(`/api/chat/rooms/${discussion.groupId}/messages`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
        body: JSON.stringify(this.pending),
      });
      if (response.status === 403 || response.status === 404) {
        const failure = await response.json().catch(() => null) as { code?: string } | null;
        await this.refresh();
        this.error.set(failure?.code === 'discussion_closed' ? '评论已关闭，草稿已保留。'
          : failure?.code === 'group_silenced' ? '你已被讨论群禁言，草稿已保留。'
          : '无法访问讨论，草稿已保留。');
        return;
      }
      if (!response.ok) throw new Error();
      if (this.draft.trim() === text) {
        this.draft = '';
        this.reply.set(null);
      }
      this.removeFile();
      this.pending = undefined;
      await this.refresh();
    } catch {
      this.error.set('无法发送评论，请重试。');
    } finally {
      this.sending.set(false);
    }
  }
}
