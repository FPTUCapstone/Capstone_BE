"""CPU E5 inference. Startup fails closed if pinned weights cannot be loaded."""
from contextlib import asynccontextmanager
import threading
import psutil
import time
from typing import Literal

from fastapi import FastAPI
from pydantic import BaseModel, Field, field_validator
import torch
import torch.nn.functional as F
from transformers import AutoTokenizer, AutoModel
from protocol import MODEL, REVISION, VERSION, DIMENSIONS, prefixed

_lock = threading.Lock()
_tokenizer = None
_model = None

@asynccontextmanager
async def lifespan(app):
    global _tokenizer, _model
    torch.set_num_threads(2)
    _tokenizer = AutoTokenizer.from_pretrained(MODEL, revision=REVISION, trust_remote_code=False)
    _model = AutoModel.from_pretrained(MODEL, revision=REVISION, trust_remote_code=False, use_safetensors=True).to('cpu').eval()
    yield

app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None)

class Request(BaseModel):
    text: str = Field(min_length=1, max_length=4000)
    kind: Literal['query', 'passage']

    @field_validator('text')
    @classmethod
    def nonempty(cls, value):
        if not value.strip(): raise ValueError('Text must not be empty after normalization')
        return value

@app.get('/health')
def health():
    return {'ready': _model is not None, 'modelVersion': VERSION, 'dimensions': DIMENSIONS}

@app.get('/diagnostics')
def diagnostics():
    process = psutil.Process()
    times = process.cpu_times()
    return {'rssBytes': process.memory_info().rss, 'cpuSeconds': times.user + times.system}

@app.post('/embed')
def embed(request: Request):
    started = time.perf_counter()
    # Serialize CPU inference to bound concurrent memory. Model/tokenizer loaded only at startup.
    with _lock, torch.inference_mode():
        batch = _tokenizer([prefixed(request.text, request.kind)], max_length=512, padding=True, truncation=True, return_tensors='pt')
        hidden = _model(**batch).last_hidden_state
        pooled = hidden.masked_fill(~batch['attention_mask'][..., None].bool(), 0.0).sum(dim=1) / batch['attention_mask'].sum(dim=1)[..., None]
        vector = F.normalize(pooled, p=2, dim=1)[0].tolist()
    if len(vector) != DIMENSIONS:
        raise RuntimeError('Pinned model dimension mismatch')
    return {'vector': vector, 'modelVersion': VERSION, 'elapsedMs': (time.perf_counter() - started) * 1000}
