// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Entity.Internal;
using Microsoft.Data.Entity.Query.ExpressionTreeVisitors;
using Microsoft.Data.Entity.Query.ResultOperators;
using Microsoft.Data.Entity.Storage;
using Microsoft.Data.Entity.Utilities;
using Microsoft.Framework.Caching.Memory;
using JetBrains.Annotations;
using Remotion.Linq;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.StreamedData;
using Remotion.Linq.Parsing;
using Remotion.Linq.Parsing.ExpressionTreeVisitors.Transformation;
using Remotion.Linq.Parsing.ExpressionTreeVisitors.TreeEvaluation;
using Remotion.Linq.Parsing.Structure;
using Remotion.Linq.Parsing.Structure.ExpressionTreeProcessors;
using Remotion.Linq.Parsing.Structure.NodeTypeProviders;

namespace Microsoft.Data.Entity.Query
{
    public class CompiledQueryCache : ICompiledQueryCache
    {
        public const string CompiledQueryParameterPrefix = "__";

        private class CompiledQuery
        {
            public Type ResultItemType;
            public Delegate Executor;
        }

        private readonly IMemoryCache _memoryCache;

        public CompiledQueryCache([NotNull] IMemoryCache memoryCache)
        {
            Check.NotNull(memoryCache, nameof(memoryCache));

            _memoryCache = memoryCache;
        }

        public virtual TResult Execute<TResult>(
            Expression query, IDataStore dataStore, QueryContext queryContext)
        {
            Check.NotNull(query, nameof(query));
            Check.NotNull(dataStore, nameof(dataStore));
            Check.NotNull(queryContext, nameof(queryContext));

            var compiledQuery
                = GetOrAdd(query, queryContext, dataStore, isAsync: false, compiler: (q, ds) =>
                    {
                        var queryModel = CreateQueryParser().GetParsedQuery(q);

                        var streamedSequenceInfo
                            = queryModel.GetOutputDataInfo() as StreamedSequenceInfo;

                        var resultItemType
                            = streamedSequenceInfo?.ResultItemType ?? typeof(TResult);

                        var executor
                            = CompileQuery(ds, DataStore.CompileQueryMethod, resultItemType, queryModel);

                        return new CompiledQuery
                        {
                            ResultItemType = resultItemType,
                            Executor = executor
                        };
                    });

            return
                typeof(TResult) == compiledQuery.ResultItemType
                    ? ((Func<QueryContext, IEnumerable<TResult>>)compiledQuery.Executor)(queryContext).First()
                    : ((Func<QueryContext, TResult>)compiledQuery.Executor)(queryContext);
        }

        public virtual IAsyncEnumerable<TResult> ExecuteAsync<TResult>(
            Expression query, IDataStore dataStore, QueryContext queryContext)
        {
            Check.NotNull(query, nameof(query));
            Check.NotNull(dataStore, nameof(dataStore));
            Check.NotNull(queryContext, nameof(queryContext));

            var compiledQuery
                = GetOrAdd(query, queryContext, dataStore, isAsync: true, compiler: (q, ds) =>
                    {
                        var queryModel = CreateQueryParser().GetParsedQuery(q);

                        var executor
                            = CompileQuery(ds, DataStore.CompileAsyncQueryMethod, typeof(TResult), queryModel);

                        return new CompiledQuery
                        {
                            ResultItemType = typeof(TResult),
                            Executor = executor
                        };
                    });

            return ((Func<QueryContext, IAsyncEnumerable<TResult>>)compiledQuery.Executor)(queryContext);
        }

        public virtual Task<TResult> ExecuteAsync<TResult>(
            Expression query, IDataStore dataStore, QueryContext queryContext, CancellationToken cancellationToken)
        {
            Check.NotNull(query, nameof(query));
            Check.NotNull(dataStore, nameof(dataStore));
            Check.NotNull(queryContext, nameof(queryContext));

            var compiledQuery
                = GetOrAdd(query, queryContext, dataStore, isAsync: true, compiler: (q, ds) =>
                    {
                        var queryModel = CreateQueryParser().GetParsedQuery(q);

                        var executor
                            = CompileQuery(ds, DataStore.CompileAsyncQueryMethod, typeof(TResult), queryModel);

                        return new CompiledQuery
                        {
                            ResultItemType = typeof(TResult),
                            Executor = executor
                        };
                    });

            return ((Func<QueryContext, IAsyncEnumerable<TResult>>)compiledQuery.Executor)(queryContext)
                .First(cancellationToken);
        }

        private CompiledQuery GetOrAdd(
            Expression query,
            QueryContext queryContext,
            IDataStore dataStore,
            bool isAsync,
            Func<Expression, IDataStore, CompiledQuery> compiler)
        {
            var parameterizedQuery
                = ParameterExtractingExpressionTreeVisitor
                    .ExtractParameters(query, queryContext);

            var cacheKey
                = dataStore.Model.GetHashCode().ToString()
                  + isAsync
                  + new ExpressionStringBuilder()
                      .Build(query);

            var compiledQuery
                = _memoryCache.GetOrSet(
                    cacheKey,
                    Tuple.Create(parameterizedQuery, dataStore),
                    c =>
                        {
                            var tuple = (Tuple<Expression, IDataStore>)c.State;

                            return compiler(tuple.Item1, tuple.Item2);
                        });

            return compiledQuery;
        }

        private class ParameterExtractingExpressionTreeVisitor : ExpressionTreeVisitorBase
        {
            public static Expression ExtractParameters(Expression expressionTree, QueryContext queryContext)
            {
                var functionEvaluationDisabledExpression = new FunctionEvaluationDisablingVisitor().VisitExpression(expressionTree);
                var partialEvaluationInfo = EvaluatableTreeFindingExpressionTreeVisitor.Analyze(functionEvaluationDisabledExpression);
                var visitor = new ParameterExtractingExpressionTreeVisitor(partialEvaluationInfo, queryContext);

                return visitor.VisitExpression(functionEvaluationDisabledExpression);
            }

            private readonly PartialEvaluationInfo _partialEvaluationInfo;
            private readonly QueryContext _queryContext;

            private ParameterExtractingExpressionTreeVisitor(
                PartialEvaluationInfo partialEvaluationInfo, QueryContext queryContext)
            {
                _partialEvaluationInfo = partialEvaluationInfo;
                _queryContext = queryContext;
            }

            protected override Expression VisitMethodCallExpression(MethodCallExpression methodCallExpression)
            {
                if (methodCallExpression.Method.IsGenericMethod)
                {
                    var methodInfo = methodCallExpression.Method.GetGenericMethodDefinition();

                    if (ReferenceEquals(methodInfo, QueryExtensions.PropertyMethodInfo)
                        || ReferenceEquals(methodInfo, QueryExtensions.ValueBufferPropertyMethodInfo))
                    {
                        return methodCallExpression;
                    }
                }

                return base.VisitMethodCallExpression(methodCallExpression);
            }

            public override Expression VisitExpression(Expression expression)
            {
                if (expression == null)
                {
                    return null;
                }

                if (expression.NodeType == ExpressionType.Lambda
                    || !_partialEvaluationInfo.IsEvaluatableExpression(expression))
                {
                    return base.VisitExpression(expression);
                }

                var e = expression;

                if (expression.NodeType == ExpressionType.Convert)
                {
                    var unaryExpression = (UnaryExpression)expression;

                    if ((unaryExpression.Type.IsNullableType()
                         && !unaryExpression.Operand.Type.IsNullableType())
                        || unaryExpression.Type == typeof(object))
                    {
                        e = unaryExpression.Operand;
                    }
                }

                if (e.NodeType != ExpressionType.Constant
                    && !typeof(IQueryable).GetTypeInfo().IsAssignableFrom(e.Type.GetTypeInfo()))
                {
                    try
                    {
                        string parameterName;

                        var parameterValue = Evaluate(e, out parameterName);

                        var compilerPrefixIndex = parameterName.LastIndexOf(">");
                        if (compilerPrefixIndex != -1)
                        {
                            parameterName = parameterName.Substring(compilerPrefixIndex + 1);
                        }

                        parameterName
                            = $"{CompiledQueryParameterPrefix}{parameterName}_{_queryContext.ParameterValues.Count}";

                        _queryContext.ParameterValues.Add(parameterName, parameterValue);

                        return e.Type == expression.Type
                            ? Expression.Parameter(e.Type, parameterName)
                            : (Expression)Expression.Convert(
                                Expression.Parameter(e.Type, parameterName),
                                expression.Type);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            Strings.ExpressionParameterizationException(expression),
                            exception);
                    }
                }

                return expression;
            }

            private static object Evaluate(Expression expression, out string parameterName)
            {
                parameterName = null;

                if (expression == null)
                {
                    return null;
                }

                switch (expression.NodeType)
                {
                    case ExpressionType.MemberAccess:
                    {
                        var memberExpression = (MemberExpression)expression;
                        var @object = Evaluate(memberExpression.Expression, out parameterName);

                        var fieldInfo = memberExpression.Member as FieldInfo;

                        if (fieldInfo != null)
                        {
                            parameterName = parameterName != null
                                ? parameterName + "_" + fieldInfo.Name
                                : fieldInfo.Name;

                            try
                            {
                                return fieldInfo.GetValue(@object);
                            }
                            catch
                            {
                                // Try again when we compile the delegate
                            }
                        }

                        var propertyInfo = memberExpression.Member as PropertyInfo;

                        if (propertyInfo != null)
                        {
                            parameterName = parameterName != null
                                ? parameterName + "_" + propertyInfo.Name
                                : propertyInfo.Name;

                            try
                            {
                                return propertyInfo.GetValue(@object);
                            }
                            catch
                            {
                                // Try again when we compile the delegate
                            }
                        }

                        break;
                    }
                    case ExpressionType.Constant:
                    {
                        return ((ConstantExpression)expression).Value;
                    }
                    case ExpressionType.Call:
                    {
                        parameterName = ((MethodCallExpression)expression).Method.Name;

                        break;
                    }
                }

                if (parameterName == null)
                {
                    parameterName = "p";
                }

                return
                    Expression.Lambda<Func<object>>(
                        Expression.Convert(expression, typeof(object)))
                        .Compile()
                        .Invoke();
            }
        }

        private static Delegate CompileQuery(
            IDataStore dataStore, MethodInfo compileMethodInfo, Type resultItemType, QueryModel queryModel)
        {
            try
            {
                return (Delegate)compileMethodInfo
                    .MakeGenericMethod(resultItemType)
                    .Invoke(dataStore, new object[] { queryModel });
            }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();

                throw;
            }
        }

        private static QueryParser CreateQueryParser()
            => new QueryParser(
                new ExpressionTreeParser(
                    CreateNodeTypeProvider(),
                    new CompoundExpressionTreeProcessor(new IExpressionTreeProcessor[]
                        {
                            new PartialEvaluatingExpressionTreeProcessor(),
                            new FunctionEvaluationEnablingProcessor(),
                            new TransformingExpressionTreeProcessor(ExpressionTransformerRegistry.CreateDefault())
                        })));

        private class FunctionEvaluationEnablingProcessor : IExpressionTreeProcessor
        {
            public Expression Process(Expression expressionTree)
            {
                var newExpressionTree = new FunctionEvaluationEnablingVisitor().VisitExpression(expressionTree);

                return newExpressionTree;
            }
        }

        private class FunctionEvaluationDisablingVisitor : ExpressionTreeVisitorBase
        {
            public static readonly MethodInfo DbContextSetMethodInfo
                = typeof(DbContext).GetTypeInfo().GetDeclaredMethod("Set");

            private MethodInfo[] _nonDeterministicMethodInfos;

            public FunctionEvaluationDisablingVisitor()
            {
                _nonDeterministicMethodInfos = new MethodInfo[]
                {
                    typeof(Guid).GetTypeInfo().GetDeclaredMethod("NewGuid"),
                    typeof(DateTime).GetTypeInfo().GetDeclaredProperty("Now").GetMethod,
                };
            }

            protected override Expression VisitMethodCallExpression(MethodCallExpression expression)
            {
                if (expression.Method.IsGenericMethod)
                {
                    var genericMethodDefinition = expression.Method.GetGenericMethodDefinition();
                    if (ReferenceEquals(genericMethodDefinition, QueryExtensions.PropertyMethodInfo)
                        || ReferenceEquals(genericMethodDefinition, DbContextSetMethodInfo))
                    {
                        return base.VisitMethodCallExpression(expression);
                    }
                }

                if (IsQueryable(expression.Object) || IsQueryable(expression.Arguments.FirstOrDefault()))
                {
                    return base.VisitMethodCallExpression(expression);
                }

                var newObject = VisitExpression(expression.Object);
                var newArguments = VisitAndConvert(expression.Arguments, "VisitMethodCallExpression");

                var newMethodCall = newObject != expression.Object || newArguments != expression.Arguments
                    ? Expression.Call(newObject, expression.Method, newArguments)
                    : expression;

                return _nonDeterministicMethodInfos.Contains(expression.Method)
                    ? (Expression)new MethodCallEvaluationPreventingExpression(newMethodCall)
                    : newMethodCall;
            }

            private bool IsQueryable(Expression expression)
            {
                if (expression == null)
                {
                    return false;
                }

                return typeof(IQueryable).GetTypeInfo().IsAssignableFrom(expression.Type.GetTypeInfo());
            }

            protected override Expression VisitMemberExpression(MemberExpression expression)
            {
                var propertyInfo = expression.Member as PropertyInfo;

                return propertyInfo != null && _nonDeterministicMethodInfos.Contains(propertyInfo.GetMethod)
                    ? (Expression)new PropertyEvaluationPreventingExpression(expression)
                    : expression;
            }
        }

        private class FunctionEvaluationEnablingVisitor : ExpressionTreeVisitorBase
        {
            protected override Expression VisitExtensionExpression(ExtensionExpression expression)
            {
                var methodCallWrapper = expression as MethodCallEvaluationPreventingExpression;
                if (methodCallWrapper != null)
                {
                    return VisitExpression(methodCallWrapper.MethodCall);
                }

                var propertyWrapper = expression as PropertyEvaluationPreventingExpression;
                if (propertyWrapper != null)
                {
                    return VisitExpression(propertyWrapper.MemberExpression);
                }

                return base.VisitExtensionExpression(expression);
            }

            protected override Expression VisitSubQueryExpression(SubQueryExpression expression)
            {
                expression.QueryModel.TransformExpressions(VisitExpression);

                return expression;
            }
        }

        private class MethodCallEvaluationPreventingExpression : ExtensionExpression
        {
            public virtual MethodCallExpression MethodCall { get; private set; }

            public MethodCallEvaluationPreventingExpression([NotNull] MethodCallExpression argument)
                : base(argument.Type)
            {
                Check.NotNull(argument, nameof(argument));

                MethodCall = argument;
            }

            public override bool CanReduce
            {
                get
                {
                    return true;
                }
            }

            public override Expression Reduce()
            {
                return MethodCall;
            }

            protected override Expression VisitChildren(ExpressionTreeVisitor visitor)
            {
                var newObject = visitor.VisitExpression(MethodCall.Object);
                var newArguments = visitor.VisitAndConvert(MethodCall.Arguments, "VisitChildren");

                if (newObject != MethodCall.Object
                    || newArguments != MethodCall.Arguments)
                {
                    return new MethodCallEvaluationPreventingExpression(
                        Call(newObject, MethodCall.Method, newArguments));
                }

                return this;
            }
        }

        private class PropertyEvaluationPreventingExpression : ExtensionExpression
        {
            public virtual MemberExpression MemberExpression { get; private set; }

            public PropertyEvaluationPreventingExpression([NotNull] MemberExpression argument)
                : base(argument.Type)
            {
                MemberExpression = argument;
            }

            public override bool CanReduce
            {
                get
                {
                    return true;
                }
            }

            public override Expression Reduce()
            {
                return MemberExpression;
            }

            protected override Expression VisitChildren(ExpressionTreeVisitor visitor)
            {
                var newExpression = visitor.VisitExpression(MemberExpression.Expression);

                if (newExpression != MemberExpression.Expression)
                {
                    return new PropertyEvaluationPreventingExpression(
                        Property(newExpression, MemberExpression.Member.Name));
                }

                return this;
            }
        }

        private static CompoundNodeTypeProvider CreateNodeTypeProvider()
        {
            var searchedTypes
                = typeof(MethodInfoBasedNodeTypeRegistry)
                    .GetTypeInfo()
                    .Assembly
                    .DefinedTypes
                    .Select(ti => ti.AsType())
                    .ToList();

            var methodInfoBasedNodeTypeRegistry
                = MethodInfoBasedNodeTypeRegistry.CreateFromTypes(searchedTypes);

            methodInfoBasedNodeTypeRegistry
                .Register(QueryAnnotationExpressionNode.SupportedMethods, typeof(QueryAnnotationExpressionNode));

            methodInfoBasedNodeTypeRegistry
                .Register(IncludeExpressionNode.SupportedMethods, typeof(IncludeExpressionNode));

            methodInfoBasedNodeTypeRegistry
                .Register(ThenIncludeExpressionNode.SupportedMethods, typeof(ThenIncludeExpressionNode));

            var innerProviders
                = new INodeTypeProvider[]
                    {
                        methodInfoBasedNodeTypeRegistry,
                        MethodNameBasedNodeTypeRegistry.CreateFromTypes(searchedTypes)
                    };

            return new CompoundNodeTypeProvider(innerProviders);
        }
    }
}
