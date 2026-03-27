FROM python:3.11-slim AS base

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /usr/local/app

COPY requirements.txt ./requirements.txt
RUN pip install --no-cache-dir -r requirements.txt

# Install OTel components in the base stage
RUN pip install opentelemetry-distro opentelemetry-exporter-otlp
RUN opentelemetry-bootstrap -a install

FROM base AS dev
RUN pip install watchdog
ENV FLASK_ENV=development
# Wrap the dev command
CMD ["opentelemetry-instrument", "python", "app.py"]

FROM base AS final
COPY . .
EXPOSE 80
# Wrap the production Gunicorn command
CMD ["opentelemetry-instrument", "gunicorn", "app:app", "-b", "0.0.0.0:80", "--workers", "4"]