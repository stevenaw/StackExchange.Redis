﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace StackExchange.Redis
{
    abstract class ResultProcessor
    {
        public static readonly ResultProcessor<bool>
            Boolean = new BooleanProcessor(),
            DemandOK = new ExpectBasicStringProcessor(RedisLiterals.OK),
            DemandPONG = new ExpectBasicStringProcessor("PONG"),
            DemandZeroOrOne = new DemandZeroOrOneProcessor(),
            AutoConfigure = new AutoConfigureProcessor(),
            EstablishConnection = new EstablishConnectionProcessor(),
            TrackSubscriptions = new TrackSubscriptionsProcessor(),
            Tracer = new TracerProcessor();

        public static readonly ResultProcessor<double>
            Double = new DoubleProcessor();
        public static readonly ResultProcessor<double?>
            NullableDouble = new NullableDoubleProcessor();

        public static readonly ResultProcessor<byte[]>
            ByteArray = new ByteArrayProcessor(),
            ScriptLoad = new ScriptLoadProcessor();

        public static readonly ResultProcessor<ClusterConfiguration>
            ClusterNodes = new ClusterNodesProcessor();
        public static readonly ResultProcessor<EndPoint>
            ConnectionIdentity = new ConnectionIdentityProcessor();

        public static readonly ResultProcessor<DateTime>
            DateTime = new DateTimeProcessor();

        public static readonly ResultProcessor<IGrouping<string, KeyValuePair<string, string>>[]>
            Info = new InfoProcessor();

        public static readonly ResultProcessor<long>
            Int64 = new Int64Processor();

        public static readonly ResultProcessor<long?>
            NullableInt64 = new NullableInt64Processor();

        public static readonly ResultProcessor<RedisKey>
            RedisKey = new RedisKeyProcessor();

        public static readonly ResultProcessor<RedisKey[]>
            RedisKeyArray = new RedisKeyArrayProcessor();

        public static readonly ResultProcessor<RedisType>
            RedisType = new RedisTypeProcessor();

        public static readonly ResultProcessor<RedisValue>
            RedisValue = new RedisValueProcessor();

        public static readonly ResultProcessor<RedisValue[]>
            RedisValueArray = new RedisValueArrayProcessor();

        public static readonly ResultProcessor<RedisChannel[]>
            RedisChannelArray = new RedisChannelArrayProcessor();

        public static readonly ResultProcessor<TimeSpan>
            ResponseTimer = new TimingProcessor();

        public static readonly ResultProcessor<string>
            String = new StringProcessor(),
            ClusterNodesRaw = new ClusterNodesRawProcessor();
        public static readonly ResultProcessor<KeyValuePair<string, string>[]>
            StringPairInterleaved = new StringPairInterleavedProcessor();
        public static readonly TimeSpanProcessor
            TimeSpanFromMilliseconds = new TimeSpanProcessor(true),
            TimeSpanFromSeconds = new TimeSpanProcessor(false);
        public static readonly ResultProcessor<KeyValuePair<RedisValue, RedisValue>[]>
            ValuePairInterleaved = new ValuePairInterleavedProcessor();

        public static readonly ResultProcessor<KeyValuePair<RedisValue, double>[]>
            SortedSetWithScores = new SortedSetWithScoresProcessor();

        public static readonly ResultProcessor<RedisResult>
            ScriptResult = new ScriptResultProcessor();


        static readonly byte[] MOVED = Encoding.UTF8.GetBytes("MOVED "), ASK = Encoding.UTF8.GetBytes("ASK ");

        static readonly char[] space = { ' ' };

        

        public void ConnectionFail(Message message, ConnectionFailureType fail, Exception innerException)
        {
            PhysicalConnection.IdentifyFailureType(innerException, ref fail);

            string exMessage = fail.ToString() + (message == null ? "" : (" on " + message.Command));
            var ex = innerException == null ? new RedisConnectionException(fail, exMessage)
                : new RedisConnectionException(fail, exMessage, innerException);
            SetException(message, ex);
        }

        public void ConnectionFail(Message message, ConnectionFailureType fail, string errorMessage)
        {
            SetException(message, new RedisConnectionException(fail, errorMessage));
        }

        public void ServerFail(Message message, string errorMessage)
        {
            SetException(message, new RedisServerException(errorMessage));
        }

        public void SetException(Message message, Exception ex)
        {
            var box = message == null ? null : message.ResultBox;
            if (box != null) box.SetException(ex);
        }
        // true if ready to be completed (i.e. false if re-issued to another server)
        public virtual bool SetResult(PhysicalConnection connection, Message message, RawResult result)
        {
            if (result.IsError)
            {
                var bridge = connection.Bridge;
                var server = bridge.ServerEndPoint;
                bool log = !message.IsInternalCall;
                bool isMoved = result.AssertStarts(MOVED);
                if (isMoved || result.AssertStarts(ASK))
                {
                    log = false;
                    string[] parts = result.GetString().Split(StringSplits.Space, 3);
                    int hashSlot;
                    EndPoint endpoint;
                    if (Format.TryParseInt32(parts[1], out hashSlot) &&
                        (endpoint = Format.TryParseEndPoint(parts[2])) != null)
                    {
                        
                        // no point sending back to same server, and no point sending to a dead server
                        if (!Equals(server.EndPoint, endpoint))
                        {
                            if (bridge.Multiplexer.TryResend(hashSlot, message, endpoint, isMoved))
                            {
                                connection.Multiplexer.Trace(message.Command + " re-issued to " + endpoint, isMoved ? "MOVED" : "ASK");
                                return false;
                            }
                        }
                    }
                }

                string err = result.GetString();
                if (log)
                {
                    bridge.Multiplexer.OnErrorMessage(server.EndPoint, err);
                }
                connection.Multiplexer.Trace("Completed with error: " + err + " (" + GetType().Name + ")", ToString());
                ServerFail(message, err);
            }
            else
            {
                bool coreResult = SetResultCore(connection, message, result);
                if (coreResult)
                {
                    connection.Multiplexer.Trace("Completed with success: " + result.ToString() + " (" + GetType().Name + ")", ToString());
                }
                else
                {
                    UnexpectedResponse(message, result);
                }
            }
            return true;
        }
        protected abstract bool SetResultCore(PhysicalConnection connection, Message message, RawResult result);

        private void UnexpectedResponse(Message message, RawResult result)
        {
            ConnectionMultiplexer.TraceWithoutContext("From " + GetType().Name, "Unexpected Response");
            ConnectionFail(message, ConnectionFailureType.ProtocolFailure, "Unexpected response to " + (message == null ? "n/a" : message.Command.ToString()) +": " + result.ToString());
        }

        public sealed class TrackSubscriptionsProcessor : ResultProcessor<bool>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if(result.Type == ResultType.Array)
                {
                    var items = result.GetItems();
                    long count;
                    if (items.Length >= 3 && items[2].TryGetInt64(out count))
                    {
                        connection.SubscriptionCount = count;
                        return true;
                    }
                }
                return false;
            }
        }
        
        public sealed class TimingProcessor : ResultProcessor<TimeSpan>
        {
            public static Message CreateMessage(CommandFlags flags, RedisCommand command, RedisValue value = default(RedisValue))
            {
                return new TimerMessage(flags, command, value);
            }
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if (result.Type == ResultType.Error)
                {
                    return false;
                }
                else
                {
                    var timingMessage = message as TimerMessage;
                    TimeSpan duration;
                    if (timingMessage != null)
                    {
                        var watch = timingMessage.Watch;
                        watch.Stop();
                        duration = watch.Elapsed;
                    }
                    else
                    {
                        duration = TimeSpan.MaxValue;
                    }
                    SetResult(message, duration);
                    return true;
                }
            }

            sealed class TimerMessage : Message
            {
                public readonly Stopwatch Watch;
                private readonly RedisValue value;
                public TimerMessage(CommandFlags flags, RedisCommand command, RedisValue value)
                    : base(-1, flags, command)
                {
                    this.Watch = Stopwatch.StartNew();
                    this.value = value;
                }
                internal override void WriteImpl(PhysicalConnection physical)
                {
                    if (value.IsNull)
                    {
                        physical.WriteHeader(command, 0);
                    }
                    else
                    {
                        physical.WriteHeader(command, 1);
                        physical.Write(value);
                    }
                }
            }
        }

        internal sealed class DemandZeroOrOneProcessor : ResultProcessor<bool>
        {
            static readonly byte[] zero = { (byte)'0' }, one = { (byte)'1' };

            public static bool TryGet(RawResult result, out bool value)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        if (result.Assert(one)) { value = true; return true; }
                        else if (result.Assert(zero)) { value = false; return true; }
                        break;
                }
                value = false;
                return false;
            }
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                bool value;
                if (TryGet(result, out value))
                {
                    SetResult(message, value);
                    return true;
                }
                return false;
            }
        }

        sealed class AutoConfigureProcessor : ResultProcessor<bool>
        {
            static readonly byte[] READONLY = Encoding.UTF8.GetBytes("READONLY ");
            public override bool SetResult(PhysicalConnection connection, Message message, RawResult result)
            {
                if(result.IsError && result.AssertStarts(READONLY))
                {
                    var server = connection.Bridge.ServerEndPoint;
                    server.Multiplexer.Trace("Auto-configured role: slave");
                    server.IsSlave = true;
                }
                return base.SetResult(connection, message, result);
            }
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                var server = connection.Bridge.ServerEndPoint;
                switch (result.Type)
                {
                    case ResultType.BulkString:
                        if (message != null && message.Command == RedisCommand.INFO)
                        {
                            string info = result.GetString(), line;
                            if (string.IsNullOrWhiteSpace(info))
                            {
                                SetResult(message, true);
                                return true;
                            }
                            using (var reader = new StringReader(info))
                            {
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("# ")) continue;

                                    string val;
                                    if ((val = Extract(line, "role:")) != null)
                                    {
                                        switch (val)
                                        {
                                            case "master":
                                                server.IsSlave = false;
                                                server.Multiplexer.Trace("Auto-configured role: master");
                                                break;
                                            case "slave":
                                                server.IsSlave = true;
                                                server.Multiplexer.Trace("Auto-configured role: slave");
                                                break;
                                        }
                                    }
                                    else if ((val = Extract(line, "redis_version:")) != null)
                                    {
                                        Version version;
                                        if (Version.TryParse(val, out version))
                                        {
                                            server.Version = version;
                                            server.Multiplexer.Trace("Auto-configured version: " + version);
                                        }
                                    }
                                    else if ((val = Extract(line, "redis_mode:")) != null)
                                    {
                                        switch (val)
                                        {
                                            case "standalone":
                                                server.ServerType = ServerType.Standalone;
                                                server.Multiplexer.Trace("Auto-configured server-type: standalone");
                                                break;
                                            case "cluster":
                                                server.ServerType = ServerType.Cluster;
                                                server.Multiplexer.Trace("Auto-configured server-type: cluster");
                                                break;
                                            case "sentinel":
                                                server.ServerType = ServerType.Sentinel;
                                                server.Multiplexer.Trace("Auto-configured server-type: sentinel");
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                        SetResult(message, true);
                        return true;
                    case ResultType.Array:
                        if (message != null && message.Command == RedisCommand.CONFIG)
                        {
                            var arr = result.GetItems();
                            int count = arr.Length / 2;

                            byte[] timeout = (byte[])RedisLiterals.timeout,
                                databases = (byte[])RedisLiterals.databases,
                                slave_read_only = (byte[])RedisLiterals.slave_read_only,
                                yes = (byte[])RedisLiterals.yes,
                                no = (byte[])RedisLiterals.no;

                            long i64;
                            for (int i = 0; i < count; i++)
                            {
                                var key = arr[i * 2];
                                if (key.Assert(timeout) && arr[(i * 2) + 1].TryGetInt64(out i64))
                                {
                                    // note the configuration is in seconds
                                    int timeoutSeconds = checked((int)i64), targetSeconds;
                                    if (timeoutSeconds > 0)
                                    {
                                        if (timeoutSeconds >= 60)
                                        {
                                            targetSeconds = timeoutSeconds - 20; // time to spare...
                                        }
                                        else
                                        {
                                            targetSeconds = (timeoutSeconds * 3) / 4;
                                        }
                                        server.Multiplexer.Trace("Auto-configured timeout: " + targetSeconds + "s");
                                        server.WriteEverySeconds = targetSeconds;
                                    }
                                }
                                else if (key.Assert(databases) && arr[(i * 2) + 1].TryGetInt64(out i64))
                                {
                                    int dbCount = checked((int)i64);
                                    server.Multiplexer.Trace("Auto-configured databases: " + dbCount);
                                    server.Databases = dbCount;
                                }
                                else if (key.Assert(slave_read_only))
                                {
                                    var val = arr[(i * 2) + 1];
                                    if (val.Assert(yes))
                                    {
                                        server.SlaveReadOnly = true;
                                        server.Multiplexer.Trace("Auto-configured slave-read-only: true");
                                    }
                                    else if (val.Assert(no))
                                    {
                                        server.SlaveReadOnly = false;
                                        server.Multiplexer.Trace("Auto-configured slave-read-only: false");
                                    }

                                }
                            }
                        }
                        SetResult(message, true);
                        return true;
                }
                return false;
            }

            static string Extract(string line, string prefix)
            {
                if (line.StartsWith(prefix)) return line.Substring(prefix.Length).Trim();
                return null;
            }
        }
        sealed class DoubleProcessor : ResultProcessor<double>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                        long i64;
                        if (result.TryGetInt64(out i64))
                        {
                            SetResult(message, i64);
                            return true;
                        }
                        break;
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        double val;
                        if (result.TryGetDouble(out val))
                        {
                            SetResult(message, val);
                            return true;
                        }
                        break;
                }
                return false;
            }
        }
        sealed class NullableDoubleProcessor : ResultProcessor<double?>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        if(result.IsNull)
                        {
                            SetResult(message, null);
                            return true;
                        }
                        double val;
                        if (result.TryGetDouble(out val))
                        {
                            SetResult(message, val);
                            return true;
                        }
                        break;
                }
                return false;
            }
        }
        sealed class BooleanProcessor : ResultProcessor<bool>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if (result.IsNull)
                {
                    SetResult(message, false); // lots of ops return (nil) when they mean "no"
                    return true;
                }
                switch (result.Type)
                {
                    case ResultType.SimpleString:
                        if(result.Assert(RedisLiterals.OK))
                        {
                            SetResult(message, true);
                        } else
                        {
                            SetResult(message, result.GetBoolean());
                        }
                        return true;
                    case ResultType.Integer:                    
                    case ResultType.BulkString:
                        SetResult(message, result.GetBoolean());
                        return true;
                    case ResultType.Array:
                        var items = result.GetItems();
                        if(items.Length == 1)
                        { // treat an array of 1 like a single reply (for example, SCRIPT EXISTS)
                            SetResult(message, items[0].GetBoolean());
                            return true;
                        }
                        break;
                }
                return false;
            }
        }

        sealed class ByteArrayProcessor : ResultProcessor<byte[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.BulkString:
                        SetResult(message, result.GetBlob());
                        return true;
                }
                return false;
            }
        }

        internal sealed class ScriptLoadProcessor : ResultProcessor<byte[]>
        {
            // note that top-level error messages still get handled by SetResult, but nested errors
            // (is that a thing?) will be wrapped in the RedisResult
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.BulkString:
                        var hash = result.GetBlob();
                        var sl = message as RedisDatabase.ScriptLoadMessage;
                        if (sl != null)
                        {
                            connection.Bridge.ServerEndPoint.AddScript(sl.Script, hash);
                        }
                        SetResult(message, hash);
                        return true;
                }
                return false;
            }
        }

        sealed class ClusterNodesProcessor : ResultProcessor<ClusterConfiguration>
        {
            internal static ClusterConfiguration Parse(PhysicalConnection connection, string nodes)
            {
                var server = connection.Bridge.ServerEndPoint;
                var config = new ClusterConfiguration(connection.Multiplexer.ServerSelectionStrategy, nodes, server.EndPoint);
                server.SetClusterConfiguration(config);
                return config;
            }

            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.BulkString:
                        string nodes = result.GetString();
                        connection.Bridge.ServerEndPoint.ServerType = ServerType.Cluster;
                        var config = Parse(connection, nodes);
                        SetResult(message, config);
                        return true;
                }
                return false;
            }
        }

        sealed class ClusterNodesRawProcessor : ResultProcessor<string>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        string nodes = result.GetString();
                        try
                        { ClusterNodesProcessor.Parse(connection, nodes); }
                        catch
                        { /* tralalalala */}
                        SetResult(message, nodes);
                        return true;
                }
                return false;
            }
        }

        private sealed class ConnectionIdentityProcessor : ResultProcessor<EndPoint>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                SetResult(message, connection.Bridge.ServerEndPoint.EndPoint);
                return true;
            }
        }
        sealed class DateTimeProcessor : ResultProcessor<DateTime>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                long unixTime, micros;
                switch (result.Type)
                {
                    case ResultType.Integer:
                        if(result.TryGetInt64(out unixTime))
                        {
                            var time = RedisBase.UnixEpoch.AddSeconds(unixTime);
                            SetResult(message, time);
                            return true;
                        }
                        break;
                    case ResultType.Array:
                        var arr = result.GetItems();
                        switch(arr.Length)
                        {
                            case 1:
                                if (arr[0].TryGetInt64(out unixTime))
                                {
                                    var time = RedisBase.UnixEpoch.AddSeconds(unixTime);
                                    SetResult(message, time);
                                    return true;
                                }
                                break;
                            case 2:
                                if(arr[0].TryGetInt64(out unixTime) && arr[1].TryGetInt64(out micros))
                                {
                                    var time = RedisBase.UnixEpoch.AddSeconds(unixTime).AddTicks(micros * 10); // datetime ticks are 100ns
                                    SetResult(message, time);
                                    return true;
                                }
                                break;
                        }
                        break;
                }
                return false;
            }
        }

        sealed class EstablishConnectionProcessor : ResultProcessor<bool>
        {
            static readonly byte[] expected = Encoding.UTF8.GetBytes("PONG"), authFail = Encoding.UTF8.GetBytes("ERR operation not permitted");
            public override bool SetResult(PhysicalConnection connection, Message message, RawResult result)
            {
                var final = base.SetResult(connection, message, result);
                if (result.IsError)
                {
                    if (result.Assert(authFail))
                    {
                        connection.RecordConnectionFailed(ConnectionFailureType.AuthenticationFailure);
                    }
                    else
                    {
                        connection.RecordConnectionFailed(ConnectionFailureType.ProtocolFailure);
                    }
                }
                return final;
            }

            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if(result.Assert(expected))
                {
                    connection.Bridge.OnFullyEstablished(connection);
                    SetResult(message, true);
                    return true;
                }
                else
                {
                    connection.RecordConnectionFailed(ConnectionFailureType.ProtocolFailure);
                    return false;
                }
            }
        }
        sealed class ExpectBasicStringProcessor : ResultProcessor<bool>
        {
            private readonly byte[] expected;
            public ExpectBasicStringProcessor(string value)
            {
                expected = Encoding.UTF8.GetBytes(value);
            }
            public ExpectBasicStringProcessor(byte[] value)
            {
                expected = value;
            }
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if (result.Assert(expected))
                {
                    SetResult(message, true);
                    return true;
                }
                return false;
            }
        }
        sealed class InfoProcessor : ResultProcessor<IGrouping<string, KeyValuePair<string, string>>[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if (result.Type == ResultType.BulkString)
                {
                    string category = Normalize(null), line;
                    var list = new List<Tuple<string, KeyValuePair<string, string>>>();
                    using (var reader = new StringReader(result.GetString()))
                    {
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            if (line.StartsWith("# "))
                            {
                                category = Normalize(line.Substring(2));
                                continue;
                            }
                            int idx = line.IndexOf(':');
                            if (idx < 0) continue;
                            var pair = new KeyValuePair<string, string>(
                                line.Substring(0, idx).Trim(),
                                line.Substring(idx + 1).Trim());
                            list.Add(Tuple.Create(category, pair));
                        }
                    }
                    var final = list.GroupBy(x => x.Item1, x => x.Item2).ToArray();
                    SetResult(message, final);
                    return true;
                }
                return false;
            }

            static string Normalize(string category)
            {
                return string.IsNullOrWhiteSpace(category) ? "miscellaneous" : category.Trim();
            }
        }

        sealed class Int64Processor : ResultProcessor<long>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        long i64;
                        if (result.TryGetInt64(out i64))
                        {
                            SetResult(message, i64);
                            return true;
                        }
                        break;
                }
                return false;
            }
        }
        sealed class NullableInt64Processor : ResultProcessor<long?>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        if(result.IsNull)
                        {
                            SetResult(message, null);
                            return true;
                        }
                        long i64;
                        if (result.TryGetInt64(out i64))
                        {
                            SetResult(message, i64);
                            return true;
                        }
                        break;
                }
                return false;
            }
        }

        sealed class RedisKeyArrayProcessor : ResultProcessor<RedisKey[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Array:
                        var arr = result.GetItemsAsKeys();
                        SetResult(message, arr);
                        return true;
                }
                return false;
            }
        }

        sealed class RedisKeyProcessor : ResultProcessor<RedisKey>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        SetResult(message, result.AsRedisKey());
                        return true;
                }
                return false;
            }
        }

        sealed class RedisTypeProcessor : ResultProcessor<RedisType>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        string s = result.GetString();
                        RedisType value;
                        if (!Enum.TryParse<RedisType>(s, true, out value)) value = global::StackExchange.Redis.RedisType.Unknown;
                        SetResult(message, value);
                        return true;
                }
                return false;
            }
        }

        sealed class RedisValueArrayProcessor : ResultProcessor<RedisValue[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Array:
                        var arr = result.GetItemsAsValues();

                        SetResult(message, arr);
                        return true;
                }
                return false;
            }
        }
        sealed class RedisChannelArrayProcessor : ResultProcessor<RedisChannel[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Array:
                        var arr = result.GetItems();
                        RedisChannel[] final;
                        if (arr.Length == 0)
                        {
                            final = RedisChannel.EmptyArray;
                        }
                        else
                        {
                            final = new RedisChannel[arr.Length];
                            byte[] channelPrefix = connection.ChannelPrefix;
                            for (int i = 0; i < final.Length; i++)
                            {
                                final[i] = result.AsRedisChannel(channelPrefix);
                            }
                        }
                        SetResult(message, final);
                        return true;
                }
                return false;
            }
        }

        sealed class RedisValueProcessor : ResultProcessor<RedisValue>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch(result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        SetResult(message, result.AsRedisValue());
                        return true;
                }
                return false;
            }
        }
        sealed class StringPairInterleavedProcessor : ValuePairInterleavedProcessorBase<string, string>
        {
            protected override string ParseKey(RawResult key) { return key.GetString(); }
            protected override string ParseValue(RawResult key) { return key.GetString(); }
        }

        sealed class StringProcessor : ResultProcessor<string>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                    case ResultType.SimpleString:
                    case ResultType.BulkString:
                        SetResult(message, result.GetString());
                        return true;
                }
                return false;
            }
        }
        public sealed class TimeSpanProcessor : ResultProcessor<TimeSpan?>
        {
            private readonly bool isMilliseconds;
            public TimeSpanProcessor(bool isMilliseconds)
            {
                this.isMilliseconds = isMilliseconds;
            }
            public bool TryParse(RawResult result, out TimeSpan? expiry)
            {
                switch (result.Type)
                {
                    case ResultType.Integer:
                        long time;
                        if (result.TryGetInt64(out time))
                        {
                            if (time < 0)
                            {
                                expiry = null;
                            }
                            else if (isMilliseconds)
                            {
                                expiry = TimeSpan.FromMilliseconds(time);
                            }
                            else
                            {
                                expiry = TimeSpan.FromSeconds(time);
                            }
                            return true;
                        }
                        break;
                }
                expiry = null;
                return false;
            }
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                TimeSpan? expiry;
                if(TryParse(result, out expiry))
                {
                    SetResult(message, expiry);
                    return true;
                }
                return false;
            }
        }

        sealed class ValuePairInterleavedProcessor : ValuePairInterleavedProcessorBase<RedisValue, RedisValue>
        {
            protected override RedisValue ParseKey(RawResult key) { return key.AsRedisValue(); }
            protected override RedisValue ParseValue(RawResult key) { return key.AsRedisValue(); }
        }

        abstract class ValuePairInterleavedProcessorBase<TKey, TValue> : ResultProcessor<KeyValuePair<TKey, TValue>[]>
        {
            static readonly KeyValuePair<TKey, TValue>[] nix = new KeyValuePair<TKey, TValue>[0];

            protected abstract TKey ParseKey(RawResult key);
            protected abstract TValue ParseValue(RawResult value);
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                switch (result.Type)
                {
                    case ResultType.Array:
                        var arr = result.GetItems();
                        int count = arr.Length / 2;
                        KeyValuePair<TKey, TValue>[] pairs;
                        if (count == 0)
                        {
                            pairs = nix;
                        }
                        else
                        {
                            pairs = new KeyValuePair<TKey, TValue>[count];
                            int offset = 0;
                            for (int i = 0; i < pairs.Length; i++)
                            {
                                var setting = ParseKey(arr[offset++]);
                                var value = ParseValue(arr[offset++]);
                                pairs[i] = new KeyValuePair<TKey, TValue>(setting, value);
                            }
                        }
                        SetResult(message, pairs);
                        return true;
                    default:
                        return false;
                }
            }
        }

        private class SortedSetWithScoresProcessor : ResultProcessor<KeyValuePair<RedisValue, double>[]>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                if(result.Type == ResultType.Array)
                {
                    var items = result.GetItems();
                    var arr = new KeyValuePair<RedisValue, double>[items.Length / 2];
                    int index = 0;
                    for(int i = 0; i < arr.Length; i++)
                    {
                        var member = items[index++].AsRedisValue();
                        double score;
                        if (!items[index++].TryGetDouble(out score)) return false;
                        arr[i] = new KeyValuePair<RedisValue, double>(member, score);
                    }
                    SetResult(message, arr);
                    return true;
                }
                return false;
            }
        }

        private class TracerProcessor : ResultProcessor<bool>
        {
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                bool happy = result.Type == ResultType.BulkString && result.Assert(connection.Multiplexer.UniqueId);
                SetResult(message, happy);
                return true; // we'll always acknowledge that we saw a non-error response
            }
        }

        private class ScriptResultProcessor : ResultProcessor<RedisResult>
        {
            static readonly byte[] NOSCRIPT = Encoding.UTF8.GetBytes("NOSCRIPT ");
            public override bool SetResult(PhysicalConnection connection, Message message, RawResult result)
            {
                if(result.Type == ResultType.Error && result.AssertStarts(NOSCRIPT))
                { // scripts are not flushed individually, so assume the entire script cache is toast ("SCRIPT FLUSH")
                    connection.Bridge.ServerEndPoint.FlushScripts();
                }
                // and apply usual processing for the rest
                return base.SetResult(connection, message, result);
            }

            // note that top-level error messages still get handled by SetResult, but nested errors
            // (is that a thing?) will be wrapped in the RedisResult
            protected override bool SetResultCore(PhysicalConnection connection, Message message, RawResult result)
            {
                var value = Redis.RedisResult.TryCreate(connection, result);
                if(value != null)
                {
                    SetResult(message, value);
                    return true;
                }
                return false;
            }
        }
    }
    internal abstract class ResultProcessor<T> : ResultProcessor
    {

        protected void SetResult(Message message, T value)
        {
            if (message == null) return;
            var box = message.ResultBox as ResultBox<T>;
            if (box != null) box.SetResult(value);            
        }
    }
}
