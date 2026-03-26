'use strict';

const { NodeSDK } = require('@opentelemetry/sdk-node');
const { getNodeAutoInstrumentations } = require('@opentelemetry/auto-instrumentations-node');
const { OTLPTraceExporter } = require('@opentelemetry/exporter-trace-otlp-http');

// Configure the exporter
const traceExporter = new OTLPTraceExporter({
    url: process.env.OTEL_EXPORTER_OTLP_ENDPOINT || 'http://otel-collector:4318/v1/traces',
});

// Initialize the SDK
const sdk = new NodeSDK({
    traceExporter,
    instrumentations: [getNodeAutoInstrumentations()],
    serviceName: process.env.OTEL_SERVICE_NAME || 'result-service',
});

// In version 0.214.0, sdk.start() is synchronous
try {
    sdk.start();
    console.log('✅ OpenTelemetry initialized');
} catch (err) {
    console.error('❌ OpenTelemetry initialization failed', err);
}

// Handle graceful shutdown
process.on('SIGTERM', () => {
    sdk.shutdown()
        .then(() => console.log('✅ OpenTelemetry shutdown complete'))
        .catch((err) => console.error('❌ OTEL shutdown error', err))
        .finally(() => process.exit(0));
});