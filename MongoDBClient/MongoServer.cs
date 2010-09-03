﻿/* Copyright 2010 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using MongoDB.BsonLibrary;
using MongoDB.MongoDBClient.Internal;

namespace MongoDB.MongoDBClient {
    public class MongoServer {
        #region private static fields
        private static object staticLock = new object();
        private static List<MongoServer> servers = new List<MongoServer>();
        #endregion

        #region private fields
        private object serverLock = new object();
        private MongoServerState state = MongoServerState.Disconnected;
        private List<MongoServerAddress> seedList;
        private List<MongoServerAddress> replicaSet;
        private SafeMode safeMode = SafeMode.False;
        private bool slaveOk;
        private Dictionary<string, MongoDatabase> databases = new Dictionary<string, MongoDatabase>();
        private MongoConnectionPool connectionPool;
        private MongoCredentials adminCredentials;
        private MongoCredentials credentials;
        #endregion

        #region constructors
        public MongoServer(
            MongoConnectionSettings settings
        ) {
            this.seedList = settings.SeedList;

            // credentials (if any) are for server only if no DatabaseName was provided
            if (settings.Credentials != null && settings.DatabaseName == null) {
                if (settings.Credentials.Admin) {
                    this.adminCredentials = settings.Credentials;
                    this.credentials = null;
                } else {
                    this.adminCredentials = null;
                    this.credentials = settings.Credentials;
                }
            }
        }
        #endregion

        #region factory methods
        public static MongoServer Create(
            MongoConnectionSettings settings
        ) {
            lock (staticLock) {
                foreach (MongoServer server in servers) {
                    if (server.seedList.SequenceEqual(settings.SeedList)) {
                        return server;
                    }
                }

                MongoServer newServer = new MongoServer(settings);
                servers.Add(newServer);
                return newServer;
            }
        }

        public static MongoServer Create(
            MongoConnectionStringBuilder builder
        ) {
            return Create(builder.ToConnectionSettings());
        }

        public static MongoServer Create(
            MongoUrl url
        ) {
            return Create(url.ToConnectionSettings());
        }

        public static MongoServer Create(
            string connectionString
        ) {
            if (connectionString.StartsWith("mongodb://")) {
                var url = new MongoUrl(connectionString);
                return Create(url);
            } else {
                MongoConnectionStringBuilder builder = new MongoConnectionStringBuilder(connectionString);
                return Create(builder);
            }
        }

        public static MongoServer Create(
            Uri uri
        ) {
            return Create(new MongoUrl(uri.ToString()));
        }
        #endregion

        #region public properties
        public MongoCredentials AdminCredentials {
            get { return adminCredentials; }
            set { adminCredentials = value; }
        }

        public MongoDatabase AdminDatabase {
            get { return GetDatabase("admin", adminCredentials); }
        }

        public MongoCredentials Credentials {
            get { return credentials; }
            set { credentials = value; }
        }

        public IEnumerable<MongoServerAddress> ReplicaSet {
            get { return replicaSet; }
        }

        public SafeMode SafeMode {
            get { return safeMode; }
            set { safeMode = value; }
        }

        public IEnumerable<MongoServerAddress> SeedList {
            get { return seedList; }
        }

        public bool SlaveOk {
            get { return slaveOk; }
            set { slaveOk = value; }
        }

        public MongoServerState State {
            get { return state; }
        }
        #endregion

        #region public indexers
        public MongoDatabase this[
            string databaseName
        ] {
            get { return GetDatabase(databaseName); }
        }

        public MongoDatabase this[
            string databaseName,
            MongoCredentials credentials
        ] {
            get { return GetDatabase(databaseName, credentials); }
        }
        #endregion

        #region public methods
        public void CloneDatabase(
            string fromHost
        ) {
            throw new NotImplementedException();
        }

        public void Connect() {
            Connect(MongoDefaults.ConnectTimeout);
        }

        public void Connect(
            TimeSpan timeout
        ) {
            lock (serverLock) {
                if (state != MongoServerState.Connected) {
                    state = MongoServerState.Connecting;
                    try {
                        var results = FindPrimary(timeout);

                        if (results.CommandResult.ContainsElement("hosts")) {
                            replicaSet = new List<MongoServerAddress>();
                            foreach (string host in results.CommandResult.GetArray("hosts")) {
                                var address = MongoServerAddress.Parse(host);
                                replicaSet.Add(address);
                            }
                        } else {
                            replicaSet = null;
                        }

                        // the connection FindPrimary made to the primary becomes the first connection in the new connection pool
                        connectionPool = new MongoConnectionPool(this, results.Address, results.Connection);

                        state = MongoServerState.Connected;
                    } catch {
                        state = MongoServerState.Disconnected;
                        throw;
                    }
                }
            }
        }

        // TODO: fromHost parameter?
        public void CopyDatabase(
            string from,
            string to
        ) {
            throw new NotImplementedException();
        }

        public void Disconnect() {
            // normally called from a connection when there is a SocketException
            // but anyone can call it if they want to close all sockets to the server
            lock (serverLock) {
                if (state == MongoServerState.Connected) {
                    connectionPool.Close();
                    connectionPool = null;
                    state = MongoServerState.Disconnected;
                }
            }
        }

        public void DropDatabase(
            string databaseName
        ) {
            MongoDatabase database = GetDatabase(databaseName);
            var command = new BsonDocument("dropDatabase", 1);
            database.RunCommand(command);
        }

        public MongoDatabase GetDatabase(
            string databaseName
        ) {
            return GetDatabase(databaseName, credentials);
        }

        public MongoDatabase GetDatabase(
            string databaseName,
            MongoCredentials credentials
        ) {
            string key;
            if (credentials == null) {
                key = databaseName;
            } else {
                key = string.Format("{0}[{1}]", databaseName, credentials);
            }

            lock (serverLock) {
                MongoDatabase database;
                if (!databases.TryGetValue(key, out database)) {
                    if (credentials == null) {
                        database = new MongoDatabase(this, databaseName);
                    } else {
                        database = new MongoDatabase(this, databaseName, credentials);
                    }
                    databases.Add(key, database);
                }
                return database;
            }
        }

        public List<string> GetDatabaseNames() {
            var databaseNames = new List<string>();
            var result = AdminDatabase.RunCommand("listDatabases");
            var databases = (BsonDocument) result["databases"];
            foreach (BsonElement database in databases) {
                string databaseName = (string) ((BsonDocument) database.Value)["name"];
                databaseNames.Add(databaseName);
            }
            databaseNames.Sort();
            return databaseNames;
        }

        public void Reconnect() {
            lock (serverLock) {
                Disconnect();
                Connect();
            }
        }

        public BsonDocument RenameCollection(
            string oldCollectionName,
            string newCollectionName
        ) {
            var command = new BsonDocument {
                { "renameCollection", oldCollectionName },
                { "to", newCollectionName }
            };
            return AdminDatabase.RunCommand(command);
        }
        #endregion

        #region private methods
        private QueryServerResults FindPrimary(
            TimeSpan timeout
        ) {
            DateTime deadline = DateTime.UtcNow + timeout;

            // query all servers in seed list in parallel (they will report results back through the resultsQueue)
            var resultsQueue = new BlockingQueue<QueryServerResults>();
            var queriedServers = new HashSet<MongoServerAddress>();
            int pendingReplies = 0;
            foreach (var address in seedList) {
                var args = new QueryServerParameters {
                    Address = address,
                    ResultsQueue = resultsQueue
                };
                ThreadPool.QueueUserWorkItem(QueryServerWorkItem, args);
                queriedServers.Add(address);
                pendingReplies++;
            }

            // process the results as they come back and stop as soon as we find the primary
            // stragglers will continue to report results to the resultsQueue but no one will read them
            // and eventually it will all get garbage collected

            QueryServerResults results;
            while (pendingReplies > 0 && (results = resultsQueue.Dequeue(deadline)) != null) {
                pendingReplies--;

                if (results.Exception != null) {
                    // TODO: how to report exceptions
                    continue;
                }

                var commandResult = results.CommandResult;
                if (!commandResult.GetAsBoolean("ok")) {
                    // TODO: how to report errors?
                    continue;
                }

                if (commandResult.GetAsBoolean("ismaster")) {
                    return results;
                }

                // look for additional members of the replica set that might not have been in the seed list and query them also
                if (commandResult.ContainsElement("hosts")) {
                    foreach (string host in commandResult.GetArray("hosts")) {
                        var address = MongoServerAddress.Parse(host);
                        if (!queriedServers.Contains(address)) {
                            var args = new QueryServerParameters {
                                Address = address,
                                ResultsQueue = resultsQueue
                            };
                            ThreadPool.QueueUserWorkItem(QueryServerWorkItem, args);
                            queriedServers.Add(address);
                            pendingReplies++;
                        }
                    }
                }
            }

            throw new MongoException("Unable to connect to server");
        }

        // note: this method will run on a thread from the ThreadPool
        private void QueryServerWorkItem(
            object parameters
        ) {
            // this method has to work at a very low level because the connection pool isn't set up yet
            var args = (QueryServerParameters) parameters;
            var results = new QueryServerResults();
            results.Address = args.Address;

            try {
                var connection = new MongoConnection(null, args.Address); // no connection pool
                var command = new BsonDocument("ismaster", 1);
                var message = new MongoQueryMessage(
                    "admin.$cmd",
                    QueryFlags.SlaveOk,
                    0, // numberToSkip
                    1, // numberToReturn
                    command,
                    null // fields
                );
                connection.SendMessage(message, SafeMode.False);
                var reply = connection.ReceiveMessage<BsonDocument>();
                var commandResult = reply.Documents[0];
                results.CommandResult = commandResult;

                if (
                    commandResult.GetAsBoolean("ok", false) &&
                    commandResult.GetAsBoolean("ismaster", false)
                ) {
                    results.Connection = connection; // will become the first connection in the connection pool
                } else {
                    connection.Dispose();
                }
            } catch (Exception ex) {
                results.Exception = ex;
            }

            args.ResultsQueue.Enqueue(results);
        }
        #endregion

        #region internal methods
        internal MongoConnectionPool GetConnectionPool() {
            lock (serverLock) {
                if (connectionPool == null) {
                    Connect();
                }
                return connectionPool;
            }
        }
        #endregion

        #region private nested classes
        private class QueryServerParameters {
            public MongoServerAddress Address { get; set; }
            public BlockingQueue<QueryServerResults> ResultsQueue { get; set; }
        }

        private class QueryServerResults {
            public MongoServerAddress Address { get; set; }
            public MongoConnection Connection { get; set; }
            public BsonDocument CommandResult { get; set; }
            public Exception Exception { get; set; }
        }
        #endregion
    }
}
