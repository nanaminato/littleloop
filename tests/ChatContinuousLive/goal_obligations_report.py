"""Score obligation capture and evidence offline using the unchanged endurance fixture."""

import argparse
from collections import Counter
from decimal import Decimal
import json
from pathlib import Path
import sqlite3

from autonomy_reliability_batch import DATA, REPO, read_json, write_json
from autonomy_reliability_report import business_timer_honored, measure


def read_goal(root, goal):
    path = root / 'chat.db'
    if goal is None or not path.is_file():
        return None, None, []
    with sqlite3.connect(f'{path.as_uri()}?mode=ro', uri=True) as db:
        row = db.execute('SELECT seq,goal FROM chat_bot_work WHERE id=?', (goal,)).fetchone()
        if row is None or row[1] is None:
            return None, None, []
        history = db.execute('SELECT action,snapshot FROM chat_goal_events WHERE work_id=? ORDER BY id', (goal,))
        snapshots = [(action, json.loads(snapshot)) for action, snapshot in history]
        return row[0], json.loads(row[1]), snapshots


def rate(numerator, denominator):
    return {'observed': numerator, 'eligible': denominator, 'rate': numerator / denominator if denominator else None}


def expected_source_groups(trace, initial_sequence):
    """Fixture assertions. Product obligation descriptions and statuses come from the Bot."""
    groups = {
        'orderedHandoffs': initial_sequence,
        'primaryFallback': initial_sequence,
        'timedCheck': initial_sequence,
        'finalValue': initial_sequence,
    }
    supplement = (trace.get('revisionDuringNativeDispatch') or {}).get('userSequence')
    if supplement is not None:
        groups['supplementalNotice'] = supplement
    return groups


def matched_groups(obligation, trace, expected):
    description = obligation.get('description', '').lower()
    full_handoff = ('codex' in description and 'deepseek' in description
        and (('b01' in description and 'b08' in description) or any(term in description for term in ('eight', '八'))))
    matches = {
        'orderedHandoffs': full_handoff,
        'primaryFallback': 'b01-primary.txt' in description and 'b01-backup.txt' in description,
        'timedCheck': ('b04' in description and 'b05' in description and
            (obligation.get('delaySeconds') == 30 or '30' in description or '三十' in description)),
        'finalValue': 'b08' in description and any(
            term in description for term in ('末四', '后四', '四位', '4位', 'last four', 'last 4')),
        'supplementalNotice': trace['finalNoticeStamp'].lower() in description,
    }
    return {name for name, sequence in expected.items() if obligation['sourceSequence'] == sequence and matches[name]}


def capture_signals(tracked, trace, expected):
    present = {name: [] for name in expected}
    for obligation in tracked:
        for name in matched_groups(obligation, trace, expected):
            present[name].append(obligation['id'])

    # The Bot can put separate clauses in separate conditions from the same original message.
    initial = {entry['id']: entry['description'].lower() for entry in tracked
        if entry['sourceSequence'] == expected['orderedHandoffs']}
    bounds = [key for key, text in initial.items() if 'b01' in text and 'b08' in text]
    handoffs = [key for key, text in initial.items() if 'codex' in text and 'deepseek' in text]
    if not present['orderedHandoffs'] and bounds and handoffs:
        present['orderedHandoffs'] = list(dict.fromkeys(bounds + handoffs))
    primary = [key for key, text in initial.items() if 'b01-primary.txt' in text]
    fallback = [key for key, text in initial.items() if 'b01-backup.txt' in text]
    if not present['primaryFallback'] and primary and fallback:
        present['primaryFallback'] = list(dict.fromkeys(primary + fallback))
    return present


def goal_events(trace):
    goal_id = trace.get('task')
    return [event for event in trace.get('events', []) if event['observation'].get('goalId') == goal_id] if goal_id else []


def ordered_handoff_evidence(trace):
    stages = trace.get('metrics', {}).get('verifiedStages', {})
    ordered = [stages.get(f'{batch}/{provider}')
        for batch in trace.get('expectedMarkers', {}) for provider in ('deepseek', 'codex')]
    return ordered if ordered and all(event_id is not None for event_id in ordered) and ordered == sorted(set(ordered)) else []


def due_timer(trace, events, wait_id=None):
    """Find the single actual business timer independently of the obligation statuses."""
    events = list(events)
    timers = [event for event in events if event['kind'] == 'Timer'
        and event['observation'].get('status') != 'decision-retry']
    if len(timers) != 1:
        return None
    timer = timers[0]
    stages = trace.get('metrics', {}).get('verifiedStages', {})
    b04_review = stages.get('B04/codex')
    if not b04_review or b04_review >= timer['id']:
        return None
    timer_data = timer['observation']
    if not any(
        row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Wait'
        and row['detail'].get('wakeAfterSeconds') == 30
        and row['observation'].get('goalVersion') == timer_data.get('goalVersion')
        and b04_review < row['id'] < timer['id'] and (wait_id is None or row['id'] == wait_id)
        for row in events
    ):
        return None
    return timer


def valid_evidence(obligation, assessment, events, notices, version):
    """Check persisted references without deciding the natural-language meaning."""
    if assessment.get('id') != obligation['id']:
        return False
    if assessment.get('completionNotice'):
        return any(notice['observation'].get('goalVersion') == version for notice in notices)
    if assessment.get('checkId'):
        check = obligation.get('check') or {}
        return bool(check.get('observed') and check.get('id') == assessment['checkId'] and
            any(event['kind'] == 'Timer' and timer_matches_check(event, obligation) for event in events.values()))
    event = events.get(assessment.get('eventId'))
    if event is None:
        return False
    observed = event['observation']
    evidence_version = observed.get('goalVersion')
    if evidence_version is None or not obligation['createdVersion'] <= evidence_version <= version:
        return False
    dispatch = assessment.get('dispatchId')
    if not dispatch or observed.get('dispatchId') != dispatch:
        return False
    expected_status = assessment.get('expectedToolStatus')
    if expected_status is not None:
        return (expected_status == 'failed' and obligation['kind'].lower() in ('behavior', 'constraint')
            and event['kind'] == 'ToolResult' and observed.get('status') == expected_status)
    if event['kind'] == 'ToolResult':
        return observed.get('status') == 'completed'
    if event['kind'] != 'HarnessReceipt' or observed.get('status') != 'returned':
        return False
    return not any(candidate['kind'] == 'ToolResult' and candidate['observation'].get('dispatchId') == dispatch
        and candidate['observation'].get('status') in ('failed', 'denied', 'error', 'declined')
        for candidate in events.values())


def timer_matches_check(timer, obligation):
    check = obligation.get('check')
    if not check:
        return False
    observed = timer['observation']
    return (observed.get('status') == 'due' and observed.get('checkId') == check['id'] and
        observed.get('wakeObligationId') == obligation['id'] and observed.get('goalVersion') == check['goalVersion'] and
        observed.get('wakeAtUnixMilliseconds') == check['dueAtUnixMilliseconds'])


def semantic_evidence(obligation, assessment, groups, trace, events, notices):
    """Return only checks this fixture can decide. Unclassified semantic claims stay unscored."""
    if 'timedCheck' in groups:
        timer = due_timer(trace, events.values())
        if not timer or ('B05/deepseek' in trace.get('metrics', {}).get('verifiedStages', {})
                and not business_timer_honored(trace)):
            return False
        check = assessment.get('checkId')
        if obligation['kind'].lower() != 'time':
            return (assessment.get('eventId') or 0) > timer['id']
        return (timer['id'] == assessment.get('eventId')
            or (check is not None and timer['observation'].get('checkId') == check))
    if 'supplementalNotice' in groups and assessment.get('completionNotice'):
        return any((row['detail'].get('message') or '').rstrip('。！\x60 ').endswith(trace['finalNoticeStamp'])
            for row in notices)
    if 'finalValue' in groups and assessment.get('completionNotice'):
        marker = trace['expectedMarkers']['B08'][-4:]
        return any(marker in row['detail'].get('message', '') for row in notices)
    if 'orderedHandoffs' in groups and assessment.get('eventId'):
        ordered = ordered_handoff_evidence(trace)
        return bool(ordered and ordered[-1] <= assessment['eventId'])
    return None


def measure_trial(trace, initial_sequence, goal, snapshots):
    events = goal_events(trace)
    event_index = {event['id']: event for event in events}
    notices = [event for event in events if event['kind'] == 'BotDecision'
        and event['detail'].get('action') == 'Complete' and event['detail'].get('message')]
    expected = expected_source_groups(trace, initial_sequence)
    tracked = (goal or {}).get('obligations')
    capture = {'available': tracked is not None, 'pendingSourceSequences': (goal or {}).get('pendingRequirementSources') or []}
    assessments = []
    scored = set()
    if tracked is not None:
        active = [o for o in tracked if o['status'].lower() not in ('superseded', 'withdrawn')]
        present = capture_signals(active, trace, expected)
        timed = [o for o in active if o['id'] in present['timedCheck']]
        enforceable = [o['id'] for o in timed if o['kind'].lower() == 'time' and o.get('delaySeconds') == 30]
        capture.update(sourceGroups=present, withoutCaptureSignal=[key for key, ids in present.items() if not ids],
            timeConditionIdsWithDueCheck=enforceable)
        for _, snapshot in snapshots:
            for obligation in snapshot.get('obligations') or []:
                if obligation['status'].lower() != 'satisfied':
                    continue
                assessment = obligation.get('assessment') or {}
                key = (obligation['id'], json.dumps(assessment, sort_keys=True))
                if key in scored:
                    continue
                scored.add(key)
                matches = matched_groups(obligation, trace, expected)
                version = snapshot['version']
                has_evidence = valid_evidence(obligation, assessment, event_index, notices, version)
                semantic = semantic_evidence(
                    obligation, assessment, matches, trace, event_index,
                    [notice for notice in notices if notice['observation'].get('goalVersion') == version])
                assessments.append({
                    'obligationId': obligation['id'],
                    'captureGroups': sorted(matches),
                    'evidenceExists': has_evidence,
                    'fixtureSemanticCheck': semantic,
                })
    restart = trace.get('restart')
    restart_result = None
    if restart is not None:
        timer = due_timer(trace, events, restart['waitDecision'])
        recovered_timer = bool(timer and restart['unixTime'] * 1000 < restart['dueUnixMilliseconds'] and
            timer['observation'].get('wakeAtUnixMilliseconds') == restart['dueUnixMilliseconds'])
        recovered_check = None
        if tracked is not None:
            if recovered_timer:
                saved_before_due = any(action == 'Wait' and timer_matches_check(timer, obligation)
                    and not obligation['check']['observed'] for action, snapshot in snapshots
                    for obligation in snapshot.get('obligations') or [])
                recovered_check = saved_before_due and any(timer_matches_check(timer, obligation)
                    and obligation['check']['observed'] for obligation in tracked)
            else:
                recovered_check = False
        restart_result = {
            'persistedTimer': recovered_timer, 'persistedObligationCheck': recovered_check,
            'nativeSessions': trace['metrics']['recovery']['sessionRecovery'],
        }
    return {
        'capture': capture,
        'assessments': assessments,
        'restart': restart_result,
        'completions': len([event for event in events if event['kind'] == 'BotDecision'
            and event['detail'].get('action') == 'Complete']),
        'allHandoffEvidenceReady': bool(ordered_handoff_evidence(trace)),
        'revisionReceived': 'supplementalNotice' in expected,
        'businessTimerHonored': bool(trace.get('metrics') and business_timer_honored(trace)),
    }


def extended_report(batch, rates):
    result = measure(batch, rates)
    all_assessments = []
    restart_attempts = []
    capture_counts = Counter()
    completions = revisions_received = verified_workloads = 0
    for trial in result['trials']:
        root = (REPO / trial['evidence']).resolve(strict=True)
        trace = read_json(root / 'endurance-trace.json')
        seq, goal, snapshots = read_goal(root, trace.get('task'))
        trial['obligations'] = observation = measure_trial(trace, seq, goal, snapshots)
        completions += observation['completions']
        revisions_received += observation['revisionReceived']
        verified_workloads += observation['allHandoffEvidenceReady']
        if observation['capture']['available']:
            capture_counts.update(eligible=len(observation['capture']['sourceGroups']),
                missing=len(observation['capture']['withoutCaptureSignal']))
            if observation['capture']['sourceGroups']['timedCheck']:
                capture_counts.update(timeCaptured=1,
                    timeWithoutDueCheck=not observation['capture']['timeConditionIdsWithDueCheck'])
        all_assessments.extend(observation['assessments'])
        if observation['restart'] is not None:
            restart_attempts.append(observation['restart'])
    semantic_checks = [assessment for assessment in all_assessments if assessment['fixtureSemanticCheck'] is not None]
    with_obligation_restart = [entry for entry in restart_attempts if entry['persistedObligationCheck'] is not None]
    result['requirementOracles'] = {
        'strictPass': rate(result['strictPasses'], result['trialCount']),
        'correctDeliveredResult': rate(result['verifiedDeliverables'], result['trialCount']),
        'verifiedWorkloadEvidence': rate(verified_workloads, result['trialCount']),
        'incorrectCommittedCompletion': rate(result['wrongCompletions'], completions),
        'missedBusinessTimerAtCompletion': rate(result['completionsWithMissedBusinessTimer'], completions),
        'receivedButUnpersistedRevision': rate(
            revisions_received - sum(trial['revisionApplied'] for trial in result['trials']), revisions_received),
        'captureSignalMissing': rate(capture_counts['missing'], capture_counts['eligible']),
        'capturedTimeWithoutDueCheck': rate(capture_counts['timeWithoutDueCheck'], capture_counts['timeCaptured']),
        'satisfiedWithoutLinkedEvidence': rate(sum(not a['evidenceExists'] for a in all_assessments), len(all_assessments)),
        'satisfiedButFixtureDisagrees': rate(sum(not a['fixtureSemanticCheck'] for a in semantic_checks), len(semantic_checks)),
        'timerRecovery': rate(sum(entry['persistedTimer'] for entry in restart_attempts), len(restart_attempts)),
        'obligationCheckRecovery': rate(sum(entry['persistedObligationCheck'] for entry in with_obligation_restart),
            len(with_obligation_restart)),
        'nativeSessionRecovery': rate(sum(entry['nativeSessions']['successful'] for entry in restart_attempts),
            sum(entry['nativeSessions']['attempted'] for entry in restart_attempts)),
    }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('candidate', type=Path, nargs='?')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rates-per-million', nargs=3, type=Decimal, metavar=('CACHE_HIT', 'CACHE_MISS', 'OUTPUT'))
    args = parser.parse_args()
    if args.rates_per_million and any(not rate.is_finite() or rate < 0 for rate in args.rates_per_million):
        parser.error('rates must be finite and non-negative')
    output = args.output.resolve()
    if not output.is_relative_to(DATA):
        parser.error('--output must be beneath data/')
    baseline = read_json(args.baseline.resolve(strict=True))
    report = {'fingerprints': baseline['fingerprints'], 'baseline': extended_report(baseline, args.rates_per_million)}
    if args.candidate:
        candidate = read_json(args.candidate.resolve(strict=True))
        if candidate.get('fingerprints') != baseline.get('fingerprints'):
            parser.error('baseline and candidate driver, fixture or configuration differ')
        if baseline.get('assemblyHashes') == candidate.get('assemblyHashes'):
            parser.error('use separate before and after builds')
        report['candidate'] = extended_report(candidate, args.rates_per_million)
    write_json(output, report)


if __name__ == '__main__':
    main()
