"""Alternate real providers on a single isolated chat worker across restart boundaries."""
import json
import uuid
from soak_metrics import atomic_json


def verify_alternating(project, codex_project, evidence, room, owner, approver, request, send, start, stop, wait, log):
    anchor = send('Alternating provider restart fixture')
    results = []
    providers = (('deepseek_test', project), ('littleloop', codex_project))
    for boundary in ('unanswered', 'paused', 'answered-unknown'):
        for provider, fixture in providers:
            code = fixture / 'code.txt'
            marker = fixture / '.littleloop-temp/test-ran'
            marker.unlink(missing_ok=True)
            code.write_text('broken', encoding='utf-8')
            message = request(owner, f'/bot/rooms/{room}/schedule', dict(
                clientId=str(uuid.uuid4()), sequence=anchor['sequence'], provider=provider,
                goal='先用 project_read 读取 code.txt。如果它是 broken，用 project_patch 精确改为 fixed。然后申请 project_command 执行 verify。文件已经是 fixed 时直接申请 verify。仅使用宿主工具。'), 202)
            task = message['card']['taskId']
            folder = evidence / 'bot' / task.replace('-', '')

            def state():
                current = request(approver, f'/bot/work/{task}')
                assert current['provider'] == provider and current['status'] == current['goal']['status'], current
                return current

            def approval(tool):
                def pending():
                    current = state()
                    assert current['status'] not in ('needs-reconciliation', 'waiting-input', 'failed', 'refused', 'completed'), current
                    a = (current.get('execution') or {}).get('approval')
                    if a:
                        assert a['parameters']['tool'] == tool, a
                        return a
                return wait(pending)

            def decide(a, expected=204):
                request(approver, f'/bot/work/{task}/decision', dict(nonce=a['nonce'], value='accept'), expected)

            old = approval('project_patch')
            assert old['parameters']['arguments']['path'] == 'code.txt'
            thread = state()['execution']['threadId']
            acp_path = folder / 'acp/deepseek-session.json'
            acp = json.loads(acp_path.read_text(encoding='utf-8')) if provider == 'deepseek_test' else None
            assert code.read_text() == 'broken' and not marker.exists()
            if boundary == 'paused':
                decide(old)
                old = approval('project_command')
                assert code.read_text() == 'fixed' and not marker.exists()
                send('暂停', message['sequence'])
                wait(lambda: state()['status'] == 'paused' and state()['execution']['status'] == 'suspended')
            stop()
            if boundary == 'answered-unknown':
                # An accepted answer on disk with no terminal receipt is the ambiguous crash window.
                atomic_json(folder / ('answer-' + old['nonce'] + '.json'), dict(nonce=old['nonce'], decision='accept'))
            start(f'{provider}-{boundary}-restart', True)
            expected = 'needs-reconciliation' if boundary == 'answered-unknown' else 'paused'
            recovered = wait(lambda: (value if (value := state())['status'] == expected else None), 20)
            decide(old, 409)
            assert not recovered['execution']['approval'] and not marker.exists()
            assert code.read_text() == ('fixed' if boundary == 'paused' else 'broken')
            if boundary == 'answered-unknown':
                send('恢复', message['sequence'])
                wait(lambda: any('不能直接恢复' in m['text'] for m in request(owner, f'/rooms/{room}/messages')), 20)
                assert state() == recovered and code.read_text() == 'broken' and not marker.exists()
                results.append(dict(provider=provider, task=task, boundary=boundary, result=recovered))
                log(f'PASS {provider} answered but unconfirmed approval requires reconciliation')
                continue

            send('恢复', message['sequence'])
            if boundary == 'unanswered':
                new = approval('project_patch')
                assert new['nonce'] != old['nonce'] and code.read_text() == 'broken'
                decide(new)
            new = approval('project_command')
            assert new['nonce'] != old['nonce'] and new['parameters']['arguments'] == {'command': 'verify'}
            assert code.read_text() == 'fixed' and not marker.exists()
            decide(new)

            def complete():
                current = state()
                assert current['status'] not in ('needs-reconciliation', 'waiting-input', 'failed', 'refused'), current
                return current if current['status'] == 'completed' else None

            result = wait(complete)
            assert result['execution']['threadId'] == thread
            if acp is not None:
                assert json.loads(acp_path.read_text(encoding='utf-8')) == acp
            assert marker.read_text().splitlines() == ['verified']
            assert result['execution']['changes'] == {'code.txt': '- broken\n+ fixed\n'}
            receipts = result['execution']['receipts']
            assert len(receipts) == 1 and receipts[0]['id'] == 'verify' and receipts[0]['exitCode'] == 0
            for prior in results:
                assert request(approver, f"/bot/work/{prior['task']}") == prior['result']
            results.append(dict(provider=provider, task=task, boundary=boundary, result=result))
            stop(); start(f'{provider}-{boundary}-completed-restart', True)
            assert state() == result and marker.read_text().splitlines() == ['verified']
            log(f'PASS {provider} {boundary} restart, fresh approval, one patch and one test receipt')
    return results
