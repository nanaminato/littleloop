"""Summarize live reliability trials without changing the frozen acceptance driver."""

import argparse
from collections import Counter
from decimal import Decimal
import json
from pathlib import Path
import sqlite3

from autonomy_reliability_batch import DATA, REPO, read_json, write_json


def audit(events):
    calls = []
    invalid = 0
    repaired = repair_attempted = normalized = 0
    stale_decisions = retry_waits = 0
    categories = Counter()
    codes = Counter()
    pending_retries = set()
    recovered_retries = set()
    for event in events:
        if event['kind'] not in ('BotDecision', 'DecisionAudit'):
            continue
        detail = event['detail']
        source = detail.get('cognitionSource') or {}
        attempts = source.get('modelCalls') or []
        calls.extend(attempts)
        rejected = [attempt for attempt in attempts if attempt.get('outcome') == 'invalid-decision']
        invalid += len(rejected)
        accepted = [attempt for attempt in attempts if attempt.get('outcome') == 'accepted']
        changes = [attempt for attempt in attempts if (attempt.get('draft') or {}).get('repairs')]
        if rejected or changes:
            repair_attempted += 1
            applied = event['kind'] == 'BotDecision' and accepted and not source.get('engine', '').startswith('host/')
            repaired += bool(applied)
            normalized += bool(applied and any(attempt in changes for attempt in accepted))
        for attempt in rejected:
            error = attempt.get('error') or {}
            categories[error.get('category', 'not-recorded')] += 1
            codes[error.get('code', 'not-recorded')] += 1
        if event['kind'] == 'DecisionAudit' and detail.get('error'):
            category = detail['error'].get('category', 'not-recorded')
            categories[category] += 1
            codes[detail['error'].get('code', 'not-recorded')] += 1
            stale_decisions += category == 'goal-version-conflict'
        retry_waits += source.get('engine') == 'host/decision-retry'
        observation = event['observation']
        if source.get('engine') == 'host/decision-retry':
            pending_retries.add((observation.get('goalId'), observation.get('goalVersion')))
        if (event['kind'] == 'BotDecision' and accepted and not source.get('engine', '').startswith('host/')
                and detail.get('triggerKind') == 'Timer'):
            key = (observation.get('goalId'), detail.get('triggerGoalVersion'))
            if key in pending_retries:
                recovered_retries.add(key)
    return calls, {
        'invalidDrafts': invalid,
        'repairEvaluations': {'succeeded': repaired, 'attempted': repair_attempted, 'normalizedAndAccepted': normalized},
        'structuredErrorCategories': dict(categories),
        'structuredErrorCodes': dict(codes),
        'staleDecisions': stale_decisions,
        'automaticRetryWaits': retry_waits,
        'successfulAutomaticRetries': len(recovered_retries),
    }


def status(root, goal):
    db_path = root / 'chat.db'
    if goal is None or not db_path.is_file():
        return None, None
    with sqlite3.connect(f'{db_path.as_uri()}?mode=ro', uri=True) as db:
        row = db.execute('SELECT status,input FROM chat_bot_work WHERE id=?', (goal,)).fetchone()
    if row:
        wake = (json.loads(row[1]).get('loop') or {}).get('wake')
        return row[0], wake
    return None, None


def cost(calls, rates):
    keys = ('cachedInputTokens', 'uncachedInputTokens', 'outputTokens')
    amounts = Counter()
    covered = Counter()
    prices = []
    for call in calls:
        usage = call.get('usage') or {}
        for key in (*keys, 'inputTokens', 'totalTokens'):
            value = usage.get(key)
            if isinstance(value, int):
                amounts[key] += value
                covered[key] += 1
        if rates and all(isinstance(usage.get(key), int) for key in keys):
            prices.append(sum(rate * usage[key] for rate, key in zip(rates, keys)) / Decimal(1_000_000))
    return {
        'apiAttempts': len(calls),
        'outcomes': dict(Counter(call.get('outcome', 'not-recorded') for call in calls)),
        'tokens': dict(amounts),
        'callsWithTokenCount': dict(covered),
        'pricedCalls': len(prices),
        'estimatedUsdForPricedCalls': str(sum(prices)) if prices else None,
        'ratesPerMillionUsd': dict(zip(keys, map(str, rates))) if rates else None,
        'billedAmount': None,
    }


def business_timer_honored(trace):
    goal = trace.get('task')
    stages = trace['metrics'].get('verifiedStages', {})
    last_review = stages.get('B04/codex')
    resumed_read = stages.get('B05/deepseek')
    if goal is None or last_review is None or resumed_read is None:
        return False
    events = [row for row in trace.get('events', []) if row['observation'].get('goalId') == goal]
    timers = [row for row in events if row['kind'] == 'Timer' and row['observation'].get('status') != 'decision-retry']
    if len(timers) != 1:
        return False
    timer = timers[0]
    version = timer['observation'].get('goalVersion')
    dispatch = next((row['observation'].get('dispatchId') for row in events if row['id'] == resumed_read), None)
    matching_plan = any(
        row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Wait'
        and row['detail'].get('wakeAfterSeconds') == 30 and row['observation'].get('goalVersion') == version
        and last_review < row['id'] < timer['id'] for row in events)
    resumed_after_timer = any(
        row['kind'] == 'BotDecision' and row['detail'].get('action') == 'Delegate'
        and dispatch and row['observation'].get('dispatchId') == dispatch
        and timer['id'] < row['id'] < resumed_read for row in events)
    return last_review < timer['id'] < resumed_read and matching_plan and resumed_after_timer


def revision_applied(trace):
    revision = trace.get('revisionDuringNativeDispatch') or {}
    sequence = revision.get('userSequence')
    return sequence is not None and any(
        row['kind'] == 'BotDecision' and row['observation'].get('goalId') == trace.get('task')
        and row['observation'].get('sequence') == sequence and row['detail'].get('action') == 'Revise'
        for row in trace.get('events', []))


def measure(batch, rates):
    trials = []
    calls = []
    categories = Counter()
    codes = Counter()
    for indexed in batch['trials']:
        root = (REPO / indexed['evidence']).resolve(strict=True)
        if not root.is_relative_to(DATA):
            raise ValueError('Trial evidence must be in data/')
        trace = read_json(root / 'endurance-trace.json')
        captured, diagnostics = audit(trace.get('events', []))
        calls.extend(captured)
        categories.update(diagnostics['structuredErrorCategories'])
        codes.update(diagnostics['structuredErrorCodes'])
        metrics = trace.get('metrics', {})
        current, wake = status(root, trace.get('task'))
        business_wait = business_timer_honored(trace) if metrics else False
        revised = revision_applied(trace)
        completions = [row['id'] for row in trace.get('events', []) if row['kind'] == 'BotDecision'
            and row['observation'].get('goalId') == trace.get('task') and row['detail'].get('action') == 'Complete']
        wrong_deliverables = set(metrics.get('incorrectCompletionDecisionIds', []))
        wrong_behavior = set(completions) if not business_wait else set()
        wrong_revision = set(completions) if not revised else set()
        verified_output = bool(metrics.get('goalCompletion', {}).get('successful'))
        trials.append({
            'trial': indexed['number'], 'evidence': indexed['evidence'], 'seconds': indexed['seconds'],
            'exitCode': indexed['exitCode'], 'goalId': trace.get('task'), 'finalStatus': current,
            'waitingForHuman': current == 'needs-reconciliation' or (current == 'waiting-input' and wake is None),
            'verifiedDeliverable': verified_output,
            'businessTimerHonored': business_wait,
            'revisionApplied': revised,
            'goalCompleted': verified_output and business_wait and revised and bool(trace.get('finalNotice')),
            'incorrectCompletionDecisionIds': sorted(wrong_deliverables | wrong_behavior | wrong_revision),
            'completionWithMissedBusinessTimerIds': sorted(wrong_behavior),
            'completionWithMissedRevisionIds': sorted(wrong_revision),
            'metrics': metrics, 'diagnostics': diagnostics,
        })
    report = {
        'assemblyHashes': batch['assemblyHashes'],
        'trialCount': len(trials),
        'strictPasses': sum(trial['exitCode'] == 0 for trial in trials),
        'goalCreated': sum(trial['goalId'] is not None for trial in trials),
        'goalsCompleted': sum(trial['goalCompleted'] for trial in trials),
        'verifiedDeliverables': sum(trial['verifiedDeliverable'] for trial in trials),
        'wrongCompletions': sum(len(trial['incorrectCompletionDecisionIds']) for trial in trials),
        'completionsWithMissedBusinessTimer': sum(len(trial['completionWithMissedBusinessTimerIds']) for trial in trials),
        'completionsWithMissedRevision': sum(len(trial['completionWithMissedRevisionIds']) for trial in trials),
        'repeatExecutions': sum(trial['metrics'].get('invalidRepeats', 0) for trial in trials),
        'humanWaitTrials': sum(trial['waitingForHuman'] for trial in trials),
        'trialsWithoutEnduranceMetrics': sum(not trial['metrics'] for trial in trials),
        'invalidDrafts': sum(trial['diagnostics']['invalidDrafts'] for trial in trials),
        'committedApiAttempts': sum(trial['metrics'].get('cognitionApi', {}).get('committedCallCount', 0) for trial in trials),
        'structuredErrorCategories': dict(categories),
        'structuredErrorCodes': dict(codes),
        'repairEvaluations': {key: sum(trial['diagnostics']['repairEvaluations'][key] for trial in trials)
            for key in ('succeeded', 'attempted', 'normalizedAndAccepted')},
        'automaticRetryWaits': sum(trial['diagnostics']['automaticRetryWaits'] for trial in trials),
        'successfulAutomaticRetries': sum(trial['diagnostics']['successfulAutomaticRetries'] for trial in trials),
        'decisionApi': cost(calls, rates),
        'trials': trials,
    }
    outcome_counts = report['decisionApi']['outcomes']
    generated = outcome_counts.get('accepted', 0) + outcome_counts.get('invalid-decision', 0)
    repairs = report['repairEvaluations']
    report['rates'] = {
        'completedGoalPerTrial': report['goalsCompleted'] / len(trials) if trials else None,
        'humanWaitPerTrial': report['humanWaitTrials'] / len(trials) if trials else None,
        'invalidAmongObservedDrafts': report['invalidDrafts'] / generated if generated else None,
        'autonomousRepairEvaluations': repairs['succeeded'] / repairs['attempted'] if repairs['attempted'] else None,
    }
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline', type=Path)
    parser.add_argument('candidate', type=Path, nargs='?')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rates-per-million', nargs=3, type=Decimal, metavar=('CACHE_HIT', 'CACHE_MISS', 'OUTPUT'))
    args = parser.parse_args()
    if args.rates_per_million and any(not value.is_finite() or value < 0 for value in args.rates_per_million):
        parser.error('rates must be finite and non-negative')
    output = args.output.resolve()
    if not output.is_relative_to(DATA):
        parser.error('--output must be beneath data/')
    baseline = read_json(args.baseline.resolve(strict=True))
    report = {'fingerprints': baseline['fingerprints'], 'baseline': measure(baseline, args.rates_per_million)}
    if args.candidate:
        candidate = read_json(args.candidate.resolve(strict=True))
        if candidate.get('fingerprints') != baseline.get('fingerprints'):
            parser.error('baseline and candidate driver, fixture or configuration differ')
        if baseline.get('assemblyHashes') == candidate.get('assemblyHashes'):
            parser.error('use separate before and after builds')
        report['candidate'] = measure(candidate, args.rates_per_million)
    write_json(output, report)


if __name__ == '__main__':
    main()
