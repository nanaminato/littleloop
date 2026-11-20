"""Run paired live endurance trials with immutable build, driver, inputs and configuration."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


REPO = Path(__file__).resolve().parents[2]
DATA = (REPO / 'data').resolve()
DRIVERS = Path(__file__).resolve().parent
CORE_ASSEMBLIES = ('LittleLoop.Chat.dll', 'LittleLoop.Core.dll', 'LittleLoop.Tools.dll')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def file_hashes(folder):
    return {p.relative_to(folder).as_posix(): sha(p) for p in sorted(folder.rglob('*')) if p.is_file()}


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8')) if path.is_file() else {}


def summarize(trials):
    metrics = [trial['metrics'] for trial in trials]
    return {
        'trials': len(trials),
        'strictPasses': sum(trial['exitCode'] == 0 for trial in trials),
        'goalsCompleted': sum(r.get('goalCompletion', {}).get('successful', 0) for r in metrics),
        'goalsAttempted': sum(r.get('goalCompletion', {}).get('attempted', 0) for r in metrics),
        'repeatExecutions': sum(r.get('invalidRepeats', 0) for r in metrics),
        'wrongCompletions': sum(len(r.get('incorrectCompletionDecisionIds', [])) for r in metrics),
        'humanWaitTrials': sum(trial['humanWait'] for trial in trials),
        'invalidDrafts': sum(r.get('cognitionApi', {}).get('outcomes', {}).get('invalid-decision', 0) for r in metrics),
        'autonomousRepairs': {
            'successful': sum(r.get('decisionRepair', {}).get('successful', 0) for r in metrics),
            'attempted': sum(r.get('decisionRepair', {}).get('attempted', 0) for r in metrics),
        },
        'committedApiCalls': sum(r.get('cognitionApi', {}).get('committedCallCount', 0) for r in metrics),
        'tokens': {
            key: sum(r.get('cognitionApi', {}).get('tokens', {}).get(key, {}).get('observed', 0) for r in metrics)
            for key in ('inputTokens', 'outputTokens', 'totalTokens', 'cachedInputTokens', 'uncachedInputTokens')
        },
        'trialsWithMetricEvidence': len([r for r in metrics if r]),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--chat-dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--trials', type=int, default=10)
    parser.add_argument('--seed', default='littleloop-decision-reliability-v1')
    parser.add_argument('--paired-with', type=Path, help='baseline batch.json; rejects a different driver, model or fixture')
    args = parser.parse_args()
    if args.trials < 1:
        parser.error('--trials must be positive')
    if sys.flags.optimize:
        parser.error('do not run with -O, which disables acceptance assertions')
    out = args.output.resolve()
    if not out.is_relative_to(DATA) or out == DATA:
        parser.error('--output must be a new directory beneath data/')
    dll = args.chat_dll.resolve(strict=True)
    model = Path(os.environ.get('LITTLELOOP_DECISION_MODEL', REPO / 'examples/models/deepseek.json')).resolve(strict=True)
    codex = Path(os.environ['LITTLELOOP_CODEX_EXECUTABLE']).resolve(strict=True)
    acp_names = tuple(key for key in os.environ if key.lower().startswith('chatbot__deepseek__'))
    settings = {key.lower(): os.environ[key] for key in sorted(acp_names)}
    launcher = Path(settings['chatbot__deepseek__readonlylauncher']).resolve(strict=True)
    driver_source = args.paired_with.resolve(strict=True).parent / 'driver' if args.paired_with else DRIVERS
    adapter_files = {
        key: sha(Path(value).resolve()) for key, value in settings.items()
        if key.startswith('chatbot__deepseek__arguments__') and value.lower().endswith(('.json', '.yaml', '.yml', '.js'))
        and Path(value).is_file()
    }
    fingerprints = {
        'driverHashes': {p.name: sha(p) for p in sorted(driver_source.glob('*.py')) if p.name != Path(__file__).name},
        'modelHash': sha(model),
        'codexHash': sha(codex),
        'acpLauncherHash': sha(launcher),
        'acpSettingsHash': hashlib.sha256(json.dumps(settings, sort_keys=True).encode()).hexdigest(),
        'acpArgumentFiles': adapter_files,
        'fixtureSeed': args.seed,
        'trialCount': args.trials,
    }
    if args.paired_with and read_json(args.paired_with.resolve(strict=True)).get('fingerprints') != fingerprints:
        parser.error('baseline and candidate driver, fixture or configuration differ')

    out.mkdir(parents=True)
    frozen = out / 'driver'
    frozen.mkdir()
    for name in fingerprints['driverHashes']:
        shutil.copyfile(driver_source / name, frozen / name)
    basemodel = out / 'model.json'
    shutil.copyfile(model, basemodel)
    build = out / 'build'
    shutil.copytree(dll.parent, build)
    pinned = file_hashes(build)
    trace = {
        'fingerprints': fingerprints,
        'batchRunnerHash': sha(Path(__file__).resolve()),
        'assemblyHashes': {key: pinned[key] for key in CORE_ASSEMBLIES},
        'trials': [],
    }
    summary_path = out / 'batch.json'
    write_json(summary_path, trace)
    env = dict(os.environ)
    env.pop('PYTHONOPTIMIZE', None)
    env.update(LITTLELOOP_REPO_ROOT=str(REPO), LITTLELOOP_CHAT_DLL=str(build / dll.name),
        LITTLELOOP_DECISION_MODEL=str(basemodel), PYTHONDONTWRITEBYTECODE='1', PYTHONUTF8='1',
        LITTLELOOP_CODEX_EXECUTABLE=str(codex))
    print(f'Evidence batch: {out}', flush=True)
    for number in range(1, args.trials + 1):
        assert file_hashes(build) == pinned, 'Frozen build changed during the batch'
        assert file_hashes(frozen) == fingerprints['driverHashes'], 'Frozen driver changed during the batch'
        assert sha(basemodel) == fingerprints['modelHash'], 'Frozen model config changed during the batch'
        assert sha(codex) == fingerprints['codexHash'] and sha(launcher) == fingerprints['acpLauncherHash']
        assert all(sha(Path(settings[key])) == value for key, value in adapter_files.items())
        trial = out / f'trial-{number:02}'
        env.update(LITTLELOOP_HTTP_TRIAL_PATH=str(trial), LITTLELOOP_ENDURANCE_TRIAL_SEED=f'{args.seed}/{number}')
        started = time.monotonic()
        with (out / f'trial-{number:02}.log').open('wb') as log:
            result = subprocess.run([sys.executable, str(frozen / 'direct_http_e2e.py'), '--decision-loop', 'endurance'],
                cwd=REPO, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
        failure = read_json(trial / 'failure.json')
        artifact = read_json(trial / 'endurance-trace.json')
        metrics = artifact.get('metrics', {})
        summary = {
            'number': number,
            'exitCode': result.returncode,
            'seconds': round(time.monotonic() - started, 3),
            'evidence': str(trial.relative_to(REPO)),
            'humanWait': observed_human_wait(artifact),
            'failure': failure.get('error', '')[:500] or ('driver emitted no failure record' if result.returncode else None),
            'metrics': metrics,
        }
        trace['trials'].append(summary)
        trace['summary'] = summarize(trace['trials'])
        write_json(summary_path, trace)
        print(f"Trial {number}/{args.trials}: exit {result.returncode}, {summary['seconds']}s, "
            f"{metrics.get('goalDecisionEvents', 0)} goal decisions", flush=True)
    return int(any(trial['exitCode'] for trial in trace['trials']))


def observed_human_wait(artifact):
    target = artifact.get('task')
    if target is None:
        return False
    decisions = [event for event in artifact.get('events', []) if event.get('kind') == 'BotDecision'
        and event.get('observation', {}).get('goalId') == target]
    last = decisions[-1]['detail'] if decisions else {}
    return last.get('action') == 'Wait' and not last.get('wakeAfterSeconds')


if __name__ == '__main__':
    raise SystemExit(main())
