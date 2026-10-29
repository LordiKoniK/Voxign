FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    VOXIGN_DEV_MODE=0 \
    VOXIGN_HOST=0.0.0.0 \
    VOXIGN_SEQUENCE_PATH=/app/test_sequence.json

WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements-prod.txt .
RUN pip install --index-url https://download.pytorch.org/whl/cpu torch \
    && pip install -r requirements-prod.txt

COPY . .

EXPOSE 8765

CMD ["python", "VoxignPipeline.py"]
