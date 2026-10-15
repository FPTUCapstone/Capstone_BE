"""Actual pinned model inference smoke; no mocks. Run with python smoke.py."""
import asyncio
import math
from app import app, lifespan, embed, Request

async def main():
    async with lifespan(app):
        query = embed(Request(text='nghỉ dưỡng biển Phú Quốc', kind='query'))
        passage = embed(Request(text='Nghỉ dưỡng biển Phú Quốc', kind='passage'))
        assert len(query['vector']) == 384
        assert abs(sum(x*x for x in query['vector'])-1) < .001
        assert query['modelVersion'] == passage['modelVersion']
        print({'modelVersion':query['modelVersion'],'dimensions':384,'queryMs':query['elapsedMs'],'passageMs':passage['elapsedMs'],'cosine':sum(a*b for a,b in zip(query['vector'],passage['vector']))})

if __name__=='__main__': asyncio.run(main())
