using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using StackExchange.Redis;



namespace Worker
{
    public class Program
    {
        private static ActivitySource _activitySource;
        private static TracerProvider _tracerProvider;
        private static MeterProvider _meterProvider;
        private static Meter _meter;
        private static Counter<int> _voteCounter;

        public static int Main(string[] args)
        {
            try
            {
                // Initialize OpenTelemetry
                InitializeOpenTelemetry();

                var pgsql = OpenDbConnection("Server=db;Username=postgres;Password=postgres;");
                var redisConn = OpenRedisConnection("redis");
                var redis = redisConn.GetDatabase();

                // Keep alive is not implemented in Npgsql yet. This workaround was recommended:
                // https://github.com/npgsql/npgsql/issues/1214#issuecomment-235828359
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "", traceparent = "" };
                int voteCount = 0;

                while (true)
                {
                    // Slow down to prevent CPU spike, only query each 100ms
                    Thread.Sleep(100);

                    // Flush traces every 50 votes to ensure they're sent
                    voteCount++;
                    if (voteCount % 50 == 0)
                    {
                        Console.WriteLine($"[TRACE] Flushing traces... (vote count: {voteCount})");
                        var flushed = _tracerProvider?.ForceFlush(1000) ?? false;
                        Console.WriteLine($"[TRACE] Flush result: {(flushed ? "Success" : "Failed")}");
                    }

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection("redis");
                        redis = redisConn.GetDatabase();
                    }

                    string json = redis.ListLeftPopAsync("votes").Result;
                    
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        var traceparent = vote.traceparent;

                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");
                        Console.WriteLine($"Traceparent: {traceparent}");

                        // Parse W3C traceparent format: 00-trace_id-span_id-trace_flags
                        var parts = traceparent.Split('-');
                        var traceIdString = parts[1];
                        var spanIdString = parts[2];
                        var traceFlagsString = parts[3];

                        // Convert to ActivityTraceId and ActivitySpanId
                        var traceIdActivityTrace = ActivityTraceId.CreateFromString(traceIdString.AsSpan());
                        var spanIdActivitySpan = ActivitySpanId.CreateFromString(spanIdString.AsSpan());
                        var traceFlags = byte.Parse(traceFlagsString, System.Globalization.NumberStyles.HexNumber);

                        // Create parent context from vote-service trace
                        var parentContext = new ActivityContext(
                            traceId: traceIdActivityTrace,
                            spanId: spanIdActivitySpan,
                            traceFlags: (ActivityTraceFlags)traceFlags,
                            isRemote: true
                        );

                        // Create a span for vote processing LINKED to parent trace
                        using (var activity = _activitySource.StartActivity(
                            "ProcessVote", 
                            ActivityKind.Internal, 
                            parentContext))
                        {
                            if (activity != null)
                            {
                                Console.WriteLine($"[TRACE] Created span: {activity.Id}");
                                Console.WriteLine($"[TRACE] Linked to trace: {activity.Context.TraceId}");
                                activity.SetTag("vote.option", vote.vote);
                                activity.SetTag("vote.voter_id", vote.voter_id);

                                // Reconnect DB if down
                                if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                                {
                                    Console.WriteLine("Reconnecting DB");
                                    pgsql = OpenDbConnection("Server=db;Username=postgres;Password=postgres;");
                                }
                                else
                                {
                                    // Normal +1 vote requested
                                    UpdateVote(pgsql, vote.voter_id, vote.vote);
                                    activity.SetTag("vote.success", true);
                                    
                                    // Increment custom meter
                                    _voteCounter?.Add(1, new KeyValuePair<string, object>("vote.option", vote.vote));
                                    Console.WriteLine($"[METRIC] Incremented vote counter for {vote.vote}");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[TRACE] Activity is NULL - not being created!");
                            }
                        }
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                // Flush and dispose of the Providers to ensure all data is sent
                _tracerProvider?.ForceFlush(5000);
                _tracerProvider?.Dispose();
                _meterProvider?.ForceFlush(5000);
                _meterProvider?.Dispose();
            }
        }

        private static void InitializeOpenTelemetry()
        {
            // Get configuration from environment variables
            var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") 
                ?? "http://otel-collector:4317";
            var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") 
                ?? "worker-service";

            Console.WriteLine($"[OpenTelemetry] Initializing with endpoint: {otlpEndpoint}");
            Console.WriteLine($"[OpenTelemetry] Service name: {serviceName}");

            // Create an ActivitySource for the worker
            _activitySource = new ActivitySource("Worker.Service");

            // Create and configure the TracerProvider
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName))
                .AddSource("Worker.Service")
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                })
                .Build();

            // Create and configure the MeterProvider
            _meter = new Meter("Worker.Service");
            _voteCounter = _meter.CreateCounter<int>("worker.votes.processed", "ea", "Number of votes processed by the worker");

            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName))
                .AddMeter("Worker.Service")
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                })
                .Build();

            Console.WriteLine("[OpenTelemetry] Initialization complete");
            Console.WriteLine($"[OpenTelemetry] Reading endpoint from env: OTEL_EXPORTER_OTLP_ENDPOINT={otlpEndpoint}");
            Console.WriteLine($"[OpenTelemetry] Metrics endpoint: {otlpEndpoint}/v1/metrics");
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
                catch (DbException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            // Use IP address to workaround https://github.com/StackExchange/StackExchange.Redis/issues/410
            var ipAddress = GetIp(hostname);
            Console.WriteLine($"Found redis at {ipAddress}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connecting to redis");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntryAsync(hostname)
                .Result
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            catch (DbException)
            {
                command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}