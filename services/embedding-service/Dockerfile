FROM python:3.11.9-slim
WORKDIR /service
COPY requirements.txt requirements.lock.txt ./
RUN pip install --no-cache-dir torch==2.10.0 --index-url https://download.pytorch.org/whl/cpu \
    && pip install --no-cache-dir -r requirements.lock.txt
COPY app.py protocol.py ./
ENV HF_HOME=/model-cache HF_HUB_DISABLE_TELEMETRY=1
EXPOSE 8000
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1", "--no-access-log"]
