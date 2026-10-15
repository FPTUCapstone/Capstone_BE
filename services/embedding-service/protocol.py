"""Shared PoC content contract. No inference substitute exists in this module."""
import hashlib
import json
import math
import unicodedata

MODEL = 'intfloat/multilingual-e5-small'
REVISION = 'fd1525a9fd15316a2d503bf26ab031a61d056e98'
VERSION = f'{MODEL}@{REVISION}:mean-pool-l2-512-v1'
DIMENSIONS = 384
MAX_CORPUS = 256

def nfc(value):
    return unicodedata.normalize('NFC', value)

def passage_text(title, destinations, operator):
    def frame(value): return f'{len(value.encode("utf-8"))}:{value}'
    names = sorted(set(nfc(x) for x in destinations), key=lambda x: x.encode('utf-8'))
    return 'Title: ' + frame(nfc(title)) + '\nDestinations: ' + ''.join(frame(x) for x in names) + '\nOperator: ' + frame(nfc(operator))

def fingerprint(title, destinations, operator):
    return hashlib.sha256(passage_text(title, destinations, operator).encode('utf-8')).hexdigest()

def prefixed(text, kind):
    value = nfc(text).strip()
    if kind not in ('query', 'passage') or not value or len(value) > 4000:
        raise ValueError('Invalid embedding input')
    return f'{kind}: {value}'

def metrics(ranking, relevance):
    relevant = {int(key): value for key, value in relevance.items() if value > 0}
    result = {f'recall@{k}': len(set(ranking[:k]) & relevant.keys()) / len(relevant) if relevant else None for k in (5, 10)}
    result['precision@5'] = len(set(ranking[:5]) & relevant.keys()) / 5
    dcg = sum((2 ** relevant.get(key, 0) - 1) / math.log2(i + 2) for i, key in enumerate(ranking[:10]))
    ideal = sum((2 ** grade - 1) / math.log2(i + 2) for i, grade in enumerate(sorted(relevant.values(), reverse=True)[:10]))
    result['ndcg@10'] = dcg / ideal if ideal else None
    return result
