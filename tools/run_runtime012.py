"""Validate the installed 0.1.2 entry with native approvals and monitored silence."""
from pathlib import Path
import datetime
import importlib.util
import json
import shutil
import subprocess
import sys
import time
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('native_helpers', r'C:\game_develop\ProbablyStolenChainStoresMod\tools\run_native_checks.py')
h = importlib.util.module_from_spec(spec)
spec.loader.exec_module(h)
sys.path.insert(0, str(ROOT/'Tests/Runtime/support'))
from audio_guard import GameAudioGuard
from private_desktop import PrivateDesktopProcess

SHIPPING = 'c1fd2350fd6a2e27002ff8885e57eb912dc0a6711789cfeded681078987158cd'


def wait_game(guard, proc, log, timeout):
    guard.attach(proc)
    guard.verify()
    deadline = time.monotonic() + timeout
    completed_at = None
    while proc.poll() is None:
        guard.raise_if_failed()
        if time.monotonic() >= deadline:
            raise TimeoutError('The owned native test exceeded its deadline')
        if completed_at is None and log.exists():
            if '[SoldRuntime012] RESULT ' in log.read_text(encoding='utf-8-sig', errors='replace'):
                assert proc.proof['desktopChecks'] > 0, 'The game desktop was never independently verified'
                completed_at = time.monotonic()
                proc.proof['nativeCompletionObservedBeforeWindowClosed'] = True
                proc.save_proof()
        if completed_at is None:
            proc.verify_desktop()
        elif time.monotonic() - completed_at > 20:
            raise TimeoutError('The native test reported completion but did not exit')
        time.sleep(.03)
    guard.raise_if_failed()
    return proc.poll()


def main():
    assert not h.processes() and not h.peer_tests(), 'Another game or native test is running'
    work = ROOT/'.work/runtime012'
    stage = work/'stage'
    assert stage.exists(), 'Prepare and audit the observer stage first'
    audit = json.loads((work/'audit/verification/native-runtime-audit.json').read_text(encoding='utf-8-sig'))
    preparation = json.loads((work/'preparation.json').read_text(encoding='utf-8-sig'))
    assert audit['status'] == 'PASS' and audit['capabilities'] == ['reflection-external']
    assert h.digest(stage/'ChainStores.dll') == preparation['observerSha256'] and h.digest(work/'audit/verification/native-runtime-audit.json') == preparation['auditSha256'], 'The audited observer changed'
    assert h.digest(stage/'ChainStores.dll') == h.digest(ROOT/'Tests/Runtime/bin/Release/netstandard2.1/ChainStores.dll')
    assert h.digest(h.MODS/'SoldStockOptimizer/SoldStockOptimizer.dll') == SHIPPING
    settings = json.loads((h.DATA/'SaveFile.es3').read_text(encoding='utf-8-sig'))
    original_order = settings['modOrder']['value']
    assert 'chain_stores' in original_order
    store = json.loads(settings['modTrustStore']['value'])
    profiles = [json.loads(machine['json']) for machine in store['machines'] if machine.get('mac')]
    trust = next((profile for profile in profiles if any(r.get('id') == 'sold_stock_optimizer' for r in profile.get('records', []))), None)
    assert trust is not None, 'The current native signed store must contain the player-approved optimizer'
    own = next((r for r in trust['records'] if r.get('id') == 'sold_stock_optimizer'), None)
    assert 'sold_stock_optimizer' in original_order and own and not own.get('developerMode') and own['declared'] == ['reflection-external'] and own['detected'] == ['reflection-external'], 'Enable the production optimizer through the native game menu first; the test never creates approvals'
    exclusions = []
    fre = next((r for r in trust['records'] if r.get('id') == 'faction_reputation_expansion'), None)
    if 'faction_reputation_expansion' in original_order and (not fre or 'files' not in fre.get('detected', [])):
        exclusions.append({'id':'faction_reputation_expansion', 'reason':'The native loader requires player approval for newly detected file access'})
        original_order = [m for m in original_order if m != 'faction_reputation_expansion']
    expected = [m for m in original_order if m != 'sold_stock_optimizer'] + ['sold_stock_optimizer']
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-'+uuid.uuid4().hex[:6]
    run = work/'runs'/stamp
    run.mkdir(parents=True)
    before, mods_before, workshop_before = h.snapshot(h.DATA), h.snapshot(h.MODS), h.snapshot(h.WORKSHOP)
    dirs = {p.relative_to(h.DATA).as_posix() for p in h.DATA.rglob('*') if p.is_dir()}
    host_dirs = {p.relative_to(h.TARGET).as_posix() for p in h.TARGET.rglob('*') if p.is_dir()}
    host_before = h.snapshot(h.TARGET)
    shutil.copytree(h.DATA, run/'userdata-before'); shutil.copytree(h.TARGET, run/'host-before')
    assert h.snapshot(run/'userdata-before') == before and h.snapshot(run/'host-before') == host_before
    result = dict(status='FAIL', expectedMods=expected, productionSha256=SHIPPING, gameSha256=h.digest(h.GAME/'Probably Stolen_Data/Managed/Assembly-CSharp.dll'),
                  nativeTrustFabricated=False, productionEntryInvokedManually=False, otherModRuntimeDisabled=False, osInputUsed=False, preexistingApprovalExclusions=exclusions, phases=[])
    guard = None; proc = None
    try:
        guard = GameAudioGuard(run/'audio-proof.json').start()
        shutil.copy2(stage/'ChainStores.dll', h.TARGET/'ChainStores.dll')
        for key in ['masterVolume', 'musicVolume', 'backgroundVolume', 'dialogVolume']:
            settings[key] = {'__type':'float', 'value':0.0}
        h.write(h.DATA/'SaveFile.es3', settings)
        for phase in ['cold-start-optimizer-last', 'cold-start-optimizer-first']:
            current = json.loads((h.DATA/'SaveFile.es3').read_text(encoding='utf-8-sig'))
            assert all(current[k]['value'] == 0 for k in ['masterVolume','musicVolume','backgroundVolume','dialogVolume'])
            order = expected if phase.endswith('last') else ['sold_stock_optimizer']+[m for m in expected if m != 'sold_stock_optimizer']
            current['modOrder']['value'] = order
            h.write(h.DATA/'SaveFile.es3', current)
            log = run/(phase+'.log')
            command = [str(h.GAME/'Probably Stolen.exe'), '-batchmode', '-noaudio', '-sold-runtime012', '-sold-runtime-mods', ','.join(expected), '-logFile', str(log),
                       '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720']
            assert not h.processes()
            proc = PrivateDesktopProcess(command, h.GAME, run/(phase+'-desktop.json'), before_resume=guard.attach_pid)
            print(json.dumps({'phase':phase,'pid':proc.pid,'run':str(run)}), flush=True)
            code = wait_game(guard, proc, log, timeout=450)
            proc.close(); proc = None
            text = log.read_text(encoding='utf-8-sig', errors='replace')
            rows = [line.split('[SoldRuntime012] RESULT ',1)[1] for line in text.splitlines() if '[SoldRuntime012] RESULT ' in line]
            assert rows, 'No native observer result in '+phase
            native = json.loads(rows[-1]); result['phases'].append(dict(phase=phase, exitCode=code, runtime=native))
            h.write(run/'progress.json', result)
            assert code == 0 and native['status'] == 'PASS', native['detail']
            assert h.digest(h.MODS/'SoldStockOptimizer/SoldStockOptimizer.dll') == SHIPPING
        result['status'] = 'PASS'
    except BaseException as error:
        result['error'] = str(error); result['traceback'] = traceback.format_exc()
    finally:
        if proc is not None:
            if proc.poll() is None: proc.kill()
            proc.close()
        if guard is not None:
            try: guard.close()
            except BaseException as error: result['audioError'] = str(error); result['status'] = 'FAIL'
            result['audio'] = {k:guard.proof.get(k) for k in ['status','maximumTargetPeak','targetSessionObservations','verifiedScans','noSessionScans','otherAppVolumeWrites','errors']}
        try:
            assert not h.processes(), 'A game process is still running'
            h.restore(h.DATA, run/'userdata-before', before, dirs)
            h.restore(h.TARGET, run/'host-before', host_before, host_dirs)
            result['userDataAndTrustRestored'] = h.snapshot(h.DATA) == before
            result['allLocalModsRestored'] = h.snapshot(h.MODS) == mods_before
            result['workshopUnchanged'] = h.snapshot(h.WORKSHOP) == workshop_before
            assert result['userDataAndTrustRestored'] and result['allLocalModsRestored'] and result['workshopUnchanged']
        except BaseException as error:
            result['restoreError'] = str(error); result['status'] = 'FAIL'
        h.write(run/'result.json', result)
        h.write(work/'latest.json', {'run':str(run),'status':result['status']})
    print(json.dumps({k:v for k,v in result.items() if k != 'phases'}, ensure_ascii=True), flush=True)
    return 0 if result['status'] == 'PASS' else 2


if __name__ == '__main__':
    raise SystemExit(main())
