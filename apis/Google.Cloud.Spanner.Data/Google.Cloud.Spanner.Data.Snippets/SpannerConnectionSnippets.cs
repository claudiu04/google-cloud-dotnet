// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Cloud.ClientTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using Xunit;

#if !NETCOREAPP1_0
using System.Transactions;
#endif

// All samples now use a connection string variable declared before the start of the snippet.
// There are pros and cons for this:
// - Pro: The tests still run correctly when the fixture specifies extra configuration, e.g. credentials or host
// - Pro: The code is shorter (as connection string building can be verbose, particularly when already indented)
// - Con: There are fewer examples of building a connection string
// - Unsure: Arguably a connection string should be built elsewhere and reused, rather than appearing in the
//           code that creates a SpannerConnection. We need to see what actual usage tends towards.

namespace Google.Cloud.Spanner.Data.Snippets
{
    [SnippetOutputCollector]
    [Collection(nameof(SnippetFixture))]
    public class SpannerConnectionSnippets
    {
        private readonly SnippetFixture _fixture;

        public SpannerConnectionSnippets(SnippetFixture fixture) => _fixture = fixture;

        [Fact]
        public void CreateConnection()
        {
            // Snippet: #ctor(string, ChannelCredentials)
            string connectionString = "Data Source=projects/my-project/instances/my-instance/databases/my-db";
            SpannerConnection connection = new SpannerConnection(connectionString);
            Console.WriteLine(connection.Project);
            Console.WriteLine(connection.SpannerInstance);
            Console.WriteLine(connection.Database);
            // End snippet

            Assert.Equal("my-project", connection.Project);
            Assert.Equal("my-instance", connection.SpannerInstance);
            Assert.Equal("my-db", connection.Database);
        }

        [Fact]
        public async Task CreateDatabaseAsync()
        {
            string databaseName = "t_" + Guid.NewGuid().ToString("N").Substring(0, 28);
            string connectionString = $"{_fixture.NoDbDataSource}/databases/{databaseName}{_fixture.ConnectionStringExtraSettings}";

            // Sample: CreateDatabaseAsync
            // Additional: CreateDdlCommand
            using (var connection = new SpannerConnection(connectionString))
            {
                var createDbCmd = connection.CreateDdlCommand($"CREATE DATABASE {databaseName}");
                await createDbCmd.ExecuteNonQueryAsync();

                var createTableCmd = connection.CreateDdlCommand(
                    @"CREATE TABLE TestTable (
                                            Key                STRING(MAX) NOT NULL,
                                            StringValue        STRING(MAX),
                                            Int64Value         INT64,
                                          ) PRIMARY KEY (Key)");
                await createTableCmd.ExecuteNonQueryAsync();
            }
            // End sample

            using (var connection = new SpannerConnection(connectionString))
            {
                var createDbCmd = connection.CreateDdlCommand($"DROP DATABASE {databaseName}");
                await createDbCmd.ExecuteNonQueryAsync();
            }
        }

        [Fact]
        public async Task InsertDataAsync()
        {
            await _fixture.EnsureTestDatabaseAsync().ConfigureAwait(false);
            string connectionString = _fixture.ConnectionString;

            // Sample: InsertDataAsync
            using (var connection = new SpannerConnection(connectionString))
            {
                await connection.OpenAsync();

                var cmd = connection.CreateInsertCommand(
                    "TestTable", new SpannerParameterCollection
                    {
                        {"Key", SpannerDbType.String},
                        {"StringValue", SpannerDbType.String},
                        {"Int64Value", SpannerDbType.Int64}
                    });

                // This executes 5 distinct transactions with one row written per transaction.
                for (var i = 0; i < 5; i++)
                {
                    cmd.Parameters["Key"].Value = Guid.NewGuid().ToString("N");
                    cmd.Parameters["StringValue"].Value = $"StringValue{i}";
                    cmd.Parameters["Int64Value"].Value = i;
                    int rowsAffected = await cmd.ExecuteNonQueryAsync();
                    Console.WriteLine($"{rowsAffected} rows written...");
                }
            }
            // End sample
        }        

        [Fact]
        public async Task ReadUpdateDeleteAsync()
        {
            await _fixture.EnsureTestDatabaseAsync().ConfigureAwait(false);
            string connectionString = _fixture.ConnectionString;

            // Sample: ReadUpdateDeleteAsync
            // Additional: CreateUpdateCommand
            // Additional: CreateDeleteCommand
            // Additional: CreateSelectCommand
            using (var connection = new SpannerConnection(connectionString))
            {
                await connection.OpenAsync();

                // Read the first two keys in the database.
                var keys = new List<string>();
                var selectCmd = connection.CreateSelectCommand("SELECT * FROM TestTable");
                using (var reader = await selectCmd.ExecuteReaderAsync())
                {
                    while (keys.Count < 3 && await reader.ReadAsync())
                    {
                        keys.Add(reader.GetFieldValue<string>("Key"));
                    }
                }

                // Update the Int64Value of keys[0]
                // Include the primary key and update columns.
                var updateCmd = connection.CreateUpdateCommand(
                    "TestTable", new SpannerParameterCollection
                    {
                        {"Key", SpannerDbType.String, keys[0]},
                        {"Int64Value", SpannerDbType.Int64, 0L}
                    });
                await updateCmd.ExecuteNonQueryAsync();

                // Delete row for keys[1]
                var deleteCmd = connection.CreateDeleteCommand(
                    "TestTable", new SpannerParameterCollection
                    {
                        {"Key", SpannerDbType.String, keys[1]}
                    });
                await deleteCmd.ExecuteNonQueryAsync();
            }
            // End sample
        }

        // Sample: SpannerFaultDetectionStrategy
        private class SpannerFaultDetectionStrategy : ITransientErrorDetectionStrategy
        {
            /// <inheritdoc />
            public bool IsTransient(Exception ex) => ex.IsTransientSpannerFault();
        }
        // End sample

        [Fact]
        public async Task TransactionAsync()
        {
            await _fixture.EnsureTestDatabaseAsync().ConfigureAwait(false);
            string connectionString = _fixture.ConnectionString;

            // Sample: TransactionAsync
            // Additional: BeginTransactionAsync
            var retryPolicy = new RetryPolicy<SpannerFaultDetectionStrategy>(RetryStrategy.DefaultExponential);

            await retryPolicy.ExecuteAsync(
                async () =>
                {
                    using (var connection = new SpannerConnection(connectionString))
                    {
                        await connection.OpenAsync();

                        using (var transaction = await connection.BeginTransactionAsync())
                        {
                            var cmd = connection.CreateInsertCommand(
                                "TestTable", new SpannerParameterCollection
                                {
                                    {"Key", SpannerDbType.String},
                                    {"StringValue", SpannerDbType.String},
                                    {"Int64Value", SpannerDbType.Int64}
                                });
                            cmd.Transaction = transaction;

                            // This executes a single transactions with alls row written at once during CommitAsync().
                            // If a transient fault occurs, this entire method is re-run.
                            for (int i = 0; i < 5; i++)
                            {
                                cmd.Parameters["Key"].Value = Guid.NewGuid().ToString("N");
                                cmd.Parameters["StringValue"].Value = $"StringValue{i}";
                                cmd.Parameters["Int64Value"].Value = i;
                                await cmd.ExecuteNonQueryAsync();
                            }

                            await transaction.CommitAsync();
                        }
                    }
                });
            // End sample
        }

#if !NETCOREAPP1_0
        [Fact]
        public async Task TransactionScopeAsync()
        {
            await _fixture.EnsureTestDatabaseAsync().ConfigureAwait(false);
            string connectionString = _fixture.ConnectionString;

            // Sample: TransactionScopeAsync
            // Additional: CreateInsertCommand
            var retryPolicy = new RetryPolicy<SpannerFaultDetectionStrategy>(RetryStrategy.DefaultExponential);

            await retryPolicy.ExecuteAsync(
                async () =>
                {
                    using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                    {
                        using (var connection = new SpannerConnection(connectionString))
                        {
                            await connection.OpenAsync();

                            var cmd = connection.CreateInsertCommand(
                                "TestTable", new SpannerParameterCollection
                                {
                                    {"Key", SpannerDbType.String},
                                    {"StringValue", SpannerDbType.String},
                                    {"Int64Value", SpannerDbType.Int64}
                                });

                            // This executes a single transactions with alls row written at once during scope.Complete().
                            // If a transient fault occurs, this entire method is re-run.
                            for (var i = 0; i < 5; i++)
                            {
                                cmd.Parameters["Key"].Value = Guid.NewGuid().ToString("N");
                                cmd.Parameters["StringValue"].Value = $"StringValue{i}";
                                cmd.Parameters["Int64Value"].Value = i;
                                await cmd.ExecuteNonQueryAsync();
                            }

                            scope.Complete();
                        }
                    }
                });
            // End sample
        }

        [Fact]
        public async Task DataAdapterAsync()
        {
            await _fixture.EnsureTestDatabaseAsync().ConfigureAwait(false);
            string connectionString = _fixture.ConnectionString;

            // Sample: DataAdapterAsync
            using (var connection = new SpannerConnection(connectionString))
            {
                var untypedDataSet = new DataSet();

                // Provide the name of the Cloud Spanner table and primary key column names.
                var adapter = new SpannerDataAdapter(connection, "TestTable", "Key");
                adapter.Fill(untypedDataSet);

                // Insert a row
                var row = untypedDataSet.Tables[0].NewRow();
                row["Key"] = Guid.NewGuid().ToString("N");
                row["StringValue"] = "New String Value";
                row["Int64Value"] = 0L;
                untypedDataSet.Tables[0].Rows.Add(row);

                adapter.Update(untypedDataSet.Tables[0]);
            }
            // End sample
        }
#endif
    }
}
