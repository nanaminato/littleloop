export interface Room {
  id: string;
  type: 'direct' | 'group' | 'saved' | 'legacy' | 'channel' | 'topic';
  title: string;
  latest: number;
  read: number;
  unread: number;
  revision: number;
  pinned: boolean;
  archived: boolean;
  muted: boolean;
  parentId?: string;
  canPost: boolean;
  canManage: boolean;
  preview: string;
  lastSent?: string;
  avatarName?: string;
  avatarVersion?: string;
  previewDeleted: boolean;
  pinnedMessage?: number | null;
}

export interface Member {
  name: string;
  displayName: string;
  robot: boolean;
  owner: boolean;
}

export interface Friend {
  id: string;
  username: string;
  displayName: string;
  state: 'friend' | 'incoming' | 'outgoing';
}

export interface Channel {
  id: string;
  title: string;
  subscribed: boolean;
  owner: boolean;
}

export interface Attachment {
  id: string;
  name: string;
  mime: string;
  size: number;
}

export interface SourceCitation {
  path: string;
  start: number;
  end: number;
  sha256: string;
  text: string;
}

export interface MessageCard {
  kind: 'citation' | 'question' | 'task';
  source?: SourceCitation;
  taskId?: string;
}

export interface ChatMessage {
  roomId: string;
  sequence: number;
  clientId: string;
  sender: string;
  text: string;
  replyTo: number | null;
  attachment: Attachment | null;
  edited: boolean;
  deleted: boolean;
  forwardedFrom: string | null;
  card?: MessageCard;
  sentAt?: string;
}

export interface Outgoing {
  clientId: string;
  text: string;
  replyTo: number | null;
  attachmentId?: string;
}

export interface Notice {
  id: string;
  title: string;
  preview?: string;
}
