import { Injectable, computed, signal } from '@angular/core';

export type Locale = 'zh' | 'en';

export const LOCALES: { id: Locale; label: string; dateLocale: string }[] = [
  { id: 'zh', label: '简体中文', dateLocale: 'zh-CN' },
  { id: 'en', label: 'English', dateLocale: 'en-US' },
];

/**
 * English is keyed by the canonical Simplified-Chinese copy. Keeping the
 * source copy as the key makes untranslated additions fall back to Chinese
 * instead of showing a raw message id, and keeps the existing UI copy stable.
 */
const english: Record<string, string> = {
  // Shell, navigation and chat list
  '会话列表': 'Chat list',
  '主菜单': 'Main menu',
  '搜索消息': 'Search messages',
  '搜索': 'Search',
  '会话文件夹': 'Chat folders',
  '全部会话': 'All chats',
  '全部': 'All',
  '已归档': 'Archived chats',
  '返回会话列表': 'All chats',
  '搜索结果': 'Search results',
  '没有匹配的消息。': 'No matching messages.',
  '关闭搜索结果': 'Close search results',
  '频道目录': 'Channel directory',
  '收起': 'Close',
  '打开频道': 'Open channel',
  '取消订阅': 'Unsubscribe',
  '订阅频道': 'Subscribe',
  '还没有频道。': 'No channels yet.',
  '创建频道': 'Create channel',
  '频道名称': 'Channel name',
  '还没有会话。': 'No chats here yet.',
  '新建会话': 'New chat',
  '类型': 'Type',
  '私聊': 'Direct chat',
  '群聊': 'Group',
  '群名称': 'Group name',
  '成员账号': 'Member usernames',
  '多个账号用逗号分隔': 'Separate usernames with commas',
  '创建会话': 'Create chat',
  '已置顶': 'Pinned',
  '已静音': 'Muted',
  '条未读': 'unread',
  '收藏': 'Saved',
  '历史': 'History',
  '话题': 'Topic',
  '频道': 'Channel',
  '消息已删除': 'Message deleted',
  '个人收藏': 'Saved messages',
  '历史工作台': 'Workbench history',
  '新建频道': 'New channel',
  '浏览频道': 'Browse channels',
  '好友': 'Friends',
  '设置': 'Settings',
  '正在连接': 'Connecting',
  '已连接': 'Connected',
  '正在重连': 'Reconnecting',
  '已断开': 'Disconnected',

  // Sign in
  '登录聊天': 'Sign in to Chat',
  '创建账号': 'Create account',
  '登录': 'Sign in',
  '注册并登录': 'Register and sign in',
  '使用邀请码注册': 'Register with an invitation',
  '返回登录': 'Back to sign in',
  '账号': 'Username',
  '密码': 'Password',
  '邀请码': 'Invitation code',
  '请检查账号和密码，或重新登录。': 'Check your credentials or sign in again.',
  '请求失败': 'Request failed',
  '登录你的聊天账户': 'Sign in to your chat account',

  // Conversation
  '返回会话': 'Back to chats',
  '打开会话详情': 'Open chat info',
  '会话内搜索': 'Search in chat',
  '会话菜单': 'Chat menu',
  '会话设置': 'Chat settings',
  '取消置顶': 'Unpin',
  '置顶会话': 'Pin chat',
  '取消归档': 'Unarchive',
  '归档会话': 'Archive chat',
  '取消静音': 'Unmute',
  '静音会话': 'Mute chat',
  '新建话题': 'New topic',
  '返回群聊': 'Back to group',
  '停用机器人': 'Disable bots',
  '加入机器人': 'Add bots',
  '更新机器人': 'Update bots',
  '引用源码提问': 'Ask about source code',
  '关闭源码提问': 'Close source question',
  '群话题': 'Group topics',
  '搜索本会话': 'Search this chat',
  '会话搜索结果': 'Chat search results',
  '话题名称': 'Topic name',
  '创建话题': 'Create topic',
  '源码提问': 'Source question',
  '问答机器人': 'Question bot',
  '源码文件': 'Source file',
  '选择文件': 'Choose a file',
  '起始行': 'First line',
  '结束行': 'Last line',
  '问题': 'Question',
  '提交源码提问': 'Submit source question',
  '安排开发任务': 'Schedule development task',
  '来源消息': 'Source message',
  '任务机器人': 'Task bot',
  '任务目标和验收': 'Task goal and acceptance criteria',
  '提交开发任务': 'Submit development task',
  '取消安排': 'Cancel scheduling',
  '已置顶消息': 'Pinned message',
  '加载更早消息': 'Load earlier messages',
  '回复': 'Reply',
  '转发自': 'Forwarded from',
  '转发': 'Forward',
  '取消置顶消息': 'Unpin message',
  '置顶消息': 'Pin message',
  '编辑': 'Edit',
  '删除': 'Delete',
  '编辑内容': 'Edit message',
  '保存修改': 'Save changes',
  '取消编辑': 'Cancel edit',
  '确认删除': 'Delete message',
  '取消删除': 'Cancel deletion',
  '转发到': 'Forward to',
  '选择会话': 'Choose a chat',
  '确认转发': 'Forward message',
  '存到个人收藏': 'Save to Saved messages',
  '取消转发': 'Cancel forwarding',
  '开始聊天吧。': 'Start a conversation.',
  '已编辑': 'Edited',
  '下载': 'Download',
  '加载图片': 'Load image',
  '待确认': 'Unconfirmed',
  '重试发送': 'Retry sending',
  '取消回复': 'Cancel reply',
  '移除附件': 'Remove attachment',
  '添加图片或文件': 'Add a photo or file',
  '消息内容': 'Message',
  '写消息…': 'Write a message…',
  '发送': 'Send',
  '此频道仅发布者可发消息。': 'Only the publisher can post to this channel.',
  '选择一个会话，或开始新对话。': 'Select a chat or start a new conversation.',
  '已发送': 'Sent',
  '消息操作': 'Message actions',
  '复制': 'Copy',
  '移除': 'Remove',
  '消息': 'Messages',
  '新消息': 'New message',
  '关闭通知': 'Dismiss notification',
  '关闭主菜单': 'Close main menu',
  '返回': 'Back',
  '关闭': 'Close',
  '保存': 'Save',
  '取消': 'Cancel',

  // Details, friends and drawers
  '会话详情': 'Chat info',
  '关闭会话详情': 'Close chat info',
  '发布者': 'Publisher',
  '成员': 'Members',
  '成员用户名': 'Member username',
  '邀请成员': 'Invite member',
  '群主': 'Owner',
  '转让群主': 'Transfer ownership',
  '当前加载的共享媒体': 'Shared media in loaded history',
  '还没有成员。': 'No members yet.',
  '关闭好友': 'Close friends',
  '关闭好友列表': 'Close friend list',
  '好友用户名': 'Friend username',
  '添加好友': 'Add friend',
  '接受': 'Accept',
  '拒绝': 'Decline',
  '等待对方接受': 'Request sent',
  '发起私聊': 'Start private chat',
  '暂无好友或申请': 'No friends or requests yet',

  // Bot cards
  '源码引用': 'Source citation',
  'LittleLoop 任务': 'LittleLoop job',
  '源码问答': 'Source Q&A',
  '开发任务': 'Development task',
  '源码审阅': 'Source review',
  '代码补丁': 'Code patch',
  '退出码': 'Exit code',
  '失败': 'Failed',
  '审批提交失败。': 'Could not submit the decision.',
  '已完成': 'Completed',
  '待核对': 'Needs reconciliation',
  '执行中': 'Running',
  '排队中': 'Queued',
  '机器人已停用': 'Bot disabled',
  '核心拒绝': 'Rejected by the core',
  '此审批已处理或已失效。': 'This approval was already used or has expired.',
  '源码已变更': 'Source has changed',
  '验收未通过': 'Verification did not pass',
  '允许本次': 'Allow once',
  '审阅结果': 'Review result',
  '执行报告': 'Execution report',
  '等待审批': 'Awaiting approval',
  '任务已停止，请核对执行记录。': 'The job stopped. Check its execution record.',
  '源码问答未完成。': 'The source question did not complete.',
  '进程曾中断，请核对执行记录。': 'The worker stopped. Check its execution record.',
  '文件已变化，请重新引用后安排任务。': 'The source changed. Cite it again before scheduling.',

  // Auth
  '思考、协作，一步步来。': 'Think and collaborate, one step at a time.',
  '私聊、群和话题在同一处。': 'Direct chats, groups and topics in one place.',
  '消息与开发任务保持关联。': 'Messages stay linked to development tasks.',
  '历史记录可随时继续。': 'Pick up the history whenever you return.',
  '使用邀请码创建新账号。': 'Create a new account with an invitation.',
  'LittleLoop 聊天': 'LittleLoop Chat',

  // Extra labels
  '关闭设置面板': 'Dismiss settings panel',
  '成员操作': 'Member actions',
  '暂无话题': 'No topics yet',
  '暂无共享媒体': 'No shared media yet',
  '自动': 'Auto',
  '删除后，此消息将在会话中显示为已删除。': 'The message will stay in the chat, marked as deleted.',

  // Settings
  '返回设置上级': 'Back to settings',
  '关闭设置': 'Close settings',
  '编辑资料': 'Edit profile',
  '通知和声音': 'Notifications and sounds',
  '隐私和安全': 'Privacy and security',
  '设备': 'Devices',
  '聊天设置': 'Chat settings',
  '数据和存储': 'Data and storage',
  '聊天文件夹': 'Chat folders',
  '语言': 'Language',
  '集成': 'Integrations',
  '高级与关于': 'Advanced and about',
  '我的头像': 'My avatar',
  '头像图片': 'Profile picture',
  '移除头像': 'Remove avatar',
  '显示名': 'Display name',
  '个人简介': 'Bio',
  '保存资料': 'Save profile',
  '用户名': 'Username',
  '保存用户名': 'Save username',
  '私聊通知': 'Direct messages',
  '群聊和话题通知': 'Groups and topics',
  '频道通知': 'Channels',
  '消息预览': 'Message preview',
  '应用内提示音': 'In-app sound',
  '后台推送': 'Background push',
  '此设备已订阅': 'This device is subscribed',
  '此设备未订阅': 'This device is not subscribed',
  '关闭此设备推送': 'Disable push on this device',
  '启用此设备推送': 'Enable push on this device',
  '已静音会话': 'Muted conversations',
  '允许加入他人创建的新群': 'Allow invitations to new groups',
  '屏蔽名单': 'Blocked accounts',
  '屏蔽用户': 'Block account',
  '解除屏蔽': 'Unblock',
  '当前密码': 'Current password',
  '新密码': 'New password',
  '修改密码': 'Change password',
  '当前设备': 'Current device',
  '其他设备': 'Other device',
  '结束此会话': 'End this session',
  '结束其他会话': 'End other sessions',
  '主题': 'Theme',
  '跟随系统': 'System',
  '浅色': 'Light',
  '深色': 'Dark',
  '强调色': 'Accent color',
  '蓝': 'Blue',
  '紫': 'Purple',
  '青': 'Teal',
  '聊天背景': 'Chat background',
  '薄荷': 'Mint',
  '沙色': 'Sand',
  '纯色': 'Plain',
  '文字大小': 'Text size',
  '小': 'Small',
  '默认': 'Default',
  '大': 'Large',
  'Enter 发送': 'Send with Enter',
  '界面动画': 'Animations',
  '自动加载图片': 'Load images automatically',
  '清除手动预览': 'Clear manual previews',
  '已清除手动图片预览': 'Image previews cleared',
  '文件通过消息中的下载按钮保存。': 'Use the download button on a message to save its file.',
  '新建文件夹': 'New folder',
  '文件夹名称': 'Folder name',
  '会话类型': 'Conversation type',
  '群和话题': 'Groups and topics',
  '未读': 'Unread',
  '额外包含': 'Also include',
  '排除': 'Exclude',
  '保存文件夹': 'Save folder',
  '界面语言': 'Interface language',
  '请从群或话题菜单管理机器人。审批权限由部署管理员配置。': 'Manage bots from the group or topic menu. Deployment administrators configure approvals.',
  'Enter 可在聊天设置中配置；Shift+Enter 换行；Esc 关闭菜单。': 'Configure Enter in Chat settings. Shift+Enter adds a line. Esc closes menus.',
  '退出登录': 'Log out',
  '已保存': 'Saved',
  '此浏览器不支持后台推送。': 'Background push is unavailable in this browser.',
  '请在浏览器中允许通知。': 'Allow notifications in your browser.',
  '浏览器推送服务注册失败，请检查网络后重试。': 'The browser push service rejected registration. Check the connection and try again.',
  '上移': 'Move up',
  '下移': 'Move down',
  '没有可发送的频道': 'No channel available',
  '订阅后的频道会显示在这里。': 'Subscribed channels will appear here.',
  '角色': 'Role',
  '管理员': 'Administrator',
  '成员权限': 'Member permissions',
  '机器人': 'Bot',
  '已启用机器人': 'Enabled bots',
  '作用范围': 'Scope',
  '操作员与审批权限说明': 'Operator and approval permissions',
  '版本': 'Version',
  '使用帮助': 'Help',
  '键盘快捷键': 'Keyboard shortcuts',
  '通知预览': 'Notification preview',
  '声音': 'Sound',

  // Errors and empty states
  '无法加载会话。': 'Could not load chats.',
  '无法加载消息。': 'Could not load messages.',
  '没有更多消息。': 'No more messages.',
  '网络不可用，正在重试。': 'Network unavailable. Retrying.',
  '消息发送失败。': 'The message could not be sent.',
  '此操作当前不可用。': 'This action is unavailable right now.',
};

const serverZh: Record<string, string> = {
  invalid_username: '用户名必须是 3–32 位字母、数字或下划线，且不能使用保留名称。',
  username_taken: '此用户名已被保留。',
  invalid_block_target: '请选择有效的其他账号。',
  communication_unavailable: '当前无法与该账号私聊或发起群邀请。',
  invalid_push_subscription: '浏览器推送订阅无效。',
};
const serverEn: Record<string, string> = {
  invalid_username: 'Use 3–32 letters, digits or underscores and an unreserved username.',
  username_taken: 'This username is reserved.',
  invalid_block_target: 'Choose another valid account.',
  communication_unavailable: 'Direct contact or a new group invitation is unavailable.',
  invalid_push_subscription: 'The browser push subscription is invalid.',
};

export type LocaleVars = Record<string, string | number>;

@Injectable({ providedIn: 'root' })
export class ChatI18n {
  readonly locale = signal<Locale>(detectLocale());
  readonly dateLocale = computed(() => this.locale() === 'en' ? 'en-US' : 'zh-CN');
  readonly label = computed(() => this.locale() === 'en' ? 'English' : '简体中文');

  use(locale: Locale) {
    this.locale.set(locale);
    if (typeof document !== 'undefined') document.documentElement.lang = locale === 'en' ? 'en' : 'zh-CN';
  }

  t(value: string, vars?: LocaleVars): string {
    let text = this.locale() === 'en' ? english[value] ?? value : value;
    if (vars) for (const [key, replacement] of Object.entries(vars)) text = text.replaceAll('{' + key + '}', String(replacement));
    return text;
  }

  date(value: string, time = false): string {
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) return value;
    return new Intl.DateTimeFormat(this.dateLocale(), time
      ? { hour: '2-digit', minute: '2-digit', hour12: false }
      : { year: 'numeric', month: 'short', day: 'numeric' }).format(parsed);
  }

  number(value: number): string {
    return new Intl.NumberFormat(this.dateLocale()).format(value);
  }

  apiMessage(raw: string, status: number): string {
    if (status === 401) return this.t('请检查账号和密码，或重新登录。');
    try {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed.code === 'string' && serverZh[parsed.code])
        return (this.locale() === 'en' ? serverEn : serverZh)[parsed.code];
      if (typeof parsed === 'string' && this.locale() !== 'en') return parsed;
    } catch {
      if (this.locale() !== 'en' && raw && !raw.startsWith('<')) return raw;
    }
    return this.t('请求失败') + ' (' + status + ')';
  }
}

function detectLocale(): Locale {
  if (typeof navigator === 'undefined') return 'zh';
  const primary = navigator.languages?.[0] ?? navigator.language ?? '';
  return primary.toLowerCase().startsWith('en') ? 'en' : 'zh';
}
