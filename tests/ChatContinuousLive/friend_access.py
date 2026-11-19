"""Private LittleLoop onboarding for the isolated HTTP fixture."""

import uuid


ACCOUNTS = (
    'access_admin', 'tool_approver', 'friend_listed', 'friend_manual',
    'friend_denied', 'friend_late', 'human_peer',
)


def run(clients, request, wait, record, project, tools=False):
    admin = clients['access_admin']
    approver = clients['tool_approver']
    listed = clients['friend_listed']
    manual = clients['friend_manual']
    denied = clients['friend_denied']
    late = clients['friend_late']
    peer = clients['human_peer']

    def entries():
        return {entry['username']: entry for entry in request(admin, '/bot/access')['entries']}

    def set_listed(username, allowed):
        request(admin, '/bot/access/allowlist', dict(username=username, allowed=allowed), 204)

    def decide(username, decision):
        entry = entries()[username]
        request(admin, f"/bot/access/{entry['id']}/decision", dict(decision=decision), 204)

    def friend(who, username='littleloop'):
        return next((row for row in request(who, '/friends') if row['username'] == username), None)

    def room_for(who):
        rooms = [room['id'] for room in request(who, '/rooms') if room['type'] == 'direct' and room.get('avatarName') == 'littleloop']
        assert len(rooms) <= 1, rooms
        return rooms[0] if rooms else None

    def request_bot_friend(who, expected=204):
        return request(who, '/friends', dict(username='littleloop'), expected)

    def messages(who, room, after=0):
        return request(who, f'/rooms/{room}/messages?after={after}')

    def send(who, room, text, expected=200):
        return request(who, f'/rooms/{room}/messages', dict(clientId=str(uuid.uuid4()), text=text), expected)

    def confirm_memory(who, room, material):
        learned = send(who, room, f'以后处理{material}时，先核对版本号。')
        query = send(who, room, '查看经验')
        reply = wait(lambda: next((msg for msg in messages(who, room, query['sequence'])
                                  if msg['sender'] == 'littleloop' and msg['replyTo'] == query['sequence']), None), 15)
        assert f"消息 #{learned['sequence']}" in reply['text'] and material in reply['text'], reply

    admin_caps = request(admin, '/bot/capabilities')
    approver_caps = request(approver, '/bot/capabilities')
    assert admin_caps['canManageAccess'] and not admin_caps['canApprove']
    assert approver_caps['canApprove'] and not approver_caps['canManageAccess']
    request(approver, '/bot/access', expected=403)
    request(admin, '/bot/private-approvals', expected=403)
    request(listed, '/bot/access/allowlist', dict(username='friend_listed', allowed=True), 403)
    record('friend access administration and per-tool approval belong to different accounts')

    set_listed('friend_listed', True)
    assert entries()['friend_listed']['allowed'] and friend(listed) is None and room_for(listed) is None
    request(listed, '/bot/direct', {}, 403)
    listed_room = request_bot_friend(listed, 200)['roomId']
    assert request_bot_friend(listed, 200)['roomId'] == listed_room == room_for(listed)
    assert friend(listed)['state'] == 'friend'
    assert request(listed, '/bot/rooms/' + listed_room)['canOperate']
    assert not request(listed, '/bot/capabilities')['canOperate']
    assert request(listed, '/bot/direct', {})['id'] == listed_room
    confirm_memory(listed, listed_room, '发布说明')
    record('an allowlisted account accepts itself, reuses the same room and can use real private memory')

    request_bot_friend(manual)
    assert friend(manual)['state'] == 'outgoing' and room_for(manual) is None
    request(manual, '/bot/direct', {}, 403)
    pending = entries()['friend_manual']
    assert pending['state'] == 'pending'
    request(manual, f"/bot/access/{pending['id']}/decision", dict(decision='accept'), 403)
    decide('friend_manual', 'accept')
    manual_room = room_for(manual)
    assert manual_room and friend(manual)['state'] == 'friend'
    assert request(manual, '/bot/rooms/' + manual_room)['canOperate']
    confirm_memory(manual, manual_room, '发版记录')
    record('manual acceptance creates a usable private room without changing server configuration')

    request_bot_friend(denied)
    decide('friend_denied', 'reject')
    assert friend(denied) is None and room_for(denied) is None
    request(denied, '/bot/direct', {}, 403)
    request_bot_friend(late)
    assert friend(late)['state'] == 'outgoing' and room_for(late) is None
    set_listed('friend_late', True)
    late_room = room_for(late)
    assert late_room and friend(late)['state'] == 'friend'
    assert request(late, '/bot/rooms/' + late_room)['canOperate']
    record('rejection denies access and a late allowlist addition accepts an existing request live')

    tool_result = {}
    if tools:
        task_message = send(listed, listed_room, '任务：只将 code.txt 的 broken 改为 fixed，然后运行宿主 verify。')
        task_id = wait(lambda: next(((msg.get('card') or {}).get('taskId') for msg in messages(listed, listed_room)
                                    if msg['sequence'] == task_message['sequence']), None), 15)
        approvals = []

        def advance():
            current = request(listed, f'/bot/work/{task_id}')
            if current['status'] == 'completed':
                return current
            assert current['status'] in ('queued', 'running'), current
            pending = next((item for item in request(approver, '/bot/private-approvals') if item['id'] == task_id), None)
            if pending and pending['nonce'] not in {entry['nonce'] for entry in approvals}:
                nonce, tool, args = pending['nonce'], pending['tool'], pending['arguments']
                if not approvals:
                    assert (project / 'code.txt').read_text(encoding='utf-8').strip() == 'broken'
                assert (tool == 'project_patch' and args['path'] == 'code.txt') or (
                    tool == 'project_command' and args['command'] == 'verify'), pending
                decision = dict(nonce=nonce, value='accept')
                path = f'/bot/private-approvals/{task_id}/decision'
                request(listed, path, decision, 403)
                request(admin, path, decision, 403)
                request(approver, path, decision, 204)
                approvals.append(dict(tool=tool, nonce=nonce))
            return None

        finished = wait(advance, 120)
        assert {step['tool'] for step in approvals} == {'project_patch', 'project_command'}, approvals
        assert (project / 'code.txt').read_text(encoding='utf-8').strip() == 'fixed'
        assert any(receipt['exitCode'] == 0 for receipt in finished['execution']['receipts'])
        tool_result = dict(task=task_id, approvals=approvals, execution=finished['execution']['status'])
        record('the accepted friend still needs distinct approvals for a real Codex patch and test')

    first_message = messages(listed, listed_room)[0]
    set_listed('friend_listed', False)
    assert not request(listed, '/bot/rooms/' + listed_room)['canOperate']
    request(listed, '/bot/direct', {}, 403)
    send(listed, listed_room, '查看经验', 403)
    assert any(msg['sequence'] == first_message['sequence'] for msg in messages(listed, listed_room))
    set_listed('friend_manual', True)
    set_listed('friend_manual', False)
    assert request(manual, '/bot/rooms/' + manual_room)['canOperate']
    decide('friend_manual', 'revoke')
    assert not request(manual, '/bot/rooms/' + manual_room)['canOperate']
    send(manual, manual_room, '任务：这个请求不能入队', 403)
    record('automatic and manual grants are revoked live while existing room history remains readable')

    request(peer, '/friends', dict(username='friend_denied'), 204)
    relationship = friend(denied, 'human_peer')
    assert relationship['state'] == 'incoming'
    request(denied, f"/friends/{relationship['id']}/respond", dict(accepted=True), 204)
    assert friend(peer, 'friend_denied')['state'] == 'friend'
    record('ordinary human friend requests still work')
    return dict(mode='friend-access', friendRoom=listed_room, tools=tool_result)
