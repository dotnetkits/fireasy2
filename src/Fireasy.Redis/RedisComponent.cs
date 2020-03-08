﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------
using Fireasy.Common;
using Fireasy.Common.Configuration;
using Fireasy.Common.Extensions;
#if NETSTANDARD
using CSRedis;
using System.Text;
#else
using StackExchange.Redis;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using Fireasy.Common.ComponentModel;
using System.Text.RegularExpressions;

namespace Fireasy.Redis
{
    /// <summary>
    /// Redis 组件抽象类。
    /// </summary>
    public abstract class RedisComponent : DisposeableBase, IConfigurationSettingHostService
    {
        private List<int> dbRanage;
        private Func<string, string> captureRule;

#if NETSTANDARD
        private readonly List<string> connectionStrs = new List<string>();
        private readonly SafetyDictionary<int, CSRedisClient> clients = new SafetyDictionary<int, CSRedisClient>();
#else
        private Lazy<ConnectionMultiplexer> connectionLazy;

        protected ConfigurationOptions Options { get; private set; }
#endif

        /// <summary>
        /// 获取 Redis 的相关配置。
        /// </summary>
        protected RedisConfigurationSetting Setting { get; private set; }

        /// <summary>
        /// 序列化对象。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual T Deserialize<T>(string value)
        {
            var serializer = CreateSerializer();
            return serializer.Deserialize<T>(value);
        }

        /// <summary>
        /// 序列化对象。
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual object Deserialize(Type type, string value)
        {
            var serializer = CreateSerializer();
            return serializer.Deserialize(type, value);
        }

        /// <summary>
        /// 反序列化。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        protected virtual string Serialize<T>(T value)
        {
            var serializer = CreateSerializer();
            return serializer.Serialize(value);
        }

        private RedisSerializer CreateSerializer()
        {
            if (Setting.SerializerType != null)
            {
                var serializer = Setting.SerializerType.New<RedisSerializer>();
                if (serializer != null)
                {
                    return serializer;
                }
            }

            return new RedisSerializer();
        }

#if NETSTANDARD
        protected CSRedisClient GetConnection(string key = null)
        {
            if (string.IsNullOrEmpty(key) || dbRanage == null)
            {
                return clients[0] ?? throw new InvalidOperationException("不能初始化 Redis 的连接。");
            }

            var ckey = captureRule == null ? key : captureRule(key);
            var index = GetModulus(ckey, dbRanage.Count);
            return clients.GetOrAdd(index, k =>
            {
                return new CSRedisClient(null, connectionStrs.Select(s => string.Concat(s, $",defaultDatabase={dbRanage[k]}")).ToArray());
            });
        }

        protected IEnumerable<CSRedisClient> GetConnections()
        {
            return clients.Values;
        }

#else
        protected IConnectionMultiplexer GetConnection()
        {
            return connectionLazy.Value ?? throw new InvalidOperationException("不能初始化 Redis 的连接。");
        }

        protected IDatabase GetDatabase(string key)
        {
            var client = connectionLazy.Value;
            var ckey = captureRule == null ? key : captureRule(key);
            var index = dbRanage != null ? GetModulus(ckey, dbRanage.Count) : (Options.DefaultDatabase ?? 0);

            return client.GetDatabase(index);
        }

        protected IEnumerable<IDatabase> GetDatabases()
        {
            var client = connectionLazy.Value;
            foreach (var index in dbRanage)
            {
                yield return client.GetDatabase(index);
            }
        }
#endif

        void IConfigurationSettingHostService.Attach(IConfigurationSettingItem setting)
        {
            Setting = (RedisConfigurationSetting)setting;

#if NETSTANDARD
            if (!string.IsNullOrEmpty(Setting.ConnectionString))
            {
                connectionStrs.Add(Setting.ConnectionString);
            }
            else
            {
                foreach (var host in Setting.Hosts)
                {
                    var connStr = new StringBuilder($"{host.Server}");

            #region connection build
                    if (host.Port != 0)
                    {
                        connStr.Append($":{host.Port}");
                    }

                    if (!string.IsNullOrEmpty(Setting.Password))
                    {
                        connStr.Append($",password={Setting.Password}");
                    }

                    if (Setting.DefaultDb != 0 && string.IsNullOrEmpty(Setting.DbRange))
                    {
                        connStr.Append($",defaultDatabase={Setting.DefaultDb}");
                    }

                    if (Setting.Ssl)
                    {
                        connStr.Append($",ssl=true");
                    }

                    if (Setting.WriteBuffer != null)
                    {
                        connStr.Append($",writeBuffer={Setting.WriteBuffer}");
                    }

                    if (Setting.PoolSize != null)
                    {
                        connStr.Append($",poolsize={Setting.PoolSize}");
                    }

                    if (Setting.ConnectTimeout != 5000)
                    {
                        connStr.Append($",connectTimeout={Setting.ConnectTimeout}");
                    }

                    if (Setting.SyncTimeout != 10000)
                    {
                        connStr.Append($",syncTimeout={Setting.SyncTimeout}");
                    }

                    connStr.Append(",allowAdmin=true");
            #endregion

                    connectionStrs.Add(connStr.ToString());
                }
            }

            if (string.IsNullOrEmpty(Setting.DbRange))
            {
                clients.GetOrAdd(0, () => new CSRedisClient(null, connectionStrs.ToArray()));
            }
            else
            {
                ParseDbRange(Setting.DbRange);
                if (!string.IsNullOrEmpty(Setting.KeyRule))
                {
                    ParseKeyCapture(Setting.KeyRule);
                }
            }
#else
            if (!string.IsNullOrEmpty(Setting.ConnectionString))
            {
                Options = ConfigurationOptions.Parse(Setting.ConnectionString);
            }
            else
            {
                Options = new ConfigurationOptions
                {
                    DefaultDatabase = Setting.DefaultDb == 0 || !string.IsNullOrEmpty(Setting.DbRange) ? (int?)null : Setting.DefaultDb,
                    Password = Setting.Password,
                    AllowAdmin = true,
                    Ssl = Setting.Ssl,
                    Proxy = Setting.Twemproxy ? Proxy.Twemproxy : Proxy.None,
                    AbortOnConnectFail = false,
                };

                if (Setting.WriteBuffer != null)
                {
                    Options.WriteBuffer = (int)Setting.WriteBuffer;
                }

                if (Setting.ConnectTimeout != 5000)
                {
                    Options.ConnectTimeout = Setting.ConnectTimeout;
                }

                if (Setting.SyncTimeout != 10000)
                {
                    Options.SyncTimeout = Setting.SyncTimeout;
                }

                foreach (var host in Setting.Hosts)
                {
                    if (host.Port == 0)
                    {
                        Options.EndPoints.Add(host.Server);
                    }
                    else
                    {
                        Options.EndPoints.Add(host.Server, host.Port);
                    }
                }
            }

            if (!string.IsNullOrEmpty(Setting.DbRange))
            {
                ParseDbRange(Setting.DbRange);
                if (!string.IsNullOrEmpty(Setting.KeyRule))
                {
                    ParseKeyCapture(Setting.KeyRule);
                }
            }

            if (connectionLazy == null)
            {
                connectionLazy = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(Options));
            }
#endif
        }

        IConfigurationSettingItem IConfigurationSettingHostService.GetSetting()
        {
            return Setting;
        }

        private int GetModulus(string str, int mod)
        {
            var hash = 0;
            foreach (var c in str)
            {
                hash = (128 * hash % (mod * 8)) + c;
            }

            return hash % mod;
        }

        private void ParseDbRange(string range)
        {
            dbRanage = new List<int>();
            foreach (var s1 in range.Split(','))
            {
                int p;
                if ((p = s1.IndexOf('-')) != -1)
                {
                    var start = s1.Substring(0, p).To<int>();
                    var end = s1.Substring(p + 1).To<int>();
                    for (var i = start; i <= end; i++)
                    {
                        dbRanage.Add(i);
                    }
                }
                else
                {
                    dbRanage.Add(s1.To<int>());
                }
            }
        }

        private void ParseKeyCapture(string rule)
        {
            if (rule.StartsWith("left", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+)");
                var length = match.Groups[1].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Left(length));
            }
            if (rule.StartsWith("right", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+)");
                var length = match.Groups[1].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Right(length));
            }
            else if (rule.StartsWith("substr", StringComparison.CurrentCultureIgnoreCase))
            {
                var match = Regex.Match(rule, @"(\d+),\s*(\d+)");
                var start = match.Groups[1].Value.To<int>();
                var length = match.Groups[2].Value.To<int>();
                captureRule = new Func<string, string>(k => k.Substring(start, start + length > k.Length ? k.Length - start : length));
            }
        }

        protected override void Dispose(bool disposing)
        {
#if NETSTANDARD
            foreach (var client in clients)
            {
                client.Value.Dispose();
            }

            clients.Clear();
#else
            if (connectionLazy.IsValueCreated)
            {
                connectionLazy.Value.Dispose();
            }
#endif
            base.Dispose(disposing);
        }
    }
}
