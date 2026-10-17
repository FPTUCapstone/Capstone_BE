"""Controlled synthetic fixture indexer/evaluator. Never opens SQL or reads production data.
Run .NET SQL integration for authoritative resolution, independently of this model benchmark.
"""
import argparse
import json
import math
import os
from pathlib import Path
import statistics
import sys
import time
import urllib.request
import urllib.error
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'services' / 'embedding-service'))
from protocol import VERSION, DIMENSIONS, MAX_CORPUS, passage_text, fingerprint, metrics, nfc

HERE = Path(__file__).parent

def http(base, path, data=None, method=None):
    encoded = json.dumps(data, ensure_ascii=False).encode() if data is not None else None
    request = urllib.request.Request(base.rstrip('/') + '/' + path, data=encoded, headers={'Content-Type':'application/json'}, method=method)
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)

def embed(base, text, kind):
    result = http(base, 'embed', {'text': text, 'kind': kind})
    if result['modelVersion'] != VERSION or len(result['vector']) != DIMENSIONS or abs(sum(x*x for x in result['vector']) - 1) > .001:
        raise ValueError('Pinned model contract mismatch')
    return result['vector'], result['elapsedMs']

def index(embedding, qdrant, collection, tours):
    if not collection.startswith('tm70_poc_') or len(tours) > MAX_CORPUS:
        raise ValueError('Only bounded isolated fixture collections are permitted')
    try:
        info = http(qdrant, f'collections/{collection}')
        config = info['result']['config']['params']['vectors']
        if config['size'] != DIMENSIONS or config['distance'] != 'Cosine':
            raise ValueError('Existing fixture collection vector contract mismatch')
    except urllib.error.HTTPError as error:
        if error.code != 404: raise
        http(qdrant, f'collections/{collection}', {'vectors':{'size':DIMENSIONS,'distance':'Cosine'}}, 'PUT')
    points = []
    for tour in tours:
        vector, _ = embed(embedding, passage_text(tour['title'], tour['destinations'], tour['operator']), 'passage')
        points.append({'id':tour['id'],'vector':vector,'payload':{'fingerprint':fingerprint(tour['title'],tour['destinations'],tour['operator']),'model_version':VERSION}})
    http(qdrant, f'collections/{collection}/points?wait=true', {'points':points}, 'PUT')

def fold(value):
    import unicodedata
    return ''.join(c for c in unicodedata.normalize('NFD', nfc(value).casefold().replace('đ','d')) if unicodedata.category(c) != 'Mn')

def lexical(query, tours):
    # Frozen three-public-field all-token comparator. Unicode accent folding approximates
    # Vietnamese SQL collation; no SQL parity claim. Fixture ratings are all null.
    normalized = fold(query).strip()
    tokens = normalized.split()
    ranked = []
    for tour in tours:
        title = fold(tour['title'])
        destinations = [fold(x) for x in tour['destinations']]
        operator = fold(tour['operator'])
        classes = []
        for token in tokens:
            if title == token: score = 0
            elif title.startswith(token): score = 1
            elif token in title: score = 2
            elif any(token in value for value in destinations): score = 3
            elif token in operator: score = 4
            else: break
            classes.append(score)
        if tokens and len(classes) == len(tokens):
            ranked.append((max(classes), fold(tour['title']), tour['id']))
    return [row[2] for row in sorted(ranked)]

def percentile(values, percentile):
    return sorted(values)[max(0, math.ceil(len(values)*percentile)-1)]

def cleanup_collection(qdrant, collection, result):
    try:
        http(qdrant, f'collections/{collection}', method='DELETE')
        result['cleanup'] = 'deleted'
    except urllib.error.HTTPError as error:
        if error.code == 404:
            result['cleanup'] = 'absent'
        else:
            result['cleanup'] = 'failed'
            result['error'] = f'Cleanup {type(error).__name__}: {error}'
    except Exception as error:
        result['cleanup'] = 'failed'
        result['error'] = f'Cleanup {type(error).__name__}: {error}'

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--embedding', default='http://127.0.0.1:8017')
    parser.add_argument('--qdrant', default='http://127.0.0.1:6337')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    tours = json.loads((HERE/'tours.json').read_text(encoding='utf-8-sig'))
    queries = json.loads((HERE/'queries.json').read_text(encoding='utf-8-sig'))
    collection = 'tm70_poc_' + uuid.uuid4().hex
    result = {'status':'unavailable','modelVersion':VERSION,'collection':collection,'fixtureCount':len(tours),'labels':'AI-authored synthetic, not independently validated','sqlAuthority':'separate provider-backed .NET tests required','results':[]}
    latencies, embeddings, vectors = [], [], []
    started = time.perf_counter()
    try:
        index(args.embedding,args.qdrant,collection,tours)
        result['indexBuildMs'] = (time.perf_counter()-started)*1000
        # Idempotent upsert and update timing, same IDs and fingerprints.
        update = time.perf_counter()
        index(args.embedding,args.qdrant,collection,tours)
        result['indexUpdateMs'] = (time.perf_counter()-update)*1000
        before = http(args.embedding,'diagnostics')
        evaluation_started = time.perf_counter()
        for query in queries:
            begin = time.perf_counter()
            vector, embedding_ms = embed(args.embedding,query['query'],'query')
            vector_begin = time.perf_counter()
            points = http(args.qdrant,f'collections/{collection}/points/query',{'query':vector,'limit':MAX_CORPUS+1,'params':{'exact':True},'with_payload':True})['result']['points']
            vector_ms = (time.perf_counter()-vector_begin)*1000
            if len(points) != len(tours): raise ValueError('Incomplete fixture corpus')
            ids = [int(x['id']) for x in sorted(points,key=lambda x:(-x['score'],int(x['id'])))]
            elapsed = (time.perf_counter()-begin)*1000
            latencies.append(elapsed); embeddings.append(embedding_ms); vectors.append(vector_ms)
            result['results'].append({'type':query['type'],'semantic':metrics(ids,query['relevance']),'lexical':metrics(lexical(query['query'],tours),query['relevance'])})
        result['status']='measured'
        for metric in ('recall@5','recall@10','precision@5','ndcg@10'):
            result[metric]={mode:statistics.mean(row[mode][metric] for row in result['results'] if row[mode][metric] is not None) for mode in ('semantic','lexical')}
        result['queryLatencyMs']={'p50':statistics.median(latencies),'p95':percentile(latencies,.95)}
        result['embeddingLatencyMs']={'p50':statistics.median(embeddings),'p95':percentile(embeddings,.95)}
        result['vectorLatencyMs']={'p50':statistics.median(vectors),'p95':percentile(vectors,.95)}
        after = http(args.embedding,'diagnostics')
        result['serviceResources']={'rssBytesAfter':after['rssBytes'],'cpuSecondsDuringQueries':after['cpuSeconds']-before['cpuSeconds'],'cpuPercentOneCoreEquivalent':100*(after['cpuSeconds']-before['cpuSeconds'])/(time.perf_counter()-evaluation_started),'sampling':'process RSS snapshot, CPU cumulative delta; not peak memory'}
        # Explicit invalidation smoke: remove a synthetic unpublished fixture point.
        http(args.qdrant,f'collections/{collection}/points/delete?wait=true',{'points':[tours[0]['id']]},'POST')
        remaining=http(args.qdrant,f'collections/{collection}/points/count',{'exact':True},'POST')['result']['count']
        if remaining != len(tours)-1: raise ValueError('Invalidation failed')
        result['invalidation']='passed'
    except Exception as error:
        result['error']=f'{type(error).__name__}: {error}'
    finally:
        cleanup_collection(args.qdrant, collection, result)
    text=json.dumps(result,ensure_ascii=False,indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(text,encoding='utf-8')
    print(text)
    return 0 if result['status']=='measured' and 'error' not in result else 1

if __name__=='__main__': raise SystemExit(main())
