import { Component, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { apiMessage } from './chat-language';

export interface Folder { name: string; kind: string; include?: string[] | null; exclude?: string[] | null }
export interface AccountSettings {
  username?: string;
  profile: { displayName: string; bio: string; avatarVersion?: string };
  notifications: { direct: boolean; groups: boolean; channels: boolean; preview: boolean; sound: boolean };
  privacy: { groupInvites: boolean };
  folders: Folder[];
}
export interface DeviceSettings {
  theme: 'system' | 'light' | 'dark'; accent: string; background: string;
  scale: string; sendOnEnter: boolean; animations: boolean; autoImages: boolean; language: 'system' | 'zh' | 'en';
}
export const deviceDefaults: DeviceSettings = {
  theme: 'system', accent: 'blue', background: 'mint', scale: 'normal',
  sendOnEnter: false, animations: true, autoImages: true, language: 'system',
};
export const accountDefaults: AccountSettings = {
  profile: { displayName: '', bio: '' },
  notifications: { direct: true, groups: true, channels: true, preview: true, sound: false },
  privacy: { groupInvites: true }, folders: [],
};
interface Device { id: string; agent: string; created: string; current: boolean }
interface BlockedAccount { username: string; displayName: string }
interface PushState { publicKey: string | null; subscribed: boolean }

@Component({
  selector: 'chat-settings',
  imports: [FormsModule, DatePipe],
  templateUrl: './chat-settings.html',
  styleUrl: './chat-settings.css',
})
export class ChatSettings {
  readonly user = input.required<string>();
  readonly account = input.required<AccountSettings>();
  readonly device = input.required<DeviceSettings>();
  readonly rooms = input.required<{ id: string; title: string; muted: boolean }[]>();
  readonly adapters = input.required<string[]>();
  readonly close = output<void>();
  readonly logout = output<void>();
  readonly deviceChange = output<DeviceSettings>();
  readonly accountChange = output<void>();
  readonly clearPreviews = output<void>();
  readonly page = signal('');
  readonly error = signal('');
  readonly done = signal('');
  readonly busy = signal(false);
  readonly devices = signal<Device[]>([]);
  readonly blocks = signal<BlockedAccount[]>([]);
  readonly push = signal<PushState | null>(null);
  readonly navigation = [
    ['profile', '编辑资料', 'Edit profile'], ['notifications', '通知和声音', 'Notifications and sounds'],
    ['privacy', '隐私和安全', 'Privacy and security'], ['devices', '设备', 'Devices'],
    ['appearance', '聊天设置', 'Chat settings'], ['storage', '数据和存储', 'Data and storage'],
    ['folders', '聊天文件夹', 'Chat folders'], ['language', '语言', 'Language'],
    ['integrations', '集成', 'Integrations'], ['about', '高级与关于', 'Advanced and about'],
  ];
  name = ''; bio = ''; current = ''; next = '';
  username = ''; blockTarget = '';
  folderName = ''; folderType = 'all'; included: string[] = []; excluded: string[] = [];
  replacingFolder: number | null = null;
  en() { const value = this.device().language; return value === 'en' || (value === 'system' && navigator.language.startsWith('en')); }
  label(zh: string, en: string) { return this.en() ? en : zh; }
  title() {
    const entry = this.navigation.find(x => x[0] === this.page());
    return entry ? this.label(entry[1], entry[2]) : this.label('设置', 'Settings');
  }
  async open(page: string) {
    this.error.set(''); this.done.set(''); this.page.set(page);
    if (page === 'profile') { this.name = this.account().profile.displayName; this.bio = this.account().profile.bio; this.username = this.user(); }
    if (page === 'devices') await this.action(() => this.loadDevices(), false);
    if (page === 'privacy') await this.action(() => this.loadBlocks(), false);
    if (page === 'notifications') await this.action(() => this.loadPush(), false);
    if (page === 'folders') this.editFolder(null);
  }
  changeDevice(key: keyof DeviceSettings, value: DeviceSettings[keyof DeviceSettings]) {
    this.deviceChange.emit({ ...this.device(), [key]: value });
  }
  saveProfile() { return this.put('profile', { displayName: this.name, bio: this.bio }); }
  rename() { return this.put('username', { username: this.username }); }

  async changeBlock(username: string, blocked: boolean) {
    await this.action(async () => {
      await this.request('/blocks', { username, blocked });
      this.blockTarget = '';
      await this.loadBlocks();
    });
  }

  private async loadBlocks() { this.blocks.set(await this.request<BlockedAccount[]>('/blocks')); }
  private async loadPush() { this.push.set(await this.request<PushState>('/push')); }

  async enablePush() {
    await this.action(async () => {
      const key = this.push()?.publicKey;
      if (!key || !('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window))
        throw new Error(this.label('此浏览器不支持后台推送。', 'Background push is unavailable in this browser.'));
      if (await Notification.requestPermission() !== 'granted')
        throw new Error(this.label('请在浏览器中允许通知。', 'Allow notifications in your browser.'));
      await navigator.serviceWorker.register('/chat-push-sw.js', { scope: '/chat' });
      const registration = await navigator.serviceWorker.ready;
      const decoded = atob(key.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(key.length / 4) * 4, '='));
      const active = await registration.pushManager.getSubscription();
      let subscription: PushSubscription;
      try {
        subscription = active ?? await registration.pushManager.subscribe({
          userVisibleOnly: true, applicationServerKey: Uint8Array.from(decoded, c => c.charCodeAt(0)),
        });
      } catch {
        throw new Error(this.label('浏览器推送服务注册失败，请检查网络后重试。', 'The browser push service rejected registration. Check the connection and try again.'));
      }
      try { await this.request('/push', { ...subscription.toJSON(), language: this.en() ? 'en' : 'zh' }); }
      catch (error) { if (!active) await subscription.unsubscribe(); throw error; }
      await this.loadPush();
    });
  }

  async disablePush() {
    await this.action(async () => {
      await this.request('/push/remove', {});
      const registration = await navigator.serviceWorker?.getRegistration('/chat');
      await (await registration?.pushManager.getSubscription())?.unsubscribe();
      await this.loadPush();
    });
  }
  ownAvatar() { return '/api/chat/avatars/' + encodeURIComponent(this.user()) + '?v=' + this.account().profile.avatarVersion; }
  async uploadAvatar(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    const form = new FormData();
    form.append('avatar', file, file.name);
    await this.put('avatar', form);
    input.value = '';
  }
  removeAvatar() { return this.put('avatar/remove', {}); }
  notifications(key: keyof AccountSettings['notifications'], value: boolean) {
    return this.put('notifications', { ...this.account().notifications, [key]: value });
  }
  privacy(value: boolean) { return this.put('privacy', { groupInvites: value }); }
  async password() {
    await this.action(async () => {
      await this.request('/password', { current: this.current, newPassword: this.next });
      this.current = ''; this.next = '';
    });
  }

  editFolder(index: number | null) {
    this.replacingFolder = index;
    const folder = index === null ? undefined : this.account().folders[index];
    this.folderName = folder?.name ?? '';
    this.folderType = folder?.kind ?? 'all';
    const visible = new Set(this.rooms().map(room => room.id));
    this.included = (folder?.include ?? []).filter(id => visible.has(id));
    this.excluded = (folder?.exclude ?? []).filter(id => visible.has(id));
  }

  saveFolder() {
    const folders = [...this.account().folders];
    const folder = { name: this.folderName, kind: this.folderType, include: this.included, exclude: this.excluded };
    if (this.replacingFolder === null) folders.push(folder);
    else folders[this.replacingFolder] = folder;
    return this.put('folders', folders, () => this.editFolder(null));
  }

  removeFolder(index: number) { return this.put('folders', this.account().folders.filter((_, i) => i !== index), () => this.editFolder(null)); }

  moveFolder(index: number, direction: number) {
    const folders = [...this.account().folders];
    const adjacent = index + direction;
    if (adjacent < 0 || adjacent >= folders.length) return;
    [folders[index], folders[adjacent]] = [folders[adjacent], folders[index]];
    void this.put('folders', folders, () => this.editFolder(null));
  }

  private put(section: string, value: object, after?: () => void) {
    return this.action(async () => {
      await this.request('/' + section, value);
      this.accountChange.emit();
      after?.();
    });
  }

  async revoke(id?: string) {
    await this.action(async () => {
      await this.request(id ? '/devices/revoke' : '/devices/revoke-others', id ? { id } : {});
      await this.loadDevices();
    });
  }
  private async loadDevices() { this.devices.set(await this.request<Device[]>('/devices')); }

  private async action(run: () => Promise<void>, confirm = true) {
    this.busy.set(true); this.error.set(''); this.done.set('');
    try { await run(); if (confirm) this.done.set(this.label('已保存', 'Saved')); }
    catch (e) { this.error.set(e instanceof Error ? e.message : String(e)); }
    finally { this.busy.set(false); }
  }

  private async request<T>(path: string, body?: object): Promise<T> {
    const response = await fetch('/api/chat/settings' + path, body ? {
      method: 'POST', headers: body instanceof FormData ? { 'X-LittleLoop-Chat': '1' } : { 'Content-Type': 'application/json', 'X-LittleLoop-Chat': '1' },
      body: body instanceof FormData ? body : JSON.stringify(body),
    } : {});
    if (!response.ok) {
      throw new Error(apiMessage(await response.text(), response.status, this.en()));
    }
    return response.status === 204 ? undefined as T : response.json();
  }
}
