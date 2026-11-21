"""Deterministic checks for the isolated obligation scorer."""

import unittest

from autonomy_reliability_report import audit
from goal_obligations_report import measure_trial


def event(event_id, kind, observation=None, detail=None):
    return {'id': event_id, 'kind': kind, 'detail': detail or {},
        'observation': {'goalId': 'goal', 'goalVersion': 4, **(observation or {})}}


class GoalObligationsReportTests(unittest.TestCase):
    def setUp(self):
        self.trace = {
            'task': 'goal',
            'expectedMarkers': {'B04': 'LL-aaaabbbb', 'B05': 'LL-ccccdddd'},
            'finalNoticeStamp': 'RPT-1234',
            'revisionDuringNativeDispatch': {'userSequence': 7},
            'metrics': {'verifiedStages': {'B04/codex': 2, 'B05/deepseek': 6},
                'recovery': {'sessionRecovery': {'successful': 2, 'attempted': 2}}},
            'events': [
                event(2, 'HarnessReceipt', {'status': 'returned', 'dispatchId': 'first'}),
                event(3, 'BotDecision', detail={'action': 'Wait', 'wakeAfterSeconds': 30}),
                event(5, 'BotDecision', {'dispatchId': 'next'}, {'action': 'Delegate'}),
                event(6, 'HarnessReceipt', {'status': 'returned', 'dispatchId': 'next'}),
            ],
        }
        self.timed = {
            'id': 'timed', 'status': 'satisfied', 'sourceSequence': 2, 'createdVersion': 1,
            'kind': 'time', 'description': 'After B04, wait 30 seconds before continuing B05.', 'delaySeconds': 30,
            'check': {'id': 'check', 'observed': False, 'goalVersion': 4, 'dueAtUnixMilliseconds': 50000},
            'assessment': {'id': 'timed', 'checkId': 'check'},
        }

    def score(self, obligations, snapshots=None):
        goal = {'version': 4, 'obligations': obligations}
        return measure_trial(self.trace, 2, goal, snapshots or [('Complete', goal)])

    def test_legacy_has_no_capture_or_false_satisfaction_denominator(self):
        score = self.score(None)
        self.assertFalse(score['capture']['available'])
        self.assertFalse(score['assessments'])

    def test_scheduled_time_is_neither_evidence_nor_semantic_satisfaction(self):
        result = self.score([self.timed])
        self.assertEqual(result['capture']['sourceGroups']['timedCheck'], ['timed'])
        self.assertEqual(result['capture']['timeConditionIdsWithRequiredDelay'], ['timed'])
        self.assertEqual(result['capture']['timeConditionIdsWithDueCheck'], [])
        self.assertEqual(result['capture']['sourceGroups']['supplementalNotice'], [])
        self.assertFalse(result['assessments'][0]['evidenceExists'])
        self.assertFalse(result['assessments'][0]['fixtureSemanticCheck'])

    def test_restart_links_the_persisted_check_to_the_due_event(self):
        self.trace['restart'] = {'waitDecision': 3, 'unixTime': 20, 'dueUnixMilliseconds': 50000}
        self.trace['events'].insert(2, event(4, 'Timer', {'status': 'due', 'checkId': 'check',
            'wakeObligationId': 'timed', 'wakeAtUnixMilliseconds': 50000}))
        before = {**self.timed, 'status': 'inProgress'}
        after = {**self.timed, 'check': {**self.timed['check'], 'observed': True}}
        result = self.score([after], [('Wait', {'version': 4, 'obligations': [before]}),
            ('Complete', {'version': 4, 'obligations': [after]})])
        self.assertTrue(result['restart']['persistedObligationCheck'])
        self.assertEqual(result['capture']['timeConditionIdsWithDueCheck'], ['timed'])
        self.assertTrue(result['assessments'][0]['evidenceExists'])
        self.assertTrue(result['assessments'][0]['fixtureSemanticCheck'])
        self.trace['events'][2]['observation']['goalId'] = 'other-goal'
        result = self.score([after])
        self.assertFalse(result['restart']['persistedObligationCheck'])
        self.assertFalse(result['assessments'][0]['evidenceExists'])

    def test_notice_is_matched_to_its_source_and_real_completion(self):
        obligation = {'id': 'stamp', 'sourceSequence': 7, 'createdVersion': 3, 'status': 'satisfied',
            'description': 'Add RPT-1234 to the end of the final notice.',
            'assessment': {'id': 'stamp', 'completionNotice': True}}
        self.trace['events'].append(event(7, 'BotDecision', detail={'action': 'Reply', 'message': 'Saved RPT-1234'}))
        result = self.score([obligation])
        self.assertEqual(result['capture']['sourceGroups']['supplementalNotice'], ['stamp'])
        self.assertFalse(result['assessments'][0]['evidenceExists'])
        self.trace['events'].append(event(8, 'BotDecision', detail={'action': 'Complete', 'message': 'Done RPT-wrong'}))
        result = self.score([obligation])
        self.assertTrue(result['assessments'][0]['evidenceExists'])
        self.assertFalse(result['assessments'][0]['fixtureSemanticCheck'])

    def test_captured_time_in_wrong_category_is_not_evidence_bound(self):
        condition = {**self.timed, 'kind': 'behavior', 'delaySeconds': None, 'status': 'pending'}
        score = self.score([condition])
        self.assertEqual(score['capture']['sourceGroups']['timedCheck'], ['timed'])
        self.assertEqual(score['capture']['timeConditionIdsWithRequiredDelay'], [])
        self.assertEqual(score['capture']['timeConditionIdsWithDueCheck'], [])

    def test_expected_failure_is_exact_tool_evidence_for_behavior(self):
        self.trace['events'].append(event(8, 'ToolResult', {'status': 'failed', 'dispatchId': 'probe'}))
        obligation = {'id': 'probe', 'sourceSequence': 2, 'createdVersion': 1, 'status': 'satisfied',
            'kind': 'behavior', 'description': 'Check the unavailable file.',
            'assessment': {'id': 'probe', 'dispatchId': 'probe', 'eventId': 8, 'expectedToolStatus': 'failed'}}
        self.assertTrue(self.score([obligation])['assessments'][0]['evidenceExists'])
        self.assertFalse(self.score([{**obligation, 'kind': 'result'}])['assessments'][0]['evidenceExists'])

    def test_internal_recovery_counts_rejected_drafts_and_durable_continuation_separately(self):
        bad_draft = {'outcome': 'invalid-decision', 'error': {'code': 'missing-harness', 'field': '$.harness'}}
        valid_draft = {'outcome': 'accepted'}
        events = [
            event(1, 'DecisionAudit', {'goalVersion': 4, 'sequence': 27}, {
                'triggerKind': 'HarnessReceipt', 'cognitionSource': {
                    'engine': 'host/decision-deferred', 'modelCalls': [bad_draft, bad_draft],
                },
            }),
            event(2, 'BotDecision', {'goalVersion': 5, 'sequence': 27}, {
                'triggerKind': 'HarnessReceipt', 'triggerGoalVersion': 4, 'cognitionSource': {
                    'engine': 'model', 'modelCalls': [bad_draft, valid_draft],
                },
            }),
        ]
        calls, metrics = audit(events)
        self.assertEqual(len(calls), 4)
        self.assertEqual(metrics['invalidDrafts'], 3)
        self.assertEqual(metrics['repeatedDraftErrors'], 1)
        self.assertEqual(metrics['errorsRepeatedAfterDeferral'], 1)
        self.assertEqual(metrics['redraftedAndCommitted'], 1)
        self.assertEqual(metrics['recoveredDecisions'], 1)


if __name__ == '__main__':
    unittest.main()
