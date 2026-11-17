import { Injectable, computed, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { AccountSettings, DeviceSettings, accountDefaults, deviceDefaults } from '../settings/chat-settings';
import { ChatI18n } from './chat-i18n';
import {
  Attachment, Channel, ChatMessage, Friend, Member, Notice, Outgoing, Room,
} from './chat-model';

@Injectable({ providedIn: 'root' })
export class ChatStore {
  private readonly i18n = inject(ChatI18n);

  readonly user = signal('');
  readonly drawer = signal(false);
  readonly settingsOpen = signal(false);
  readonly detailsOpen = signal(false);
  readonly friendsOpen = signal(false);
  readonly friends = signal<Friend[]>([]);
  friendUsername = '';
  memberUsername = '';
  readonly detailMembers = signal<Member[]>([]);
  readonly inRoomSearch = signal(false);
  readonly folder = signal('');
  readonly accountPrefs = signal<AccountSettings>(accountDefaults);
  readonly devicePrefs = signal<DeviceSettings>(this.loadDevice());
  readonly displayedName = computed(() => this.accountPrefs().profile.displayName || this.user());
  readonly emptyPreviewSet = new Set<string>();
  readonly manuallyLoaded = signal(new Set<string>());
  private drafts = new Map<string, string>();
  private scrolls = new Map<string, number>();
  private audio?: AudioContext;
  private linkRoom = new URLSearchParams(location.search).get('room');
  private readonly linkMessage = Number(new URLSearchParams(location.search).get('message'));
  private readonly linkPost = Number(new URLSearchParams(location.search).get('comments'));
  readonly linkedComments = signal<{roomId: string; sequence: number} | null>(null);

  readonly visibleRooms = computed(() => {
    const folder = this.accountPrefs().folders.find(item => item.name === this.folder());
    return this.rooms().filter(room => room.archived === this.showArchive() && (!folder ||
      (!folder.exclude?.includes(room.id) && (folder.include?.includes(room.id) || folder.kind === 'all' ||
        folder.kind === room.type || (folder.kind === 'group' && room.type === 'topic') ||
        (folder.kind === 'unread' && room.unread > 0)))));
  });
  readonly rooms = signal<Room[]>([]);
  readonly selected = signal('');
  readonly currentRoom = computed(() => this.rooms().find(room => room.id === this.selected()));
  readonly messages = signal<ChatMessage[]>([]);
  readonly connection = signal('正在连接');
  readonly error = signal('');
  readonly more = signal(false);
  readonly busy = signal(false);
  readonly pending = signal<Record<string, Outgoing>>({});
  readonly reply = signal<ChatMessage | null>(null);
  readonly showArchive = signal(false);
  readonly notice = signal<Notice | null>(null);
  readonly attachment = signal<Attachment | null>(null);
  readonly editing = signal<ChatMessage | null>(null);
  readonly forwarding = signal<ChatMessage | null>(null);
  readonly removing = signal<ChatMessage | null>(null);
  readonly searchResults = signal<ChatMessage[] | null>(null);
  searchTerm = '';
  editText = '';
  forwardRoom = '';
  readonly bot = signal({ enabled: false, canOperate: false, canApprove: false, providers: [] as string[] });
  readonly roomProviders = signal<string[]>([]);
  readonly directory = signal<Channel[] | null>(null);
  readonly topicDialog = signal(false);
  readonly channelDialog = signal(false);
  provider = '';
  readonly botInstalled = signal(false);
  readonly sources = signal<string[] | null>(null);
  readonly taskAnchor = signal<ChatMessage | null>(null);
  sourcePath = '';
  sourceStart = 1;
  sourceEnd = 20;
  question = '';
  goal = '';
  private questionId?: string;
  private taskId?: string;
  private loadedRevision = -1;
  private highlight?: number;
  private readonly notifications = new Set<string>();
  mode: 'login' | 'register' = 'login';
  name = '';
  password = '';
  invite = '';
  kind: 'direct' | 'group' = 'direct';
  members = '';
  title = '';
  text = '';
  private hub?: HubConnection;
  private syncQueued = false;
  private syncing?: Promise<void>;
  private historyVersion = 0;
  private loadedRoom = '';

  constructor() {
    this.applyLanguage();
    void this.api<{ name: string }>('/me').then(result => this.enter(result.name)).catch(() => {});
  }

  t(value: string, vars?: Record<string, string | number>) { return this.i18n.t(value, vars); }

  locale() { return this.i18n.locale(); }

  async authenticate() {
    await this.action(async () => {
      const credentials = { name: this.name, password: this.password, invite: this.invite };
      if (this.mode === 'register') await this.api('/register', credentials);
      const account = await this.api<{ name: string }>('/login', { name: this.name, password: this.password });
      this.password = '';
      this.invite = '';
      this.enter(account.name);
    });
  }

  private enter(name: string) {
    this.user.set(name);
    void this.refreshAccount();
    void this.api<{ enabled: boolean; canOperate: boolean; canApprove: boolean; providers: string[] }>('/bot/capabilities')
      .then(info => { if (this.user() === name) { this.bot.set(info); this.requestSync(); } }).catch(() => {});
    this.pending.set(JSON.parse(sessionStorage.getItem('chat-outbox:' + name) ?? '{}'));
    this.connection.set('正在连接');
    const hub = new HubConnectionBuilder().withUrl('/hubs/chat')
      .withAutomaticReconnect([0, 1000, 2000, 5000])
      .configureLogging(LogLevel.Error).build();
    this.hub = hub;
    hub.on('roomChanged', (id: string) => {
      if (this.hub !== hub) return;
      this.notifications.add(id);
      this.requestSync();
    });
    hub.on('readChanged', () => this.requestSync());
    hub.on('socialChanged', () => this.requestSync());
    hub.on('settingsChanged', () => void this.refreshAccount());
    hub.on('identityChanged', (id: string) => {
      if (this.selected() === id) this.loadedRevision = -1;
      this.requestSync();
    });
    hub.onreconnecting(() => this.connection.set('正在重连'));
    hub.onreconnected(() => {
      this.connection.set('已连接');
      void this.refreshAccount();
      this.requestSync();
    });
    hub.onclose(() => {
      if (this.hub !== hub) return;
      this.connection.set('已断开');
      setTimeout(() => void this.connect(hub), 1500);
    });
    this.requestSync();
    void this.connect(hub);
  }

  private async connect(hub: HubConnection) {
    if (this.hub !== hub || hub.state !== HubConnectionState.Disconnected) return;
    try {
      await hub.start();
      if (this.hub !== hub) return;
      this.connection.set('已连接');
      void this.refreshAccount();
      this.requestSync();
    } catch {
      if (this.hub !== hub) return;
      this.connection.set('已断开');
      setTimeout(() => void this.connect(hub), 1500);
    }
  }

  async logout() {
    await this.action(async () => {
      await this.api('/logout', {});
      const hub = this.hub;
      this.hub = undefined;
      this.clearAccount();
      this.user.set('');
      this.bot.set({ enabled: false, canOperate: false, canApprove: false, providers: [] });
      this.directory.set(null);
      this.resetBotForms();
      this.historyVersion++;
      this.rooms.set([]);
      this.selected.set('');
      this.messages.set([]);
      this.loadedRoom = '';
      this.loadedRevision = -1;
      this.notifications.clear();
      this.notice.set(null);
      this.searchResults.set(null);
      this.showArchive.set(false);
      this.attachment.set(null);
      this.editing.set(null);
      this.removing.set(null);
      this.forwarding.set(null);
      this.reply.set(null);
      this.text = '';
      await hub?.stop();
    });
  }

  async createRoom(menu?: HTMLDetailsElement) {
    await this.action(async () => {
      const result = await this.api<{ id: string }>('/rooms', {
        type: this.kind, title: this.title, members: this.members.split(/[,，\s]+/).filter(Boolean),
      });
      this.members = '';
      this.title = '';
      if (menu) menu.open = false;
      this.select(result.id);
    });
  }

  select(room: string) {
    if (this.selected() === room) return;
    const oldRoom = this.selected();
    if (oldRoom) {
      this.drafts.set(oldRoom, this.text);
      const timeline = document.querySelector('.timeline');
      if (timeline) this.scrolls.set(oldRoom, timeline.scrollTop);
    }
    this.historyVersion++;
    this.detailsOpen.set(false);
    this.inRoomSearch.set(false);
    this.selected.set(room);
    this.topicDialog.set(false);
    this.channelDialog.set(false);
    this.resetBotForms();
    this.loadedRoom = '';
    this.loadedRevision = -1;
    this.messages.set([]);
    this.more.set(false);
    this.reply.set(null);
    this.attachment.set(null);
    this.editing.set(null);
    this.forwarding.set(null);
    this.removing.set(null);
    if (this.notice()?.id === room) this.notice.set(null);
    const summary = this.rooms().find(item => item.id === room);
    if (summary) this.showArchive.set(summary.archived);
    this.text = this.drafts.get(room) ?? '';
    this.requestSync();
  }

  private requestSync() {
    this.syncQueued = true;
    if (this.syncing) return;
    this.syncing = this.sync().catch(e => this.error.set(e.message)).finally(() => {
      this.syncing = undefined;
      if (this.syncQueued && this.user()) this.requestSync();
    });
  }

  private async sync() {
    while (this.syncQueued && this.user()) {
      this.syncQueued = false;
      const hub = this.hub;
      const incoming = new Set(this.notifications);
      this.notifications.clear();
      const rooms = await this.api<Room[]>('/rooms');
      if (this.hub !== hub) continue;
      const previous = this.rooms();
      const directory = this.directory();
      if (directory !== null && previous.some(room => room.type === 'channel' && incoming.has(room.id) &&
        !rooms.some(current => current.id === room.id))) {
        const channels = await this.api<Channel[]>('/channels');
        if (this.hub !== hub) continue;
        if (this.directory() === directory) this.directory.set(channels);
      }
      const notify = rooms.find(item => incoming.has(item.id) && this.notificationsEnabled(item) && !item.muted && item.id !== this.selected() && item.unread > 0 &&
        item.latest > (previous.find(old => old.id === item.id)?.latest ?? 0));
      if (notify) {
        this.notice.set({
          id: notify.id,
          title: this.roomName(notify),
          preview: this.accountPrefs().notifications.preview ? this.roomPreview(notify) : undefined,
        });
        this.playNotice();
      }
      this.rooms.set(rooms);
      const accessible = new Set(rooms.map(room => room.id));
      const hits = this.searchResults();
      if (hits?.some(hit => !accessible.has(hit.roomId))) {
        this.searchResults.set(hits.filter(hit => accessible.has(hit.roomId)));
      }
      const selected = this.selected();
      if (selected && !accessible.has(selected) && previous.some(room => room.id === selected)) {
        this.select('');
        this.drafts.delete(selected);
        this.scrolls.delete(selected);
      }
      const oldNotice = this.notice();
      if (oldNotice && !accessible.has(oldNotice.id)) this.notice.set(null);
      if (this.friendsOpen()) {
        const friends = await this.api<Friend[]>('/friends');
        if (this.hub !== hub) continue;
        this.friends.set(friends);
      }
      if (this.linkRoom && rooms.some(item => item.id === this.linkRoom)) {
        const target = this.linkRoom;
        this.linkRoom = null;
        if (Number.isSafeInteger(this.linkPost) && this.linkPost > 0 &&
            rooms.some(item => item.id === target && item.type === 'channel')) {
          this.linkedComments.set({ roomId: target, sequence: this.linkPost });
          this.openResult({ roomId: target, sequence: this.linkPost });
        } else if (Number.isSafeInteger(this.linkMessage) && this.linkMessage > 0) {
          this.openResult({ roomId: target, sequence: this.linkMessage });
        } else this.select(target);
      }
      if (incoming.size > 0 && this.searchResults() !== null) await this.runSearch(this.searchTerm);
      const room = this.selected();
      const summary = rooms.find(r => r.id === room);
      if (!summary) continue;
      const version = this.historyVersion;
      if (this.detailsOpen()) await this.refreshMembers(room);
      if (this.bot().enabled) {
        const info = await this.api<{ installed: boolean; providers: string[] }>('/bot/rooms/' + room);
        if (version !== this.historyVersion) continue;
        this.botInstalled.set(info.installed);
        this.roomProviders.set(info.providers);
        if (!info.providers.includes(this.provider)) this.provider = info.providers[0] ?? '';
      }
      if (this.loadedRoom !== room) {
        const target = this.highlight;
        const batch = await this.history(room, target ? '?after=' + (target - 1) : '');
        if (version !== this.historyVersion) continue;
        this.messages.set(batch);
        this.more.set((batch[0]?.sequence ?? 1) > 1);
        this.loadedRoom = room;
        this.loadedRevision = summary.revision;
        if (target) this.scrollTo(target);
        else requestAnimationFrame(() => {
          if (version !== this.historyVersion) return;
          const frame = document.querySelector('.timeline');
          if (frame) frame.scrollTop = this.scrolls.get(room) ?? frame.scrollHeight;
        });
        this.highlight = undefined;
      } else {
        if (this.loadedRevision !== summary.revision) {
          // Tombstones preserve the sequence and reply anchors. Refresh the loaded window for edits/deletes.
          const first = this.messages()[0]?.sequence ?? 1;
          let cursor = first - 1;
          while (cursor < summary.latest && version === this.historyVersion) {
            const batch = await this.history(room, '?after=' + cursor);
            if (version !== this.historyVersion || batch.length === 0) break;
            this.merge(batch);
            cursor = batch.at(-1)!.sequence;
          }
          this.loadedRevision = summary.revision;
        }
        while (version === this.historyVersion) {
          const last = this.messages().at(-1)?.sequence ?? 0;
          if (last >= summary.latest) break;
          const batch = await this.history(room, '?after=' + last);
          if (version !== this.historyVersion || batch.length === 0) break;
          this.merge(batch);
        }
      }
      const last = this.messages().at(-1)?.sequence ?? 0;
      if (version === this.historyVersion && last > summary.read) {
        await this.api('/rooms/' + room + '/read', { sequence: last });
        if (this.hub === hub)
          this.rooms.update(items => items.map(item => item.id === room && item.latest <= last
            ? { ...item, read: last, unread: 0 } : item));
      }
    }
  }

  async older() {
    const version = this.historyVersion;
    const first = this.messages()[0]?.sequence;
    if (first === undefined) return;
    await this.action(async () => {
      const items = await this.history(this.selected(), '?before=' + first);
      if (version !== this.historyVersion) return;
      const frame = document.querySelector('.timeline');
      const height = frame?.scrollHeight ?? 0;
      const top = frame?.scrollTop ?? 0;
      this.merge(items);
      if (frame) requestAnimationFrame(() => { if (version === this.historyVersion) frame.scrollTop = top + frame.scrollHeight - height; });
      this.more.set((this.messages()[0]?.sequence ?? 1) > 1);
    });
  }

  private resetBotForms() {
    this.botInstalled.set(false);
    this.roomProviders.set([]);
    this.provider = '';
    this.sources.set(null);
    this.taskAnchor.set(null);
    this.question = '';
    this.goal = '';
    this.questionId = undefined;
    this.taskId = undefined;
  }

  botName(name: string) {
    if (name === 'deepseek_test') return 'DeepSeek · ' + this.t('测试项目');
    return name === 'deepseek' ? 'DeepSeek' : name === 'source_review' ? this.t('源码审阅') : 'LittleLoop';
  }

  roomType(room: Room) {
    return this.t({ direct: '私聊', group: '群聊', saved: '收藏', legacy: '历史', topic: '话题', channel: '频道' }[room.type]);
  }

  async showChannels() {
    if (this.directory() !== null) { this.directory.set(null); return; }
    await this.action(() => this.refreshChannels());
  }

  private async refreshChannels() { this.directory.set(await this.api<Channel[]>('/channels')); }

  async deleteChannel(id: string) {
    const response = await fetch('/api/chat/channels/' + id, {
      method: 'DELETE', headers: { 'X-LittleLoop-Chat': '1' },
    });
    if (!response.ok) throw new Error('Channel deletion failed.');
    this.notifications.add(id);
    this.requestSync();
  }

  async subscribe(channel: Channel) {
    await this.action(async () => {
      await this.api('/channels/' + channel.id + '/subscription', { subscribed: !channel.subscribed });
      if (channel.subscribed && this.selected() === channel.id) this.select('');
      await this.refreshChannels();
      this.requestSync();
    });
  }

  async createChannel(title: string) {
    if (!title.trim()) return;
    await this.action(async () => {
      const channel = await this.api<{ id: string }>('/channels', { title: title.trim() });
      this.channelDialog.set(false);
      if (this.directory() !== null) await this.refreshChannels();
      this.select(channel.id);
    });
  }

  async createTopic(title: string) {
    if (!title.trim()) return;
    const parent = this.selected();
    await this.action(async () => {
      const topic = await this.api<{ id: string }>('/rooms/' + parent + '/topics', { title: title.trim() });
      this.topicDialog.set(false);
      this.select(topic.id);
    });
  }

  openCreateChannel() {
    this.error.set('');
    this.topicDialog.set(false);
    this.channelDialog.set(true);
  }

  openCreateTopic() {
    this.error.set('');
    this.channelDialog.set(false);
    this.topicDialog.set(true);
  }

  closeCreateDialogs() {
    this.channelDialog.set(false);
    this.topicDialog.set(false);
  }

  async directBot(provider: string) {
    await this.action(async () => {
      const room = await this.api<{ id: string }>('/bot/direct?provider=' + encodeURIComponent(provider), {});
      this.select(room.id);
      this.requestSync();
    });
  }

  async installBot(enabled: boolean) {
    const room = this.selected();
    await this.action(async () => {
      await this.api('/bot/rooms/' + room + '/install', { enabled });
      if (!enabled && this.selected() === room) this.resetBotForms();
      this.requestSync();
    });
  }

  async openSources() {
    if (this.sources() !== null) { this.sources.set(null); return; }
    const room = this.selected();
    await this.action(async () => {
      const paths = await this.api<string[]>('/bot/rooms/' + room + '/sources');
      if (this.selected() === room) this.sources.set(paths);
    });
  }

  async askBot() {
    const room = this.selected();
    this.questionId ??= crypto.randomUUID();
    await this.action(async () => {
      await this.api('/bot/rooms/' + room + '/ask', {
        clientId: this.questionId, text: this.question, path: this.sourcePath, start: this.sourceStart,
        end: this.sourceEnd, replyTo: this.reply()?.sequence ?? null, provider: this.provider,
      });
      if (this.selected() === room) {
        this.sources.set(null);
        this.question = '';
        this.questionId = undefined;
        this.reply.set(null);
      }
      this.requestSync();
    });
  }

  beginTask(message: ChatMessage) {
    if (this.taskAnchor()?.sequence !== message.sequence) {
      this.taskId = undefined;
      this.goal = '';
    }
    this.taskAnchor.set(message);
  }

  async queueTask() {
    const anchor = this.taskAnchor();
    if (!anchor) return;
    this.taskId ??= crypto.randomUUID();
    await this.action(async () => {
      await this.api('/bot/rooms/' + anchor.roomId + '/schedule',
        { clientId: this.taskId, sequence: anchor.sequence, goal: this.goal, provider: this.provider });
      if (this.selected() === anchor.roomId) {
        this.taskAnchor.set(null);
        this.goal = '';
        this.taskId = undefined;
      }
      this.requestSync();
    });
  }

  private merge(batch: ChatMessage[]) {
    const frame = document.querySelector('.timeline');
    const last = this.messages().at(-1)?.sequence ?? 0;
    const scrollToEnd = frame && batch.some(message => message.sequence > last) &&
      frame.scrollHeight - frame.clientHeight - frame.scrollTop < 70;
    this.messages.update(current => {
      const items = new Map(current.map(item => [item.sequence, item]));
      for (const item of batch) items.set(item.sequence, item);
      return [...items.values()].sort((a, b) => a.sequence - b.sequence);
    });
    if (scrollToEnd) requestAnimationFrame(() => { frame.scrollTop = frame.scrollHeight; });
  }

  jump(sequence: number) {
    if (this.messages().some(message => message.sequence === sequence)) this.scrollTo(sequence);
    else this.openResult({ roomId: this.selected(), sequence });
  }

  private scrollTo(sequence: number) {
    requestAnimationFrame(() => document.getElementById('message-' + sequence)?.scrollIntoView({ block: 'center' }));
  }

  openResult(message: Pick<ChatMessage, 'roomId' | 'sequence'>) {
    this.searchResults.set(null);
    this.showArchive.set(this.rooms().find(room => room.id === message.roomId)?.archived ?? false);
    if (this.selected() === message.roomId && this.messages().some(item => item.sequence === message.sequence)) {
      this.scrollTo(message.sequence);
      return;
    }
    this.highlight = message.sequence;
    if (this.selected() !== message.roomId) this.select(message.roomId);
    else {
      this.historyVersion++;
      this.loadedRoom = '';
      this.requestSync();
    }
  }

  async upload(event: Event) {
    const element = event.target as HTMLInputElement;
    const file = element.files?.[0];
    if (!file) return;
    const room = this.selected();
    await this.action(async () => {
      const form = new FormData();
      form.append('file', file, file.name);
      const attachment = await this.api<Attachment>('/attachments', form);
      if (this.selected() === room) this.attachment.set(attachment);
    });
    element.value = '';
  }

  attachmentUrl(message: ChatMessage) {
    return '/api/chat/rooms/' + message.roomId + '/messages/' + message.sequence + '/attachment';
  }

  async search() { await this.action(() => this.runSearch(this.searchTerm)); }

  private async runSearch(term: string) {
    if (!term.trim()) { this.searchResults.set(null); return; }
    const room = this.inRoomSearch() ? this.selected() : '';
    const hits = await this.api<ChatMessage[]>('/search?q=' + encodeURIComponent(term.trim()) + (room ? '&room=' + room : ''));
    if (term === this.searchTerm && this.user()) {
      const accessible = new Set(this.rooms().map(item => item.id));
      this.searchResults.set(hits.filter(hit => accessible.has(hit.roomId)));
    }
  }

  roomTitle(id: string) {
    const room = this.rooms().find(item => item.id === id);
    return room ? this.roomName(room) : '';
  }

  roomName(room: Room) {
    if (room.type === 'saved') return this.t('个人收藏');
    if (room.type === 'legacy') return this.t('历史工作台');
    return room.title;
  }

  roomPreview(room: Room) { return room.previewDeleted ? this.t('消息已删除') : room.preview; }

  beginEdit(message: ChatMessage) { this.editText = message.text; this.editing.set(message); }

  async saveEdit() {
    const message = this.editing();
    if (!message) return;
    await this.action(async () => {
      await this.api(this.messagePath(message) + '/edit', { text: this.editText });
      this.editing.set(null);
      this.requestSync();
    });
  }

  async deleteMessage() {
    const message = this.removing();
    if (!message) return;
    await this.action(async () => {
      await this.api(this.messagePath(message) + '/delete', {});
      this.removing.set(null);
      this.requestSync();
    });
  }

  private messagePath(message: ChatMessage) {
    return '/rooms/' + message.roomId + '/messages/' + message.sequence;
  }

  async pinMessage(message: ChatMessage) {
    await this.action(async () => {
      await this.api(this.messagePath(message) + '/pin', { value: this.currentRoom()?.pinnedMessage !== message.sequence });
      this.requestSync();
    });
  }

  async saved() {
    await this.action(async () => {
      const room = await this.api<{ id: string }>('/saved', {});
      this.showArchive.set(this.rooms().find(item => item.id === room.id)?.archived ?? false);
      this.select(room.id);
    });
  }

  beginForward(message: ChatMessage) { this.forwardRoom = ''; this.forwarding.set(message); }

  async forward(toSaved = false) {
    const message = this.forwarding();
    if (!message || (!toSaved && !this.forwardRoom)) return;
    await this.action(async () => {
      const target = toSaved ? (await this.api<{ id: string }>('/saved', {})).id : this.forwardRoom;
      await this.api('/rooms/' + target + '/forward', { roomId: message.roomId, sequence: message.sequence, clientId: crypto.randomUUID() });
      this.forwarding.set(null);
      this.requestSync();
    });
  }

  async preference(room: Room, key: 'pinned' | 'archived' | 'muted') {
    await this.action(async () => {
      await this.api('/rooms/' + room.id + '/preference', { key, value: !room[key] });
      if (key === 'archived') this.select('');
      this.requestSync();
    });
  }

  async send(retry = false) {
    const room = this.selected();
    if (!room || this.busy()) return;
    const outgoing = retry ? this.pending()[room] :
      { clientId: crypto.randomUUID(), text: this.text, replyTo: this.reply()?.sequence ?? null, attachmentId: this.attachment()?.id };
    if (!outgoing || (!outgoing.text.trim() && !outgoing.attachmentId) || (!retry && this.pending()[room])) return;
    this.pending.update(items => ({ ...items, [room]: outgoing }));
    this.persistOutbox();
    await this.action(async () => {
      await this.api('/rooms/' + room + '/messages', outgoing);
      this.pending.update(items => Object.fromEntries(Object.entries(items).filter(([id]) => id !== room)));
      this.persistOutbox();
      this.drafts.delete(room);
      if (this.selected() === room) {
        this.text = ''; this.reply.set(null); this.attachment.set(null);
        this.scrolls.delete(room);
      }
      this.requestSync();
    });
  }

  private persistOutbox() {
    sessionStorage.setItem('chat-outbox:' + this.user(), JSON.stringify(this.pending()));
  }

  private history(room: string, suffix = '') {
    return this.api<ChatMessage[]>('/rooms/' + room + '/messages' + suffix);
  }

  private loadDevice(): DeviceSettings {
    try { return { ...deviceDefaults, ...JSON.parse(localStorage.getItem('littleloop-chat-ui') ?? '{}') }; }
    catch { return deviceDefaults; }
  }

  changeDevice(value: DeviceSettings) {
    this.devicePrefs.set(value);
    localStorage.setItem('littleloop-chat-ui', JSON.stringify(value));
    this.applyLanguage();
    if (this.user()) void this.syncPushLanguage();
  }

  setLanguage(language: DeviceSettings['language']) {
    this.changeDevice({ ...this.devicePrefs(), language });
  }

  private async syncPushLanguage() {
    try {
      const registration = await navigator.serviceWorker?.getRegistration('/chat');
      const subscription = await registration?.pushManager.getSubscription();
      if (subscription && (await this.api<{ subscribed: boolean }>('/settings/push')).subscribed)
        await this.api('/settings/push', { ...subscription.toJSON(), language: this.i18n.locale() });
    } catch { /* Browser may be offline; previous subscription remains valid. */ }
  }

  private applyLanguage() {
    const value = this.devicePrefs().language;
    const locale = value === 'en' || (value === 'system' && navigator.language.toLowerCase().startsWith('en')) ? 'en' : 'zh';
    this.i18n.use(locale);
    if (typeof document !== 'undefined') document.title = this.t('LittleLoop 聊天');
  }

  private clearAccount() {
    this.drawer.set(false);
    this.friendsOpen.set(false);
    this.friends.set([]);
    this.friendUsername = '';
    this.settingsOpen.set(false);
    this.detailsOpen.set(false);
    this.channelDialog.set(false);
    this.topicDialog.set(false);
    this.folder.set('');
    this.accountPrefs.set(accountDefaults);
    this.drafts.clear();
    this.scrolls.clear();
    this.manuallyLoaded.set(new Set());
  }

  async refreshAccount() {
    const user = this.user();
    if (!user) return;
    try {
      const prefs = await this.api<AccountSettings>('/settings');
      if (this.user() !== user) return;
      if (prefs.username && prefs.username !== user) {
        sessionStorage.removeItem('chat-outbox:' + user);
        this.user.set(prefs.username);
        this.persistOutbox();
        this.loadedRevision = -1;
        this.requestSync();
      }
      this.accountPrefs.set(prefs);
      if (!prefs.folders.some(item => item.name === this.folder())) this.folder.set('');
    } catch { /* Preferences keep their last confirmed values until the next sync. */ }
  }

  openSettings() {
    this.drawer.set(false);
    this.settingsOpen.set(true);
    void this.refreshAccount();
  }

  openFriends() {
    this.drawer.set(false);
    this.friendsOpen.set(true);
    this.requestSync();
  }

  async requestFriend() {
    await this.action(async () => {
      await this.api('/friends', { username: this.friendUsername.trim() });
      this.friendUsername = '';
      this.requestSync();
    });
  }

  async respondFriend(friend: Friend, accepted: boolean) {
    await this.action(async () => {
      await this.api('/friends/' + friend.id + '/respond', { accepted });
      this.requestSync();
    });
  }

  async directFriend(friend: Friend) {
    await this.action(async () => {
      const result = await this.api<{ id: string }>('/rooms', { type: 'direct', title: '', members: [friend.username] });
      this.friendsOpen.set(false);
      this.select(result.id);
    });
  }

  async changeMember(operation: 'invite' | 'remove' | 'transfer' | 'promote' | 'demote' | 'silence' | 'unsilence', username = this.memberUsername) {
    const room = this.currentRoom();
    if (!room?.canManage) return;
    await this.action(async () => {
      await this.api('/rooms/' + room.id + '/members', { operation, username: username.trim() });
      if (operation === 'invite') this.memberUsername = '';
      this.requestSync();
    });
  }

  private async refreshMembers(room: string) {
    const items = await this.api<Member[]>('/rooms/' + room + '/members');
    if (this.selected() === room && this.detailsOpen()) this.detailMembers.set(items);
  }

  openDetails() {
    if (!this.selected()) return;
    if (this.detailsOpen()) { this.detailsOpen.set(false); return; }
    this.detailMembers.set([]);
    this.memberUsername = '';
    this.detailsOpen.set(true);
    this.requestSync();
  }

  avatarUrl(name: string, version: string) { return '/api/chat/avatars/' + encodeURIComponent(name) + '?v=' + version; }

  initial(value: string) { return Array.from(value.trim())[0]?.toUpperCase() ?? '?'; }

  day(sentAt?: string) { return sentAt ? new Date(sentAt).toDateString() : ''; }

  dateLabel(value: string, time = false) { return this.i18n.date(value, time); }

  showImage(message: ChatMessage) {
    return this.devicePrefs().autoImages || this.manuallyLoaded().has(message.roomId + ':' + message.sequence);
  }

  loadImage(message: ChatMessage) {
    this.manuallyLoaded.update(previous => new Set([...previous, message.roomId + ':' + message.sequence]));
  }

  composerKey(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && this.devicePrefs().sendOnEnter) {
      event.preventDefault();
      void this.send();
    }
  }

  toggleSearch(inside: boolean) {
    this.inRoomSearch.set(inside);
    this.searchResults.set(null);
    if (inside) requestAnimationFrame(() => document.getElementById('room-search')?.focus());
  }

  copyMessage(text: string) {
    void this.action(() => navigator.clipboard.writeText(text));
  }

  private notificationsEnabled(room: Room) {
    const prefs = this.accountPrefs().notifications;
    return room.type === 'channel' ? prefs.channels : room.type === 'group' || room.type === 'topic' ? prefs.groups : prefs.direct;
  }

  initAudio() {
    if (!this.audio && typeof AudioContext !== 'undefined') {
      this.audio = new AudioContext();
      void this.audio.resume().catch(() => {});
    }
  }

  private playNotice() {
    if (!this.accountPrefs().notifications.sound || this.audio?.state !== 'running') return;
    const oscillator = this.audio.createOscillator();
    const volume = this.audio.createGain();
    oscillator.frequency.value = 660;
    volume.gain.value = .045;
    oscillator.connect(volume).connect(this.audio.destination);
    oscillator.start();
    oscillator.stop(this.audio.currentTime + .13);
  }

  positionMessageMenu(event: Event) {
    const menu = event.target as HTMLDetailsElement;
    if (!menu.open) return;
    for (const other of document.querySelectorAll<HTMLDetailsElement>('.message-menu[open]')) {
      if (other !== menu) other.open = false;
    }
    const sheet = menu.querySelector<HTMLElement>('.menu-sheet')!;
    const anchor = menu.querySelector('summary')!.getBoundingClientRect();
    const margin = 8;
    const width = document.documentElement.clientWidth;
    const height = window.innerHeight;
    const aboveSpace = Math.max(0, anchor.top - margin - 5);
    const belowSpace = Math.max(0, height - margin - anchor.bottom - 5);
    sheet.style.maxHeight = `${Math.max(aboveSpace, belowSpace)}px`;
    const left = Math.max(margin, Math.min(anchor.right - sheet.offsetWidth, width - sheet.offsetWidth - margin));
    const below = anchor.bottom + 5;
    const top = below + sheet.offsetHeight <= height - margin ? below : anchor.top - sheet.offsetHeight - 5;
    sheet.style.left = `${left}px`;
    sheet.style.top = `${Math.max(margin, Math.min(top, height - sheet.offsetHeight - margin))}px`;
  }

  closeMessageMenus() {
    for (const menu of document.querySelectorAll<HTMLDetailsElement>('.message-menu[open]')) menu.open = false;
  }

  dismissMessageMenu(event: Event) {
    if (!(event.target as Element).closest('.message-menu')) this.closeMessageMenus();
  }

  escape() {
    const menu = document.querySelector<HTMLDetailsElement>('details.popover[open]');
    if (menu) { menu.open = false; menu.querySelector<HTMLElement>('summary')?.focus(); }
    else if (this.settingsOpen()) this.settingsOpen.set(false);
    else if (this.friendsOpen()) this.friendsOpen.set(false);
    else if (this.detailsOpen()) this.detailsOpen.set(false);
    else if (this.drawer()) this.drawer.set(false);
    else if (this.inRoomSearch()) this.toggleSearch(false);
  }

  private async action(run: () => Promise<void>) {
    this.error.set('');
    this.busy.set(true);
    try { await run(); }
    catch (e) { this.error.set(e instanceof Error ? e.message : String(e)); }
    finally { this.busy.set(false); }
  }

  private async api<T>(path: string, body?: object): Promise<T> {
    const response = await fetch('/api/chat' + path, body ? {
      method: 'POST', headers: body instanceof FormData ? { 'X-LittleLoop-Chat': '1' } : { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
      body: body instanceof FormData ? body : JSON.stringify(body),
    } : {});
    if (!response.ok) {
      if (response.status === 401 && this.user()) {
        const hub = this.hub;
        this.hub = undefined;
        this.historyVersion++;
        this.user.set('');
        this.clearAccount();
        this.rooms.set([]);
        this.selected.set('');
        this.messages.set([]);
        this.loadedRoom = '';
        this.pending.set({});
        void hub?.stop();
      }
      throw new Error(this.i18n.apiMessage(await response.text(), response.status));
    }
    return response.status === 204 ? undefined as T : response.json();
  }
}
