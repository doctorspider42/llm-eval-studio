"""Integration test against a disposable running app; all LLM calls use a local mock.

Usage: python tests/test_series_summaries.py --base-url http://localhost:5197
"""
import argparse
import json
import threading
import time
import urllib.error
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', required=True)
parser.add_argument('--mock-port', type=int, default=5198)
parser.add_argument('--keep', action='store_true', help='Keep fixtures in the disposable database for UI checks')
args = parser.parse_args()
prompts = []

class Mock(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        model = request['model']
        prompt = request['messages'][-1]['content']
        if model == 'failed-candidate':
            self.send_response(500)
            self.end_headers()
            self.wfile.write(b'Fixture provider failure')
            return
        if model == 'summary-judge':
            prompts.append(prompt)
            time.sleep(.1)
            if 'Evidence:\n' in prompt:
                evidence = json.loads(prompt.split('Evidence:\n')[1])
                assert len(evidence) == 3, 'Summary did not receive all three repetitions'
                assert all(len(row['answers']) == 2 for row in evidence)
                assert all('summary-private-model-name' not in row for row in [prompt])
                assert sorted(row['repetition'] for row in evidence) == [1, 2, 3]
                assert all(any(a['status'] == 'Failed' for a in row['answers']) for row in evidence)
                aliases = sorted({a['alias'] for row in evidence for a in row['answers']})
                output = {'text': 'Compact task summary across three trials.', 'models': [
                    {'alias': alias, 'text': f'Compact {alias}: all three trials were considered.'} for alias in aliases]}
            else:
                source = json.loads(prompt.split('\n', 1)[1])
                assert len(source) == 2, 'Overview did not receive both task summaries'
                output = {'text': 'Compact overall series conclusion.'}
            content = json.dumps(output)
        elif model == 'broken-judge':
            content = '{"text":"Invalid summary","models":[]}'
        else:
            time.sleep(2)
            content = 'Fixture candidate answer.'
        body = json.dumps({'choices': [{'message': {'content': content}}]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(body)

server = ThreadingHTTPServer(('127.0.0.1', args.mock_port), Mock)
threading.Thread(target=server.serve_forever, daemon=True).start()
base = args.base_url.rstrip('/') + '/api/'

def api(method, path, body=None, status=200, html=False):
    payload = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(base + path, data=payload, method=method,
                                     headers={'Content-Type': 'application/json'})
    try:
        response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as ex:
        response = ex
    with response:
        text = response.read().decode()
        assert response.status == status, (method, path, response.status, text)
    return text if html else json.loads(text) if text else None

def wait_summary(batch, run_id, status='Completed'):
    for _ in range(100):
        run = next(r for r in api('GET', f'batches/{batch}/summaries') if r['id'] == run_id)
        if run['status'] in ('Completed', 'Failed'):
            assert run['status'] == status, run
            return run
        time.sleep(.1)
    raise AssertionError('Summary did not finish')

provider = batch = user = None
passed = False
models, cases = [], []
try:
    provider = api('POST', 'providers/', {'name': 'Summary test mock', 'type': 'OpenAI',
        'baseUrl': f'http://127.0.0.1:{args.mock_port}/v1', 'apiKey': 'fixture-only'}, status=201)['id']
    for name in ['candidate', 'failed-candidate', 'summary-judge', 'broken-judge']:
        models.append(api('POST', 'models/', {'providerId': provider, 'modelId': name,
            'displayName': 'summary-private-model-name-' + name, 'isJudge': 'judge' in name}, status=201)['id'])
    for name in ['Task one', 'Task two']:
        cases.append(api('POST', 'test-cases/', {'title': name, 'prompt': 'Do the fixture task.',
            'expectedAnswer': 'Fixture reference'}, status=201)['id'])
    user = api('POST', 'users/', {'name': 'Summary regression ' + uuid.uuid4().hex}, status=201)['id']
    batch = api('POST', 'batches/', {'testCaseIds': cases, 'modelIds': models[:2], 'repetitions': 3,
        'name': 'Summary regression series'}, status=202)['summary']['id']
    api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[2], 'language': 'en'}, status=400)
    api('GET', f'batches/{batch}/wait?timeoutSeconds=30&includeJudges=false')
    api('GET', f'batches/{batch}/report?lang=en&summaries=true', status=400, html=True)
    api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[0], 'language': 'en'}, status=400)
    api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[2], 'language': 'xx'}, status=400)
    source = api('GET', f'batches/{batch}')
    iteration = api('GET', f'iterations/{source["rows"][0]["cells"][0]["iterationId"]}')
    result_id = next(r['id'] for r in iteration['results'] if r['status'] == 'Completed')
    api('PUT', f'results/{result_id}/rating', {'userId': user, 'stars': 4, 'comment': 'UNIQUE_DETAILED_COMMENT'})
    run = api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[2], 'language': 'en'}, status=202)
    duplicate = api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[2], 'language': 'en'}, status=202)
    assert duplicate['id'] == run['id']
    done = wait_summary(batch, run['id'])
    assert done['completedCases'] == 2 and len(done['cases']) == 2
    assert all(c['repetitions'] == 3 and len(c['models']) == 2 for c in done['cases'])
    assert len(prompts) == 3, 'Duplicate generation caused extra LLM calls'
    compact = api('GET', f'batches/{batch}/report?lang=en&reveal=false&summaries=true&comments=true', html=True)
    assert 'Compact overall series conclusion.' in compact and 'Compact task summary' in compact
    assert 'UNIQUE_DETAILED_COMMENT' not in compact and 'summary-private-model-name-candidate' not in compact
    detailed = api('GET', f'batches/{batch}/report?lang=en&comments=true', html=True)
    assert 'UNIQUE_DETAILED_COMMENT' in detailed and 'Compact overall series conclusion.' not in detailed
    api('GET', f'batches/{batch}/report?lang=pl&summaries=true', status=400, html=True)
    api('DELETE', f'models/{models[2]}', status=409)
    api('PUT', f'results/{result_id}/rating', {'userId': user, 'stars': 3, 'comment': 'Changed comment'})
    assert api('GET', f'batches/{batch}/summaries')[0]['isStale']
    api('GET', f'batches/{batch}/report?lang=en&summaries=true', status=400, html=True)
    fresh = api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[2], 'language': 'en'}, status=202)
    assert not wait_summary(batch, fresh['id'])['isStale']
    assert len(prompts) == 6
    failed = api('POST', f'batches/{batch}/summaries', {'judgeModelId': models[3], 'language': 'en'}, status=202)
    assert wait_summary(batch, failed['id'], 'Failed')['error']
    assert len(api('GET', f'batches/{batch}/summaries')) == 2
    print('PASS: all repetitions and failures, blind aliases, whole-series synthesis, deduplication, cache, stale detection, regeneration, malformed AI response, compact/detailed reports, language and judge validation.')
    passed = True
    if args.keep:
        print('UI fixture batch: ' + batch)
finally:
    if not (args.keep and passed):
        if batch: api('DELETE', f'batches/{batch}', status=204)
        for case in cases: api('DELETE', f'test-cases/{case}', status=204)
        for model in models: api('DELETE', f'models/{model}', status=204)
        if provider: api('DELETE', f'providers/{provider}', status=204)
        if user: api('DELETE', f'users/{user}', status=204)
    server.shutdown()
