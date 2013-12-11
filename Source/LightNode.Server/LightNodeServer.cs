﻿using System;
using System.Linq.Expressions;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.IO;

namespace LightNode.Server
{
    internal class MessageContract
    {
        public string MethodName { get; set; }

        public ParameterInfo[] Arguments { get; set; }

        public Type ReturnType { get; set; }

        public MessageContractBodyType MessageContractBodyType { get; set; }

        public Func<object[], object> MethodFuncBody { get; set; } // 1

        public Func<object[], Task> MethodAsyncFuncBody { get; set; } // 2

        public Action<object[]> MethodActionBody { get; set; } // 3
        public Func<object[], Task> MethodAsyncActionBody { get; set; } // 4
    }
    internal enum MessageContractBodyType
    {
        Func = 1,
        AsyncFunc = 2,
        Action = 3,
        AsyncAction = 4
    }

    public static class LightNodeServer
    {
        // {Class,Method} => MessageContract
        readonly static Dictionary<Tuple<string, string>, MessageContract> handlers = new Dictionary<Tuple<string, string>, MessageContract>();
        readonly static Dictionary<Type, Func<object, object>> taskResultExtractorCache = new Dictionary<Type, Func<object, object>>();

        public static void RegisterHandler(Assembly[] hostAssemblies)
        {
            var contractTypes = hostAssemblies
                .SelectMany(x => x.GetTypes())
                .Where(x => typeof(ILightNodeContract).IsAssignableFrom(x));

            // TODO:validation, duplicate entry, non support arguments.

            // TODO:faster way, make parallel
            foreach (var classType in contractTypes)
            {
                var className = classType.Name;
                foreach (var methodInfo in classType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var contract = new MessageContract();

                    var methodName = methodInfo.Name;

                    contract.MethodName = methodName;
                    contract.Arguments = methodInfo.GetParameters();
                    contract.ReturnType = methodInfo.ReturnType;

                    if (typeof(Task).IsAssignableFrom(contract.ReturnType))
                    {
                        // (object[] args) => new X().M((T1)args[0], (T2)args[1])...
                        var args = Expression.Parameter(typeof(object[]), "args");

                        var parameters = methodInfo.GetParameters()
                            .Select((x, i) => Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), x.ParameterType))
                            .ToArray();

                        var lambda = Expression.Lambda<Func<object[], Task>>(
                            Expression.Call(
                                Expression.New(classType),
                                methodInfo,
                                parameters),
                            args);

                        if (contract.ReturnType.IsGenericType && contract.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                        {
                            contract.MessageContractBodyType = MessageContractBodyType.AsyncFunc;
                            contract.MethodAsyncFuncBody = lambda.Compile();

                            // (object task) => (object)((Task<>).Result)
                            var taskParameter = Expression.Parameter(typeof(object), "task");
                            var resultLambda = Expression.Lambda<Func<object, object>>(
                                Expression.Convert(
                                    Expression.Property(
                                        Expression.Convert(taskParameter, contract.ReturnType),
                                        "Result"),
                                    typeof(object)),
                                taskParameter);

                            taskResultExtractorCache[contract.ReturnType] = resultLambda.Compile();
                        }
                        else
                        {
                            contract.MessageContractBodyType = MessageContractBodyType.AsyncAction;
                            contract.MethodAsyncActionBody = lambda.Compile();
                        }
                    }
                    else if (contract.ReturnType == typeof(void))
                    {
                        // (object[] args) => { new X().M((T1)args[0], (T2)args[1])... }
                        var args = Expression.Parameter(typeof(object[]), "args");

                        var parameters = methodInfo.GetParameters()
                            .Select((x, i) => Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), x.ParameterType))
                            .ToArray();

                        var lambda = Expression.Lambda<Action<object[]>>(
                            Expression.Call(
                                Expression.New(classType),
                                methodInfo,
                                parameters),
                            args);

                        contract.MessageContractBodyType = MessageContractBodyType.Action;
                        contract.MethodActionBody = lambda.Compile();
                    }
                    else
                    {
                        // (object[] args) => (object)new X().M((T1)args[0], (T2)args[1])...
                        var args = Expression.Parameter(typeof(object[]), "args");

                        var parameters = methodInfo.GetParameters()
                            .Select((x, i) => Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), x.ParameterType))
                            .ToArray();

                        var lambda = Expression.Lambda<Func<object[], object>>(
                            Expression.Convert(
                                Expression.Call(
                                    Expression.New(classType),
                                    methodInfo,
                                    parameters)
                            , typeof(object)),
                            args);

                        contract.MessageContractBodyType = MessageContractBodyType.Func;
                        contract.MethodFuncBody = lambda.Compile();
                    }

                    handlers.Add(Tuple.Create(className, methodName), contract);
                }
            }
        }

        public static async Task HandleRequest(IDictionary<string, object> environment)
        {
            var path = environment["owin.RequestPath"] as string;

            // TODO:requestmethod is POST
            var keyBase = path.Trim('/').Split('/');
            if (keyBase.Length != 2) throw new InvalidOperationException(); // TODO:Exception Handling

            // {ClassName, MethodName}
            var key = Tuple.Create(keyBase[0], keyBase[1]);

            MessageContract handler;
            if (handlers.TryGetValue(key, out handler))
            {
                ILookup<string, string> requestParameter;
                // TODO:GET is from QueryString
                using (var sr = new StreamReader((environment["owin.RequestBody"] as Stream)))
                {
                    var str = await sr.ReadToEndAsync();
                    requestParameter = str.Split('&')
                        .Select(xs => xs.Split('='))
                        .Where(xs => xs.Length == 2)
                        .ToLookup(xs => xs[0], xs => xs[1]);
                }

                var methodParameters = handler.Arguments.Select(x =>
                {
                    var values = requestParameter[x.Name];
                    var count = values.Count();
                    if (count == 0)
                    {
                        if (x.IsOptional)
                        {
                            return x.DefaultValue;
                        }
                        else
                        {
                            throw new InvalidOperationException(); // TODO:Exception Handling
                        }
                    }
                    else if (count == 1)
                    {
                        var conv = AllowRequestType.GetConverter(x.ParameterType);
                        if (conv == null) throw new InvalidOperationException(); // TODO:Exception Handling
                        return conv(values.First());
                    }
                    else // Array
                    {
                        if (!x.ParameterType.IsArray) throw new InvalidOperationException(); // TODO:Exception Handling
                        var conv = AllowRequestType.GetArrayConverter(x.ParameterType);
                        if (conv == null) throw new InvalidOperationException(); // TODO:Exception Handling
                        return conv(values);
                    }
                })
                .ToArray();

                bool isVoid = true;
                object result = null;
                switch (handler.MessageContractBodyType)
                {
                    case MessageContractBodyType.Action:
                        handler.MethodActionBody(methodParameters);
                        break;
                    case MessageContractBodyType.Func:
                        isVoid = false;
                        result = handler.MethodFuncBody(methodParameters);
                        break;
                    case MessageContractBodyType.AsyncAction:
                        var actionTask = handler.MethodAsyncActionBody(methodParameters);
                        await actionTask;
                        break;
                    case MessageContractBodyType.AsyncFunc:
                        isVoid = false;
                        var funcTask = handler.MethodAsyncFuncBody(methodParameters);
                        await funcTask;
                        var extractor = taskResultExtractorCache[funcTask.GetType()];
                        result = extractor(funcTask);
                        break;
                    default:
                        throw new InvalidOperationException("critical:register code is broken");
                }

                // TODO:
                // set response
                // exception handling
            }
            else
            {
                // TODO:return 404 Message
            }
        }



        
    }



    public interface ILightNodeContract
    {

    }


    public class ContractOptionAttribute : Attribute
    {
        public string Name { get; private set; }

        public AcceptVerbs AcceptVerb { get; set; }
    }

    [Flags]
    public enum AcceptVerbs
    {
        Get, Post
    }

    public interface ISerializer
    {

    }



}