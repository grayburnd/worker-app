using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // PostgreSQL Configuration
                var dbServer = Environment.GetEnvironmentVariable("DB_HOST") ?? "";
                var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
                var dbUsername = Environment.GetEnvironmentVariable("DB_USERNAME") ?? "";
                var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
                var dbName = Environment.GetEnvironmentVariable("DB") ?? "";
                var dbSslMode = Environment.GetEnvironmentVariable("DB_SSL_MODE") ?? "prefer";

                // Redis Sentinel Configuration
                var redisSentinelServer = Environment.GetEnvironmentVariable("REDIS_SENTINEL_HOST") ?? "";
                var redisSentinelPort = Environment.GetEnvironmentVariable("REDIS_SENTINEL_PORT") ?? "";
                var redisSentinelMaster = Environment.GetEnvironmentVariable("REDIS_MASTER_NAME") ?? "";
                var redisUser = Environment.GetEnvironmentVariable("REDIS_USER_NAME") ?? "";
                var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD") ?? "";

                var pgsql = OpenDbConnection(dbServer, dbPort, dbUsername, dbPassword, dbName, dbSslMode);
                var redisConn = OpenRedisConnectionWithSentinel(redisSentinelServer, redisSentinelPort, redisSentinelMaster, redisUser, redisPassword);
                var redis = redisConn.GetDatabase();

                // Keep alive is not implemented in Npgsql yet. This workaround was recommended:
                // https://github.com/npgsql/npgsql/issues/1214#issuecomment-235828359
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };
                while (true)
                {
                    // Slow down to prevent CPU spike, only query each 100ms
                    Thread.Sleep(100);

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected) {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnectionWithSentinel(redisSentinelServer, redisSentinelPort, redisSentinelMaster, redisUser, redisPassword);
                        redis = redisConn.GetDatabase();
                    }
                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");
                        // Reconnect DB if down
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection(dbServer, dbPort, dbUsername, dbPassword, dbName, dbSslMode);
                        }
                        else
                        { // Normal +1 vote requested
                            UpdateVote(pgsql, vote.voter_id, vote.vote);
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
        }

        private static NpgsqlConnection OpenDbConnection(string host, string port, string username, string password, string database, string sslMode)
        {
            var connectionStringBuilder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = int.Parse(port),
                Username = username,
                Password = password,
                Database = database,
                SslMode = Enum.Parse<SslMode>(sslMode, true),
                TrustServerCertificate = true
            };

            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionStringBuilder.ConnectionString);
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

        private static ConnectionMultiplexer OpenRedisConnectionWithSentinel(string sentinelHostnames, string sentinelPort, string masterName, string username, string password)
        {
            var sentinelHosts = sentinelHostnames
                .Split(',')
                .Select(h => h.Trim())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToList();

            while (true)
            {
                try
                {
                    var endpoints = string.Join(",", sentinelHosts.Select(h => $"{h}:{sentinelPort}"));
                    var connectionString = $"{endpoints},serviceName={masterName},abortConnect=false,allowAdmin=true,connectTimeout=10000,syncTimeout=10000";

                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        connectionString += $",user={username}";
                    }

                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        connectionString += $",password={password}";
                    }

                    Console.WriteLine($"Connecting to Redis Sentinel service '{masterName}' via {endpoints}");
                    var connection = ConnectionMultiplexer.Connect(connectionString);

                    if (connection.IsConnected)
                    {
                        Console.WriteLine($"Connected to Redis Sentinel service '{masterName}'");
                        return connection;
                    }

                    Console.Error.WriteLine($"Redis Sentinel connection to service '{masterName}' is not active yet");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error with sentinel {sentinelHostnames}: {ex.Message}");
                }

                Console.Error.WriteLine("Waiting for redis sentinel");
                Thread.Sleep(2000);
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