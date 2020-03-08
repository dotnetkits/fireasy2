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
using Fireasy.Data.Entity.Linq.Translators;
using Fireasy.Data.Entity.Linq.Translators.Configuration;
using Fireasy.Data.Provider;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Fireasy.Data.Entity.Linq
{
    /// <summary>
    /// 为实体提供 LINQ 查询的支持。无法继承此类。
    /// </summary>
    public sealed class EntityQueryProvider : IEntityQueryProvider, IProviderAware
    {
        private readonly IDatabase database;
        private readonly IContextService contextService;
        private static readonly ConcurrentDictionary<ParameterInfo[], Tuple<bool, bool>> attrCache = new ConcurrentDictionary<ParameterInfo[], Tuple<bool, bool>>(new ParametersComparer());

        /// <summary>
        /// 使用一个 <see cref="IDatabase"/> 对象初始化 <see cref="EntityQueryProvider"/> 类的新实例。
        /// </summary>
        /// <param name="contextService">一个 <see cref="IContextService"/> 对象。</param>
        public EntityQueryProvider(IContextService contextService)
        {
            Guard.ArgumentNull(contextService, nameof(contextService));

            if (contextService is IDatabaseAware aware)
            {
                database = aware.Database;
            }
            else
            {
                throw new InvalidOperationException(SR.GetString(SRKind.NotFoundDatabaseAware));
            }

            this.contextService = contextService;
            Provider = contextService.Provider;
            ServiceProvider = contextService.ServiceProvider;
            ContextOptions = contextService.Options;
        }

        /// <summary>
        /// 获取数据库提供者实例。
        /// </summary>
        public IProvider Provider { get; private set; }

        /// <summary>
        /// 获取应用程序服务提供者实例。
        /// </summary>
        public IServiceProvider ServiceProvider { get; private set; }

        /// <summary>
        /// 获取参数选项。
        /// </summary>
        public EntityContextOptions ContextOptions { get; private set; }

        /// <summary>
        /// 执行 <see cref="Expression"/> 的查询，返回查询结果。
        /// </summary>
        /// <param name="expression">表示 LINQ 查询的表达式树。</param>
        /// <returns>单值对象。</returns>
        /// <exception cref="TranslateException">对 LINQ 表达式解析失败时抛出此异常。</exception>
        public object Execute(Expression expression)
        {
            var translateCache = ServiceProvider.TryGetService(() => DefaultCacheParsingProcessor.Instance);
            var efn = translateCache.TryGetDelegate(expression, GetCacheOptions(), () => (LambdaExpression)GetExecutionPlan(expression));

            var attrs = GetMethodAttributes(efn.Method);

            if (!attrs.Item2)
            {
                return efn.DynamicInvoke(database);
            }

            var segment = SegmentFinder.Find(expression);

            return efn.DynamicInvoke(database, segment);
        }

        /// <summary>
        /// 执行 <see cref="Expression"/> 的查询，返回查询结果。
        /// </summary>
        /// <param name="expression">表示 LINQ 查询的表达式树。</param>
        /// <returns>单值对象。</returns>
        /// <exception cref="TranslateException">对 LINQ 表达式解析失败时抛出此异常。</exception>
        public TResult Execute<TResult>(Expression expression)
        {
            return (TResult)Execute(expression);
        }

        /// <summary>
        /// 执行 <see cref="Expression"/> 的查询，返回查询结果。
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="expression">表示 LINQ 查询的表达式树。</param>
        /// <returns>单值对象。</returns>
        /// <exception cref="TranslateException">对 LINQ 表达式解析失败时抛出此异常。</exception>
        public async Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var translateCache = ServiceProvider.TryGetService(() => DefaultCacheParsingProcessor.Instance);
            var efn = translateCache.TryGetDelegate(expression, GetCacheOptions(), () => (LambdaExpression)GetExecutionPlan(expression));

            var attrs = GetMethodAttributes(efn.Method);

            object result;
            if (attrs.Item1)
            {
                if (attrs.Item2)
                {
                    var segment = SegmentFinder.Find(expression);
                    result = efn.DynamicInvoke(database, segment, cancellationToken);
                }
                else
                {
                    result = efn.DynamicInvoke(database, cancellationToken);
                }
            }
            else
            {
                if (attrs.Item2)
                {
                    var segment = SegmentFinder.Find(expression);
                    result = efn.DynamicInvoke(database, segment);
                }
                else
                {
                    result = efn.DynamicInvoke(database);
                }
            }

            return await (Task<TResult>)result;
        }

#if !NETFRAMEWORK && !NETSTANDARD2_0
        public IAsyncEnumerable<TResult> ExecuteEnumerableAsync<TResult>(Expression expression, CancellationToken cancellationToken)
        {
            var translateCache = ServiceProvider.TryGetService(() => DefaultCacheParsingProcessor.Instance);
            var efn = translateCache.TryGetDelegate(expression, GetCacheOptions(), () => (LambdaExpression)GetExecutionPlan(expression, true));

            var attrs = GetMethodAttributes(efn.Method);

            object result;
            if (attrs.Item1)
            {
                if (attrs.Item2)
                {
                    var segment = SegmentFinder.Find(expression);
                    result = efn.DynamicInvoke(database, segment, cancellationToken);
                }
                else
                {
                    result = efn.DynamicInvoke(database, cancellationToken);
                }
            }
            else
            {
                if (attrs.Item2)
                {
                    var segment = SegmentFinder.Find(expression);
                    result = efn.DynamicInvoke(database, segment);
                }
                else
                {
                    result = efn.DynamicInvoke(database);
                }
            }

            return (IAsyncEnumerable<TResult>)result;
        }
#endif

        /// <summary>
        /// 获取表达式的执行计划。
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        public Expression GetExecutionPlan(Expression expression, bool? isAsync = null)
        {
            try
            {
                if (expression is LambdaExpression lambda)
                {
                    expression = lambda.Body;
                }

                var section = ConfigurationUnity.GetSection<TranslatorConfigurationSection>();
                var options = GetTranslateOptions();

                using var scope = new TranslateScope(contextService, options);
                var translation = scope.TranslateProvider.Translate(expression);
                var translator = scope.TranslateProvider.CreateTranslator();

                var buildOptions = new ExecutionBuilder.BuildOptions { IsAsync = isAsync, IsNoTracking = !options.TraceEntityState };
                return ExecutionBuilder.Build(translation, e => translator.Translate(e), buildOptions);
            }
            catch (Exception ex)
            {
                throw new TranslateException(expression, ex);
            }
        }

        /// <summary>
        /// 执行表达式的翻译。
        /// </summary>
        /// <param name="expression">表示 LINQ 查询的表达式树。</param>
        /// <returns>一个 <see cref="TranslateResult"/>。</returns>
        /// <param name="options">指定解析的选项。</param>
        /// <exception cref="TranslateException">对 LINQ 表达式解析失败时抛出此异常。</exception>
        public TranslateResult Translate(Expression expression, TranslateOptions options = null)
        {
            try
            {
                if (expression is LambdaExpression lambda)
                {
                    expression = lambda.Body;
                }

                options ??= GetTranslateOptions();

                using var scope = new TranslateScope(contextService, options);
                var translation = scope.TranslateProvider.Translate(expression);
                var translator = scope.TranslateProvider.CreateTranslator();

                TranslateResult result;
                var selects = SelectGatherer.Gather(translation).ToList();
                if (selects.Count > 0)
                {
                    result = translator.Translate(selects[0]);
                    if (selects.Count > 1)
                    {
                        var list = new List<TranslateResult>();
                        for (var i = 1; i < selects.Count; i++)
                        {
                            list.Add(translator.Translate((selects[i])));
                        }

                        result.NestedResults = list.AsReadOnly();
                    }
                }
                else
                {
                    result = translator.Translate(expression);
                }

                return result;
            }
            catch (Exception ex)
            {
                throw new TranslateException(expression, ex);
            }
        }

        private TranslateOptions GetTranslateOptions()
        {
            var section = ConfigurationUnity.GetSection<TranslatorConfigurationSection>();
            return (section != null ? section.Options : TranslateOptions.Default).Clone();
        }

        private CacheParsingOptions GetCacheOptions()
        {
            return new CacheParsingOptions(ContextOptions.CacheParsing, ContextOptions.CacheParsingTimes, ContextOptions.CachePrefix);
        }

        /// <summary>
        /// 返回一个元组，Item1 表示是否为异步方法，Item2 表示是否有分页参数。
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        private Tuple<bool, bool> GetMethodAttributes(MethodInfo method)
        {
            return attrCache.GetOrAdd(method.GetParameters(), ps =>
                {
                    var isAsync = false;
                    var isSegment = false;
                    foreach (var p in ps)
                    {
                        if (isAsync && isSegment)
                        {
                            break;
                        }

                        if (p.ParameterType == typeof(CancellationToken))
                        {
                            isAsync = true;
                        }
                        else if (typeof(IDataSegment).IsAssignableFrom(p.ParameterType))
                        {
                            isSegment = true;
                        }
                    }

                    return Tuple.Create(isAsync, isSegment);
                });
        }

        private class ParametersComparer : IEqualityComparer<ParameterInfo[]>
        {
            bool IEqualityComparer<ParameterInfo[]>.Equals(ParameterInfo[] x, ParameterInfo[] y)
            {
                if (x.Length != y.Length)
                {
                    return false;
                }

                for (int i = 0, n = x.Length; i < n; i++)
                {
                    if (x[i].ParameterType != y[i].ParameterType)
                    {
                        return false;
                    }
                }

                return true;
            }

            int IEqualityComparer<ParameterInfo[]>.GetHashCode(ParameterInfo[] obj)
            {
                if (obj.Length == 0)
                {
                    return 0;
                }

                var hash = obj[0].ParameterType.GetHashCode();
                if (obj.Length == 1)
                {
                    return hash;
                }

                for (int i = 1, n = obj.Length; i < n; i++)
                {
                    hash ^= obj[i].ParameterType.GetHashCode();
                }

                return hash;
            }
        }
    }
}
